using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Domain;
using Quicker.Payroll.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Payroll.Application;

/// <summary>
/// Payroll runs (A-181, POSTING_RULES §8 "Payroll run"): one period of one company. Calculation takes every employee
/// employed in the period, prorates the basic and the fixed earnings over a thirty-day month for days not employed
/// and unpaid leave, applies the components in order (percentages of the basic, then of the gross), rounds each item
/// to the currency under the company's rounding mode and nets the deductions. Submission goes through the workflow
/// engine when a definition is active; posting writes one journal (salaries and employer contributions by component
/// against the liabilities withheld and the net pay as a payable per employee); payment settles each employee's
/// payable from a bank account through Banking; reversal mirrors the journal and the payables while nothing is paid.
/// </summary>
public sealed class RunService(
    PayrollDbContext db,
    ComponentService components,
    ICompanyDirectory companies,
    IPartnerDirectory partners,
    IDimensionSets dimensionSets,
    IPostingService posting,
    IPayables payables,
    IPayments payments,
    IBankAccountDirectory bankAccounts,
    IWorkflowEngine workflow,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = PayrollDocumentTypes.Run;
    public const string AuditEntity = PayrollDocumentTypes.Run;

    /// <summary>A pay month is thirty days whatever the calendar says (the convention of the region's labour practice), so a day of unpaid leave costs the same in February and in March.</summary>
    public const int DaysInMonth = 30;

    public async Task<IReadOnlyList<PayrollRunSummary>> ListAsync(Guid companyId, string? status, int? year, CancellationToken cancellationToken)
    {
        var query = db.Runs.AsNoTracking().Include(static r => r.Lines.OrderBy(static l => l.LineNo)).ThenInclude(static l => l.Items.OrderBy(static i => i.SortOrder)).Where(r => r.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (year is { } y)
        {
            query = query.Where(r => r.PeriodYear == y);
        }

        var runs = await query.OrderByDescending(static r => r.PeriodYear).ThenByDescending(static r => r.PeriodMonth).ThenByDescending(static r => r.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var currency = company?.FunctionalCurrency.Code ?? string.Empty;
        return runs.Select(r => Map(r, currency)).ToList();
    }

    public async Task<Result<PayrollRunSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(id, cancellationToken);
        return run is null ? Error.NotFound(AuditEntity, id) : await MapAsync(run, cancellationToken);
    }

    public async Task<Result<PayrollRunSummary>> CreateAsync(CreateRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Month is < 1 or > 12 || request.Year is < 2000 or > 2100)
        {
            return Error.Validation("payroll_run.period_invalid", "The period is a month of a year.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var start = new DateOnly(request.Year, request.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        if (await db.Runs.AnyAsync(r => r.CompanyId == request.CompanyId && r.PeriodYear == request.Year && r.PeriodMonth == request.Month && r.Status != PayrollRunStatuses.Cancelled && r.Status != PayrollRunStatuses.Reversed, cancellationToken))
        {
            return Error.Conflict("payroll_run.period_exists", "The company already has a run for that month.").WithWhy(("year", request.Year), ("month", request.Month));
        }

        var postingDate = request.PostingDate ?? end;
        var payDate = request.PayDate ?? end;
        if (postingDate < start)
        {
            return Error.Validation("payroll_run.posting_date", "The run posts on or after the first day of its period.");
        }

        var now = clock.UtcNow;
        var run = new PayrollRun
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            PeriodYear = request.Year,
            PeriodMonth = request.Month,
            PeriodStart = start,
            PeriodEnd = end,
            PostingDate = postingDate,
            PayDate = payDate,
            Notes = Validation.Trim(request.Notes),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "PRL", "PRL-{yyyy}-{seq:3}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, postingDate, run.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        run.Number = number.Value.Text;
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.Created, After: new { run.PeriodYear, run.PeriodMonth, run.PostingDate, run.PayDate }, CompanyId: run.CompanyId), cancellationToken);
        var calculated = await CalculateCoreAsync(run, company, cancellationToken);
        return calculated.IsFailure ? calculated.Error! : await MapAsync(run, cancellationToken);
    }

    public async Task<Result<PayrollRunSummary>> CalculateAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status is not (PayrollRunStatuses.Draft or PayrollRunStatuses.Calculated))
        {
            return Error.Conflict("payroll_run.not_editable", "Only a draft or calculated run is recalculated.").WithWhy(("status", run.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken))!;
        var calculated = await CalculateCoreAsync(run, company, cancellationToken);
        return calculated.IsFailure ? calculated.Error! : await MapAsync(run, cancellationToken);
    }

    /// <summary>Sends the run for approval when a workflow definition is active for payroll runs; approves it at once otherwise.</summary>
    public async Task<Result<PayrollRunSummary>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status != PayrollRunStatuses.Calculated)
        {
            return Error.Conflict("payroll_run.not_calculated", "Calculate the run before submitting it.").WithWhy(("status", run.Status));
        }

        if (run.Lines.Count == 0)
        {
            return Error.Conflict("payroll_run.no_employees", "No employee is employed in this period.");
        }

        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(await SubjectAsync(run, cancellationToken), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.Pending)
            {
                run.Status = PayrollRunStatuses.PendingApproval;
                run.ApprovalRequestId = outcome.Value.RequestId;
                run.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.StateChanged, After: new { status = run.Status, approvalRequestId = run.ApprovalRequestId }, CompanyId: run.CompanyId), cancellationToken);
                return await MapAsync(run, cancellationToken);
            }
        }

        run.Status = PayrollRunStatuses.Approved;
        run.RejectionReason = null;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.Approved, After: new { status = run.Status }, CompanyId: run.CompanyId), cancellationToken);
        return await MapAsync(run, cancellationToken);
    }

    /// <summary>The workflow's answer: approval readies the run for posting, rejection sends it back calculated with the reason, a cancelled or expired request likewise.</summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var run = await LoadAsync(decision.EntityId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, decision.EntityId);
        }

        if (run.Status != PayrollRunStatuses.PendingApproval || run.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("payroll_run.not_pending", "The run is no longer awaiting this approval request.").WithWhy(("status", run.Status));
        }

        var approved = decision.Status == WorkflowDecisions.Approved;
        run.Status = approved ? PayrollRunStatuses.Approved : PayrollRunStatuses.Calculated;
        run.RejectionReason = decision.Status == WorkflowDecisions.Rejected ? decision.Comment ?? "Not approved." : null;
        run.ApprovalRequestId = null;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, approved ? AuditActions.Approved : AuditActions.Rejected, After: new { status = run.Status, reason = run.RejectionReason, decision.DecidedBy }, CompanyId: run.CompanyId), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PayrollRunSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status != PayrollRunStatuses.Approved)
        {
            return Error.Conflict("payroll_run.not_approved", "Only an approved run is posted.").WithWhy(("status", run.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken))!;
        var currency = company.FunctionalCurrency.Code;
        var lines = new List<PostingLine>();
        foreach (var line in run.Lines)
        {
            // Net pay goes to the employee's supplier account, which the bank pays like any other payable.
            var supplier = await partners.EnsureSupplierAsync(run.CompanyId, line.PartnerId, SupplierPurposes.Payment, cancellationToken);
            if (supplier.IsFailure)
            {
                return Error.Conflict("payroll_run.employee_not_payable", $"Employee {line.EmployeeNumber} has no active supplier account in the company to be paid through; add one on the partner.").WithWhy(("employee", line.EmployeeNumber), ("reason", supplier.Error!.Code));
            }

            var dimensions = line.DimensionSetId is { } setId ? await dimensionSets.GetAsync(setId, cancellationToken) : null;
            foreach (var item in line.Items.Where(static i => i.Amount != 0m))
            {
                var keys = new PostingKeys(DocumentType, PayComponentId: item.ComponentId);
                var label = item.ComponentName;
                switch (item.Kind)
                {
                    case PayComponentKinds.Earning:
                        lines.Add(new PostingLine(AccountRoles.PayrollExpense, item.Amount, keys, Dimensions: dimensions, PartnerId: line.PartnerId, Description: label));
                        break;
                    case PayComponentKinds.Deduction:
                        lines.Add(new PostingLine(AccountRoles.PayrollLiability, -item.Amount, keys, Dimensions: dimensions, PartnerId: line.PartnerId, Description: label));
                        break;
                    default:
                        lines.Add(new PostingLine(AccountRoles.PayrollExpense, item.Amount, keys, Dimensions: dimensions, PartnerId: line.PartnerId, Description: label));
                        lines.Add(new PostingLine(AccountRoles.PayrollLiability, -item.Amount, keys, Dimensions: dimensions, PartnerId: line.PartnerId, Description: label));
                        break;
                }
            }

            if (line.Net > 0m)
            {
                lines.Add(new PostingLine(AccountRoles.AP, -line.Net, new PostingKeys(DocumentType, PartnerPostingGroupId: supplier.Value.PostingGroupId), Dimensions: dimensions, PartnerId: line.PartnerId,
                    SubledgerType: SubledgerTypes.Payables, SubledgerRef: run.Id, DueDate: run.PayDate, Description: LocalizedText.Bilingual($"Net pay {line.EmployeeNumber}", $"صافي الراتب {line.EmployeeNumber}")));
            }
        }

        if (lines.Count == 0)
        {
            return Error.Conflict("payroll_run.nothing_to_post", "The run has no amounts to post.");
        }

        var period = new DateTime(run.PeriodYear, run.PeriodMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var description = LocalizedText.Bilingual($"Payroll {run.Number} for {period.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}", $"رواتب {run.Number} لشهر {period.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ar"))}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "payroll", DocumentType, run.Id, run.PostingDate, currency, lines, run.Number, run.PeriodEnd, description, IdempotencyKey: $"payroll_run:{run.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        foreach (var line in run.Lines.Where(static l => l.Net > 0m))
        {
            var item = await payables.OpenAsync(new NewOpenItem(run.CompanyId, line.PartnerId, PayableKinds.Invoice, DocumentType, run.Id, run.Number, line.LineNo, line.EmployeeNumber, run.PostingDate, run.PeriodEnd, run.PayDate, null, 0m, currency, line.Net, line.Net, 1m, posted.Value.EntryId, line.BranchId, line.DimensionSetId), cancellationToken);
            line.OpenItemId = item.Id;
        }

        run.JournalEntryId = posted.Value.EntryId;
        run.Status = PayrollRunStatuses.Posted;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.Posted, After: new { status = run.Status, journalEntryId = run.JournalEntryId, run.TotalGross, run.TotalDeductions, run.TotalEmployer, run.TotalNet }, CompanyId: run.CompanyId), cancellationToken);
        return await MapAsync(run, cancellationToken);
    }

    /// <summary>Pays every employee's net from one bank account on a day: one Banking payment per employee settling the payable the run opened.</summary>
    public async Task<Result<PayrollRunSummary>> PayAsync(Guid id, PayRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status != PayrollRunStatuses.Posted)
        {
            return Error.Conflict("payroll_run.not_posted", "Only a posted run is paid.").WithWhy(("status", run.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken))!;
        var bank = await bankAccounts.FindAsync(request.BankAccountId, cancellationToken);
        if (bank is null || bank.CompanyId != run.CompanyId)
        {
            return Error.NotFound("bank_account", request.BankAccountId);
        }

        if (!string.Equals(bank.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return Error.Validation("payroll_run.bank_currency", "Salaries are paid from an account in the company's currency.").WithWhy(("bank", bank.Currency), ("company", company.FunctionalCurrency.Code));
        }

        var payDate = request.PayDate ?? run.PayDate;
        // A bank account pays by transfer, a cash box or petty cash in cash; the employee's own method is what the record says they expect.
        var method = bank.Kind == BankAccountKinds.Bank ? "transfer" : "cash";
        var paidNow = 0;
        foreach (var line in run.Lines.Where(static l => l.OpenItemId is not null && l.PaymentId is null && l.Net > 0m))
        {
            var paid = await payments.PayAsync(new PayOpenItemRequest(run.CompanyId, line.PartnerId, bank.Id, line.OpenItemId!.Value, line.Net, payDate, method, Validation.Trim(request.Reference) ?? run.Number), cancellationToken);
            if (paid.IsFailure)
            {
                return paid.Error!.WithWhy(("employee", line.EmployeeNumber));
            }

            line.PaymentId = paid.Value.Id;
            line.PaymentNumber = paid.Value.Number;
            line.PaidOn = payDate;
            paidNow++;
        }

        if (paidNow == 0)
        {
            return Error.Conflict("payroll_run.nothing_to_pay", "Every employee of the run has been paid.");
        }

        if (run.Lines.All(static l => l.Net <= 0m || l.PaymentId is not null))
        {
            run.Status = PayrollRunStatuses.Paid;
        }

        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.StateChanged, After: new { status = run.Status, paid = paidNow, bank = bank.Code, payDate }, CompanyId: run.CompanyId), cancellationToken);
        return await MapAsync(run, cancellationToken);
    }

    public async Task<Result<PayrollRunSummary>> ReverseAsync(Guid id, ReverseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status != PayrollRunStatuses.Posted || run.JournalEntryId is null)
        {
            return Error.Conflict("payroll_run.not_posted", "Only a posted, unpaid run is reversed.").WithWhy(("status", run.Status));
        }

        if (run.Lines.Any(static l => l.PaymentId is not null))
        {
            return Error.Conflict("payroll_run.paid", "Employees of this run were paid; reverse their payments first.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("payroll_run.reason_required", "A reversal names its reason.");
        }

        var reversed = await posting.ReverseAsync(run.JournalEntryId.Value, request.ReversalDate, request.Reason, cancellationToken: cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var items = await payables.ReverseDocumentAsync(DocumentType, run.Id, reversed.Value.PostingDate, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error!;
        }

        run.ReversalEntryId = reversed.Value.EntryId;
        run.Status = PayrollRunStatuses.Reversed;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.Reversed, After: new { status = run.Status, reversalEntryId = run.ReversalEntryId }, Reason: request.Reason, CompanyId: run.CompanyId), cancellationToken);
        return await MapAsync(run, cancellationToken);
    }

    public async Task<Result<PayrollRunSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (run.Status is not (PayrollRunStatuses.Draft or PayrollRunStatuses.Calculated or PayrollRunStatuses.PendingApproval or PayrollRunStatuses.Approved))
        {
            return Error.Conflict("payroll_run.not_cancellable", "A posted run is reversed, not cancelled.").WithWhy(("status", run.Status));
        }

        if (run.Status == PayrollRunStatuses.PendingApproval)
        {
            var withdrawn = await workflow.CancelAsync(DocumentType, run.Id, "The run was cancelled.", cancellationToken);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }
        }

        run.Status = PayrollRunStatuses.Cancelled;
        run.ApprovalRequestId = null;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.StateChanged, After: new { status = run.Status }, CompanyId: run.CompanyId), cancellationToken);
        return await MapAsync(run, cancellationToken);
    }

    // ------------------------------------------------------------------ the calculation

    private async Task<Result> CalculateCoreAsync(PayrollRun run, CompanyInfo company, CancellationToken cancellationToken)
    {
        var basic = await components.EnsureBasicAsync(run.CompanyId, cancellationToken);
        if (basic is null)
        {
            return Error.NotFound("company", run.CompanyId);
        }

        var all = await db.Components.AsNoTracking().Where(c => c.CompanyId == run.CompanyId && c.IsActive).OrderBy(static c => c.SortOrder).ThenBy(static c => c.Code).ToListAsync(cancellationToken);
        var staff = await db.Employees.AsNoTracking().Include(static e => e.Components)
            .Where(e => e.CompanyId == run.CompanyId && e.HireDate <= run.PeriodEnd && (e.TerminationDate == null || e.TerminationDate >= run.PeriodStart))
            .OrderBy(static e => e.Number).ToListAsync(cancellationToken);
        var staffIds = staff.Select(static e => e.Id).ToList();
        var unpaidLeaves = await db.Leaves.AsNoTracking().Where(l => staffIds.Contains(l.EmployeeId) && l.Kind == LeaveKinds.Unpaid && l.Status == "approved" && l.FromDate <= run.PeriodEnd && l.ToDate >= run.PeriodStart).ToListAsync(cancellationToken);
        var rounding = new RoundingPolicy(company.RoundingMode);
        var minor = company.FunctionalCurrency.MinorUnits;
        decimal Round(decimal amount) => rounding.Round(amount, minor);

        db.RunItems.RemoveRange(run.Lines.SelectMany(static l => l.Items));
        db.RunLines.RemoveRange(run.Lines);
        run.Lines.Clear();

        var lineNo = 0;
        foreach (var employee in staff)
        {
            lineNo++;
            var partner = await partners.FindAsync(employee.PartnerId, cancellationToken);
            // The thirty-day convention: a hire on the 16th is paid from the 16th to the 30th (15 days), a leaver on the 15th the first 15; the 31st does not exist.
            var before = employee.HireDate > run.PeriodStart ? Math.Min(DaysInMonth, employee.HireDate.Day - 1) : 0;
            var after = employee.TerminationDate is { } ended && ended < run.PeriodEnd ? Math.Max(0, DaysInMonth - ended.Day) : 0;
            var unpaid = unpaidLeaves.Where(l => l.EmployeeId == employee.Id).Sum(l => Overlap(l, employee, run));
            var paidDays = Math.Max(0m, DaysInMonth - before - after - unpaid);
            var line = new PayrollRunLine
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                LineNo = lineNo,
                EmployeeId = employee.Id,
                PartnerId = employee.PartnerId,
                EmployeeNumber = employee.Number,
                EmployeeName = partner?.LegalName ?? new LocalizedText(),
                BranchId = employee.BranchId,
                DimensionSetId = employee.DimensionSetId,
                DaysInPeriod = DaysInMonth,
                PaidDays = paidDays,
                UnpaidDays = Math.Min(DaysInMonth, unpaid),
                BasicSalary = employee.BasicSalary,
            };
            var factor = paidDays / DaysInMonth;
            line.BasicAmount = Round(employee.BasicSalary * factor);

            // Which components apply: every one that applies to all, plus those the employee carries in this period, each at the employee's own figure when given.
            var applicable = new List<(PayComponent Component, decimal Amount, decimal RatePct)>();
            foreach (var component in all)
            {
                var assignment = employee.Components.FirstOrDefault(c => c.ComponentId == component.Id && (c.ValidFrom == null || c.ValidFrom <= run.PeriodEnd) && (c.ValidTo == null || c.ValidTo >= run.PeriodStart));
                if (assignment is null && !component.AppliesToAll)
                {
                    continue;
                }

                applicable.Add((component, assignment?.Amount ?? component.DefaultAmount, assignment?.RatePct ?? component.DefaultRatePct));
            }

            var order = 0;
            decimal Add(PayComponent component, decimal baseAmount, decimal ratePct, decimal amount)
            {
                var rounded = Round(amount);
                line.Items.Add(new PayrollRunItem { Id = Guid.CreateVersion7(), LineId = line.Id, ComponentId = component.Id, ComponentCode = component.Code, ComponentName = component.Name, Kind = component.Kind, Calculation = component.Calculation, BaseAmount = baseAmount, RatePct = ratePct, Amount = rounded, SortOrder = order++ });
                return rounded;
            }

            // Earnings first: the basic, fixed allowances (prorated like the basic), percentages of the basic, then percentages of the gross so far.
            var earnings = 0m;
            foreach (var (component, amount, rate) in applicable.Where(static a => a.Component.Kind == PayComponentKinds.Earning && a.Component.Calculation != PayComponentCalculations.PercentOfGross))
            {
                earnings += component.Calculation switch
                {
                    PayComponentCalculations.Basic => Add(component, employee.BasicSalary, 0m, line.BasicAmount),
                    PayComponentCalculations.Fixed => Add(component, amount, 0m, amount * factor),
                    _ => Add(component, line.BasicAmount, rate, line.BasicAmount * rate / 100m),
                };
            }

            foreach (var (component, _, rate) in applicable.Where(static a => a.Component.Kind == PayComponentKinds.Earning && a.Component.Calculation == PayComponentCalculations.PercentOfGross))
            {
                earnings += Add(component, earnings, rate, earnings * rate / 100m);
            }

            var gross = earnings;
            var deductions = 0m;
            var employer = 0m;
            foreach (var (component, amount, rate) in applicable.Where(static a => a.Component.Kind != PayComponentKinds.Earning))
            {
                var value = component.Calculation switch
                {
                    PayComponentCalculations.Fixed => Add(component, amount, 0m, amount),
                    PayComponentCalculations.PercentOfBasic => Add(component, line.BasicAmount, rate, line.BasicAmount * rate / 100m),
                    _ => Add(component, gross, rate, gross * rate / 100m),
                };
                if (component.Kind == PayComponentKinds.Deduction)
                {
                    deductions += value;
                }
                else
                {
                    employer += value;
                }
            }

            line.EarningsTotal = earnings - line.BasicAmount;
            line.Gross = gross;
            line.DeductionsTotal = deductions;
            line.EmployerTotal = employer;
            line.Net = gross - deductions;
            run.Lines.Add(line);
        }

        run.EmployeesCount = run.Lines.Count;
        run.TotalBasic = run.Lines.Sum(static l => l.BasicAmount);
        run.TotalEarnings = run.Lines.Sum(static l => l.EarningsTotal);
        run.TotalGross = run.Lines.Sum(static l => l.Gross);
        run.TotalDeductions = run.Lines.Sum(static l => l.DeductionsTotal);
        run.TotalEmployer = run.Lines.Sum(static l => l.EmployerTotal);
        run.TotalNet = run.Lines.Sum(static l => l.Net);
        run.Status = PayrollRunStatuses.Calculated;
        run.RejectionReason = null;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, run.Id, run.Number, AuditActions.Updated, After: new { status = run.Status, run.EmployeesCount, run.TotalGross, run.TotalDeductions, run.TotalEmployer, run.TotalNet }, CompanyId: run.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>The days of a leave that fall inside the period and inside the employment, as calendar days.</summary>
    private static decimal Overlap(Leave leave, Employee employee, PayrollRun run)
    {
        var from = Max(leave.FromDate, run.PeriodStart, employee.HireDate);
        var to = Min(leave.ToDate, run.PeriodEnd, employee.TerminationDate ?? run.PeriodEnd);
        if (to < from)
        {
            return 0m;
        }

        var calendar = to.DayNumber - from.DayNumber + 1;
        var whole = leave.ToDate.DayNumber - leave.FromDate.DayNumber + 1;
        return calendar >= whole ? leave.Days : Math.Min(leave.Days, calendar);
    }

    private static DateOnly Max(DateOnly a, DateOnly b, DateOnly c) => a > b ? (a > c ? a : c) : (b > c ? b : c);

    private static DateOnly Min(DateOnly a, DateOnly b, DateOnly c) => a < b ? (a < c ? a : c) : (b < c ? b : c);

    // ------------------------------------------------------------------ reading

    internal Task<PayrollRun?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Runs.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).ThenInclude(static l => l.Items.OrderBy(static i => i.SortOrder)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    internal async Task<WorkflowSubject> SubjectAsync(PayrollRun run, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken);
        return new WorkflowSubject(DocumentType, run.Id, run.CompanyId, run.Number, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = run.TotalNet,
            ["gross"] = run.TotalGross,
            ["employerCost"] = run.TotalGross + run.TotalEmployer,
            ["currency"] = company?.FunctionalCurrency.Code,
            ["employees"] = run.EmployeesCount,
            ["period"] = $"{run.PeriodYear}-{run.PeriodMonth:00}",
            ["payDate"] = run.PayDate,
        }, run.CreatedBy ?? principal.Principal?.MembershipId.Value);
    }

    internal async Task<PayrollRunSummary> MapAsync(PayrollRun r, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(r.CompanyId), cancellationToken);
        return Map(r, company?.FunctionalCurrency.Code ?? string.Empty);
    }

    internal static PayrollRunSummary Map(PayrollRun r, string currency)
    {
        var lines = r.Lines.OrderBy(static l => l.LineNo).Select(static l => new PayrollRunLineSummary(l.Id, l.LineNo, l.EmployeeId, l.PartnerId, l.EmployeeNumber, l.EmployeeName.Values, l.BranchId, l.DaysInPeriod, l.PaidDays, l.UnpaidDays, l.BasicSalary, l.BasicAmount,
            l.EarningsTotal, l.Gross, l.DeductionsTotal, l.EmployerTotal, l.Net, l.OpenItemId, l.PaymentId, l.PaymentNumber, l.PaidOn,
            l.Items.OrderBy(static i => i.SortOrder).Select(static i => new PayrollRunItemSummary(i.Id, i.ComponentId, i.ComponentCode, i.ComponentName.Values, i.Kind, i.Calculation, i.BaseAmount, i.RatePct, i.Amount, i.SortOrder)).ToList())).ToList();
        return new PayrollRunSummary(r.Id, r.CompanyId, r.Number, r.PeriodYear, r.PeriodMonth, r.PeriodStart, r.PeriodEnd, r.PostingDate, r.PayDate, r.Status, r.ApprovalRequestId, r.RejectionReason, r.EmployeesCount,
            r.TotalBasic, r.TotalEarnings, r.TotalGross, r.TotalDeductions, r.TotalEmployer, r.TotalNet, currency, r.JournalEntryId, r.ReversalEntryId, r.Notes, lines, r.CreatedAt, r.UpdatedAt);
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Expenses.Contracts;
using Quicker.Expenses.Domain;
using Quicker.Expenses.Persistence;
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
using Quicker.Tax.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Expenses.Application;

/// <summary>
/// Expenses (founder programme item 2, A-174): recorded with their details and date as drafts; confirming one runs the
/// policy checks and the tax engine, then posts the accrual on the expense date, Dr the categories' accounts (and
/// input tax) / Cr the payable for a supplier or employee (an open item the payment settles on its own date) or Cr the
/// cash box for a petty cash voucher (a bank movement). A workflow definition on <c>expense</c> makes confirming wait
/// for approval. Reversal mirrors everything and is refused once a payment stands.
/// </summary>
public sealed class ExpenseService(
    ExpensesDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IChartOfAccounts chart,
    IPostingService posting,
    IPostingRules postingRules,
    IPayables payables,
    IPartnerDirectory partners,
    IBankAccountDirectory bankAccounts,
    IBankTransactions bankTransactions,
    IPayments payments,
    ITaxDetermination tax,
    ITaxLedger taxLedger,
    IDimensionSets dimensionSets,
    INumberAllocator numbering,
    IWorkflowEngine workflow,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = ExpenseDocumentTypes.Expense;

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<ExpenseSummary>> ListAsync(Guid companyId, string? status, string? kind, Guid? partnerId, Guid? fundId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Expenses.AsNoTracking().Where(e => e.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(e => e.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(e => e.Kind == kind);
        }

        if (partnerId is { } p)
        {
            query = query.Where(e => e.PartnerId == p);
        }

        if (fundId is { } f)
        {
            query = query.Where(e => e.FundId == f);
        }

        if (from is { } start)
        {
            query = query.Where(e => e.ExpenseDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(e => e.ExpenseDate <= end);
        }

        var expenses = await query.OrderByDescending(static e => e.ExpenseDate).ThenByDescending(static e => e.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var result = new List<ExpenseSummary>(expenses.Count);
        foreach (var expense in expenses)
        {
            result.Add(await MapAsync(expense, false, cancellationToken));
        }

        return result;
    }

    public async Task<ExpenseSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var expense = await db.Expenses.AsNoTracking().Include(static e => e.Lines).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return expense is null ? null : await MapAsync(expense, true, cancellationToken);
    }

    public Task<Expense?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Expenses.Include(static e => e.Lines).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    // ------------------------------------------------------------------ drafts

    public async Task<Result<ExpenseSummary>> CreateAsync(SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = new Expense { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Required.MembershipId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(expense, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Expenses.Add(expense);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.Created, After: new { expense.Kind, expense.TotalGross, expense.Currency }, CompanyId: expense.CompanyId), cancellationToken);
        return await MapAsync(expense, true, cancellationToken);
    }

    public async Task<Result<ExpenseSummary>> UpdateAsync(Guid id, SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status is not (ExpenseStatuses.Draft or ExpenseStatuses.Rejected))
        {
            return Error.Conflict("expense.not_draft", $"A {expense.Status} expense cannot be edited.").WithWhy(("status", expense.Status));
        }

        if (expense.CompanyId != request.CompanyId)
        {
            return Error.Validation("expense.company_fixed", "An expense stays in the company it was recorded in.");
        }

        var applied = await ApplyAsync(expense, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        expense.Status = ExpenseStatuses.Draft;
        expense.RejectionReason = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.Updated, After: new { expense.Kind, expense.TotalGross, expense.Currency }, CompanyId: expense.CompanyId), cancellationToken);
        return await MapAsync(expense, true, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status is not (ExpenseStatuses.Draft or ExpenseStatuses.Rejected))
        {
            return Error.Conflict("expense.not_draft", $"A {expense.Status} expense cannot be deleted; reverse it instead.").WithWhy(("status", expense.Status));
        }

        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.Deleted, CompanyId: expense.CompanyId), cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ApplyAsync(Expense expense, SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var kind = Validation.OneOf(request.Kind, "expense.kind", [.. ExpenseKinds.All]);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("expense.lines_required", "An expense has at least one line.");
        }

        var currency = (request.Currency ?? company.FunctionalCurrency.Code).Trim().ToUpperInvariant();
        var known = await companies.FindCurrencyAsync(currency, cancellationToken);
        if (known is null)
        {
            return Error.Validation("expense.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", currency));
        }

        var tc = known.Value;

        PettyCashFund? fund = null;
        if (kind.Value == ExpenseKinds.PettyCash)
        {
            if (request.FundId is not { } fundId)
            {
                return Error.Validation("expense.fund_required", "A petty cash expense names the fund it was paid from.");
            }

            fund = await db.Funds.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fundId && f.CompanyId == request.CompanyId, cancellationToken);
            if (fund is null || !fund.IsActive)
            {
                return Error.NotFound("petty_cash_fund", fundId);
            }

            if (!string.Equals(fund.Currency, currency, StringComparison.Ordinal))
            {
                return Error.Validation("expense.fund_currency", $"The fund keeps {fund.Currency}; a voucher paid from it is in that currency.").WithWhy(("fundCurrency", fund.Currency), ("currency", currency));
            }
        }
        else
        {
            if (request.PartnerId is not { } partnerId)
            {
                return Error.Validation("expense.partner_required", kind.Value == ExpenseKinds.Employee ? "An employee expense names the employee it is owed to." : "A supplier expense names the supplier it is owed to.");
            }

            var partner = await partners.FindAsync(partnerId, cancellationToken);
            if (partner is null || !partner.IsActive)
            {
                return Error.NotFound("partner", partnerId);
            }

            if (kind.Value == ExpenseKinds.Supplier && !partner.IsSupplier)
            {
                return Error.Validation("expense.partner_not_supplier", $"'{partner.Code}' is not a supplier; record an employee expense for a person's claim.").WithWhy(("partnerId", partnerId));
            }
        }

        if (request.DueDate is { } due && due < request.ExpenseDate)
        {
            return Error.Validation("expense.due_before_date", "The due date is on or after the expense date.");
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var categoryIds = request.Lines.Select(static l => l.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        var lines = new List<ExpenseLine>(request.Lines.Count);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (!categories.TryGetValue(line.CategoryId, out var category) || !category.IsActive)
            {
                return Error.NotFound("expense_category", line.CategoryId).WithWhy(("line", i + 1));
            }

            if (line.Amount <= 0m)
            {
                return Error.Validation("expense.amount_invalid", $"Line {i + 1}: the amount is above zero.").WithWhy(("line", i + 1));
            }

            if (!new Money(line.Amount, tc).IsRoundedToMinorUnit)
            {
                return Error.Validation("expense.amount_precision", $"Line {i + 1}: more decimals than {tc.Code} allows ({tc.MinorUnits}).").WithWhy(("line", i + 1), ("minorUnits", tc.MinorUnits));
            }

            if (category.MaxAmount is { } ceiling && line.Amount > ceiling)
            {
                return Error.Validation("expense.over_limit", $"Line {i + 1}: {category.Code} allows at most {ceiling.ToString(CultureInfo.InvariantCulture)} per line.").WithWhy(("line", i + 1), ("category", category.Code), ("limit", ceiling), ("amount", line.Amount));
            }

            Guid? dimensionSetId = null;
            if (line.Dimensions is { Count: > 0 })
            {
                var set = await dimensionSets.GetOrCreateAsync(line.Dimensions, cancellationToken);
                if (set.IsFailure)
                {
                    return set.Error!.WithWhy(("line", i + 1));
                }

                dimensionSetId = set.Value;
            }

            var existing = expense.Lines.ElementAtOrDefault(i);
            lines.Add(new ExpenseLine
            {
                Id = existing?.Id ?? Guid.CreateVersion7(),
                ExpenseId = expense.Id,
                LineNo = i + 1,
                CategoryId = category.Id,
                Description = string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim(),
                Amount = line.Amount,
                TaxCodeId = line.TaxCodeId ?? category.TaxCodeId,
                DimensionSetId = dimensionSetId,
            });
        }

        expense.Kind = kind.Value;
        expense.PartnerId = kind.Value == ExpenseKinds.PettyCash ? null : request.PartnerId;
        expense.FundId = fund?.Id;
        expense.ExpenseDate = request.ExpenseDate;
        expense.DueDate = request.DueDate;
        expense.Currency = currency;
        expense.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        expense.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        using var customJson = JsonDocument.Parse(custom.Value);
        expense.CustomFields = customJson.RootElement.Clone();
        expense.Lines.Clear();
        expense.Lines.AddRange(lines);
        // The draft already shows the tax that confirming will post, and a code outside the company's regime is refused at entry.
        return await TaxAsync(expense, cancellationToken);
    }

    // ------------------------------------------------------------------ confirm, approve, post

    /// <summary>Checks the draft in full, taxes it, and either posts it or hands it to the workflow when a definition on expenses is active.</summary>
    public async Task<Result<ExpenseSummary>> ConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status is not (ExpenseStatuses.Draft or ExpenseStatuses.Rejected))
        {
            return Error.Conflict("expense.not_draft", $"A {expense.Status} expense cannot be confirmed.").WithWhy(("status", expense.Status));
        }

        var prepared = await PrepareAsync(expense, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(await SubjectAsync(expense, cancellationToken), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.Pending)
            {
                expense.Status = ExpenseStatuses.PendingApproval;
                expense.ApprovalRequestId = outcome.Value.RequestId;
                expense.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.StateChanged, After: new { status = expense.Status, approvalRequestId = expense.ApprovalRequestId }, CompanyId: expense.CompanyId), cancellationToken);
                return await MapAsync(expense, true, cancellationToken);
            }
        }

        var posted = await PostCoreAsync(expense, cancellationToken);
        return posted.IsFailure ? posted.Error! : await MapAsync(expense, true, cancellationToken);
    }

    /// <summary>The workflow's answer: approval posts the expense, rejection sends it back with the reason, a cancelled or expired request leaves it a draft.</summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var expense = await LoadAsync(decision.EntityId, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, decision.EntityId);
        }

        if (expense.Status != ExpenseStatuses.PendingApproval || expense.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("expense.not_pending", "The expense is no longer awaiting this approval request.").WithWhy(("status", expense.Status));
        }

        if (decision.Status == WorkflowDecisions.Approved)
        {
            var prepared = await PrepareAsync(expense, cancellationToken);
            if (prepared.IsFailure)
            {
                return prepared.Error!;
            }

            var posted = await PostCoreAsync(expense, cancellationToken);
            return posted.IsFailure ? posted.Error! : Result.Success();
        }

        var rejected = decision.Status == WorkflowDecisions.Rejected;
        expense.Status = rejected ? ExpenseStatuses.Rejected : ExpenseStatuses.Draft;
        expense.RejectionReason = rejected ? decision.Comment ?? "Not approved." : null;
        expense.ApprovalRequestId = null;
        expense.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), rejected ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status = expense.Status, reason = expense.RejectionReason }, CompanyId: expense.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Resolves each line's account in the company's chart, taxes the lines on the expense date and sets the totals; nothing is posted yet.</summary>
    private async Task<Result> PrepareAsync(Expense expense, CancellationToken cancellationToken)
    {
        var chartId = await chart.ChartForCompanyAsync(new CompanyId(expense.CompanyId), cancellationToken);
        if (chartId is null)
        {
            return Error.Validation("expense.chart_required", "The company has no chart of accounts yet.");
        }

        var categoryIds = expense.Lines.Select(static l => l.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        foreach (var line in expense.Lines.OrderBy(static l => l.LineNo))
        {
            var category = categories[line.CategoryId];
            var account = await chart.FindAccountByCodeAsync(chartId.Value, category.AccountCode, cancellationToken);
            if (account is null || account.IsHeader || !account.IsActive)
            {
                return Error.Validation("expense.account_unknown", $"Line {line.LineNo}: category {category.Code} names account '{category.AccountCode}', which the company's chart does not have as a postable account.").WithWhy(("line", line.LineNo), ("category", category.Code), ("accountCode", category.AccountCode));
            }

            if (account.IsControl)
            {
                return Error.Validation("expense.account_control", $"Line {line.LineNo}: '{account.Code}' is a control account; an expense category lands on an expense or asset account.").WithWhy(("line", line.LineNo), ("accountCode", account.Code));
            }

            // Documents post by role: the category's account is kept as a posting rule (role Expense, key expense category)
            // in the company's profile, visible among the other rules, and the line names the category.
            var ensured = await postingRules.EnsureRuleAsync(new CompanyId(expense.CompanyId), AccountRoles.Expense, new PostingKeys(ExpenseCategoryId: category.Id), account.Id, cancellationToken);
            if (ensured.IsFailure)
            {
                return ensured.Error!;
            }

            line.AccountId = account.Id;
        }

        return await TaxAsync(expense, cancellationToken);
    }

    /// <summary>Taxes the lines on the expense date (the line's code, else the category's, else the determination matrix) and sets the totals.</summary>
    private async Task<Result> TaxAsync(Expense expense, CancellationToken cancellationToken)
    {
        var taxLines = expense.Lines.OrderBy(static l => l.LineNo).Select(l => new TaxDocumentLine(l.LineNo.ToString(CultureInfo.InvariantCulture), l.Amount, TaxCodeId: l.TaxCodeId)).ToList();
        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(expense.CompanyId, TaxDirections.Purchase, expense.ExpenseDate, expense.Currency, false, taxLines, expense.PartnerId), cancellationToken);
        if (taxed.IsFailure)
        {
            return taxed.Error!;
        }

        var byKey = taxed.Value.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        foreach (var line in expense.Lines)
        {
            var result = byKey[line.LineNo.ToString(CultureInfo.InvariantCulture)];
            line.TaxCodeId = result.TaxCodeId;
            line.TaxRatePct = result.RatePct;
            line.TaxAmount = result.Tax;
            line.TaxRecoverable = result.IsRecoverable;
            line.TaxReverseCharge = result.IsReverseCharge;
        }

        expense.TotalNet = taxed.Value.Document.Net;
        expense.TotalTax = taxed.Value.Document.Tax;
        expense.TotalGross = taxed.Value.Document.Gross;
        expense.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<Result> PostCoreAsync(Expense expense, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(expense.CompanyId), cancellationToken))!;
        var sameCurrency = string.Equals(expense.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        decimal? rateOverride = null;
        if (!sameCurrency)
        {
            var resolved = await rates.ResolveAsync(company.Id, expense.Currency, company.FunctionalCurrency.Code, expense.ExpenseDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("expense.rate_missing", "No spot rate from the expense currency to the company's currency on the expense date.").WithWhy(("currency", expense.Currency), ("date", expense.ExpenseDate));
            }

            rateOverride = resolved.Value.Rate.Rate;
        }

        decimal Fc(decimal tc) => sameCurrency ? tc : RoundingPolicy.Default.Round(tc * rateOverride!.Value, company.FunctionalCurrency.MinorUnits);

        SupplierTermsInfo? supplier = null;
        BankAccountInfo? cashBox = null;
        if (expense.Kind == ExpenseKinds.PettyCash)
        {
            var fund = await db.Funds.AsNoTracking().SingleAsync(f => f.Id == expense.FundId, cancellationToken);
            cashBox = await bankAccounts.FindAsync(fund.BankAccountId, cancellationToken);
            if (cashBox is null)
            {
                return Error.NotFound("bank_account", fund.BankAccountId);
            }
        }
        else
        {
            supplier = await partners.FindSupplierAsync(expense.CompanyId, expense.PartnerId!.Value, cancellationToken);
        }

        var number = expense.Number;
        if (number is null)
        {
            await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(expense.CompanyId), "EXP", "EXP-{yyyy}-{seq:5}", "yearly", cancellationToken);
            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(expense.CompanyId), null, expense.ExpenseDate, expense.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            number = allocated.Value.Text;
        }

        // The lines: each category's account for the net (plus tax that cannot be recovered), input tax per code for what can.
        var lines = new List<PostingLine>();
        var dimensionCache = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>?>();
        foreach (var line in expense.Lines.OrderBy(static l => l.LineNo))
        {
            IReadOnlyDictionary<string, Guid>? dimensions = null;
            if (line.DimensionSetId is { } setId)
            {
                if (!dimensionCache.TryGetValue(setId, out dimensions))
                {
                    dimensions = await dimensionSets.GetAsync(setId, cancellationToken);
                    dimensionCache[setId] = dimensions;
                }
            }

            var cost = line.Amount + (line.TaxRecoverable || line.TaxReverseCharge ? 0m : line.TaxAmount);
            lines.Add(new PostingLine(AccountRoles.Expense, cost, new PostingKeys(DocumentType, ExpenseCategoryId: line.CategoryId), Dimensions: dimensions, PartnerId: expense.PartnerId, AmountFc: sameCurrency ? null : Fc(cost),
                Description: line.Description is null ? null : LocalizedText.Bilingual(line.Description, line.Description)));
        }

        foreach (var byCode in expense.Lines.Where(static l => l.TaxCodeId is not null && l.TaxAmount != 0m).GroupBy(static l => l.TaxCodeId!.Value).OrderBy(static g => g.Key))
        {
            var code = await tax.FindCodeAsync(byCode.Key, expense.ExpenseDate, cancellationToken);
            if (code.IsFailure)
            {
                return code.Error!;
            }

            foreach (var part in byCode.GroupBy(static l => (l.TaxRecoverable, l.TaxReverseCharge)))
            {
                var amount = part.Sum(static l => l.TaxAmount);
                var basis = part.Sum(static l => l.Amount);
                if (part.Key.TaxRecoverable)
                {
                    lines.Add(new PostingLine(code.Value.InputAccountRole, amount, new PostingKeys(DocumentType), PartnerId: expense.PartnerId, TaxCodeId: byCode.Key, TaxBase: basis, AmountFc: sameCurrency ? null : Fc(amount)));
                }

                if (part.Key.TaxReverseCharge)
                {
                    lines.Add(new PostingLine(code.Value.OutputAccountRole, -amount, new PostingKeys(DocumentType), PartnerId: expense.PartnerId, TaxCodeId: byCode.Key, TaxBase: basis, AmountFc: sameCurrency ? null : -Fc(amount)));
                }
            }
        }

        // The credit: the payable (an open item on the partner, due on the due date) or the cash box (a bank movement).
        var transactionId = Guid.CreateVersion7();
        var dueDate = expense.DueDate ?? expense.ExpenseDate;
        if (cashBox is null)
        {
            lines.Add(new PostingLine(AccountRoles.AP, -expense.TotalGross, new PostingKeys(DocumentType, PartnerPostingGroupId: supplier?.PostingGroupId), SubledgerType: SubledgerTypes.Payables, SubledgerRef: expense.Id, PartnerId: expense.PartnerId, DueDate: dueDate, AmountFc: sameCurrency ? null : -Fc(expense.TotalGross)));
        }
        else
        {
            lines.Add(new PostingLine(RoleOf(cashBox.Kind), -expense.TotalGross, new PostingKeys(DocumentType, BankAccountId: cashBox.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: sameCurrency ? null : -Fc(expense.TotalGross)));
        }

        var what = expense.Description is null ? string.Empty : $" – {expense.Description}";
        var description = expense.Kind switch
        {
            ExpenseKinds.Employee => LocalizedText.Bilingual($"Expense claim {number}{what}", $"مطالبة مصروفات {number}{what}"),
            ExpenseKinds.PettyCash => LocalizedText.Bilingual($"Petty cash voucher {number}{what}", $"سند صندوق نثري {number}{what}"),
            _ => LocalizedText.Bilingual($"Expense {number}{what}", $"مصروف {number}{what}"),
        };
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "expenses", DocumentType, expense.Id, expense.ExpenseDate, expense.Currency, lines, number, expense.ExpenseDate, description,
            null, RateTypes.Spot, rateOverride, rateOverride is null ? null : "Expense date rate", $"expense:{expense.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (expense.Lines.Any(static l => l.TaxCodeId is not null))
        {
            var entries = expense.Lines.Where(static l => l.TaxCodeId is not null)
                .Select(l => new TaxEntryRequest(l.TaxCodeId!.Value, TaxDirections.Purchase, l.TaxRatePct, l.Amount, l.TaxAmount, Fc(l.Amount), Fc(l.TaxAmount), l.TaxReverseCharge, l.TaxRecoverable, l.Id))
                .ToList();
            var recorded = await taxLedger.RecordAsync(new TaxPostingRequest(expense.CompanyId, expense.ExpenseDate, expense.ExpenseDate, "expenses", DocumentType, expense.Id, number, posted.Value.EntryId, expense.PartnerId, expense.Currency, entries), cancellationToken);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        if (cashBox is null)
        {
            var apLine = posted.Value.Lines.Single(l => l.SubledgerType == SubledgerTypes.Payables && l.SubledgerRef == expense.Id);
            await payables.OpenAsync(new NewOpenItem(expense.CompanyId, expense.PartnerId!.Value, PayableKinds.Invoice, DocumentType, expense.Id, number, 1, expense.Reference, expense.ExpenseDate, expense.ExpenseDate, dueDate, null, 0m,
                expense.Currency, expense.TotalGross, apLine.CreditFc - apLine.DebitFc, posted.Value.RateTcFc, posted.Value.EntryId), cancellationToken);
        }
        else
        {
            await bankTransactions.RecordAsync(new NewBankTransaction(transactionId, cashBox.Id, expense.CompanyId, expense.ExpenseDate, "disbursement", -expense.TotalGross, -Fc(expense.TotalGross), expense.Reference ?? number, DocumentType, expense.Id, posted.Value.EntryId), cancellationToken);
            expense.BankTransactionId = transactionId;
        }

        expense.Number = number;
        expense.Status = ExpenseStatuses.Confirmed;
        expense.JournalEntryId = posted.Value.EntryId;
        expense.ApprovalRequestId = null;
        expense.RejectionReason = null;
        expense.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, number, AuditActions.Posted, After: new { status = expense.Status, journalEntryId = expense.JournalEntryId, expense.TotalGross, expense.Currency }, CompanyId: expense.CompanyId), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ pay, reverse, cancel

    /// <summary>Pays a confirmed supplier or employee expense from a bank account on a day: one posted payment settling its open item.</summary>
    public async Task<Result<ExpensePaymentResult>> PayAsync(Guid id, PayExpenseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status != ExpenseStatuses.Confirmed)
        {
            return Error.Conflict("expense.not_confirmed", "Only a confirmed expense is paid.").WithWhy(("status", expense.Status));
        }

        if (expense.Kind == ExpenseKinds.PettyCash)
        {
            return Error.Conflict("expense.paid_from_fund", "A petty cash voucher was paid from its fund when it was incurred; there is nothing to pay.");
        }

        var item = (await payables.ItemsOfAsync(DocumentType, expense.Id, cancellationToken)).FirstOrDefault(static i => i.RemainingTc != 0m);
        if (item is null)
        {
            return Error.Conflict("expense.already_paid", "The expense has been paid in full.");
        }

        var amount = request.Amount ?? item.RemainingTc;
        if (amount <= 0m || amount > item.RemainingTc)
        {
            return Error.Validation("expense.payment_amount", "The payment is above zero and at most what remains open.").WithWhy(("remaining", item.RemainingTc), ("amount", amount));
        }

        var company = (await companies.FindAsync(new CompanyId(expense.CompanyId), cancellationToken))!;
        var paymentDate = request.PaymentDate ?? clock.TodayIn(company.TimeZone);
        var paid = await payments.PayAsync(new PayOpenItemRequest(expense.CompanyId, expense.PartnerId!.Value, request.BankAccountId, item.Id, amount, paymentDate, request.Method, request.Reference ?? expense.Reference), cancellationToken);
        if (paid.IsFailure)
        {
            return paid.Error!;
        }

        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.StateChanged, After: new { paid = amount, paymentId = paid.Value.Id, paymentNumber = paid.Value.Number, paymentDate }, CompanyId: expense.CompanyId), cancellationToken);
        return new ExpensePaymentResult(paid.Value.Id, paid.Value.Number, await MapAsync(expense, true, cancellationToken));
    }

    public async Task<Result<ExpenseSummary>> ReverseAsync(Guid id, ReverseExpenseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status != ExpenseStatuses.Confirmed || expense.JournalEntryId is null)
        {
            return Error.Conflict("expense.not_confirmed", "Only a confirmed expense is reversed.").WithWhy(("status", expense.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("expense.reason_required", "A reversal names its reason.");
        }

        // The engine dates the mirror (the requested date, else its default) and the subledgers follow that date; a payable
        // settled in part refuses, which rolls the whole request back.
        var reversed = await posting.ReverseAsync(expense.JournalEntryId.Value, request.ReversalDate, request.Reason, cancellationToken: cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        if (expense.Kind == ExpenseKinds.PettyCash)
        {
            await bankTransactions.ReverseDocumentAsync(DocumentType, expense.Id, reversed.Value.PostingDate, reversed.Value.EntryId, cancellationToken);
        }
        else
        {
            var items = await payables.ReverseDocumentAsync(DocumentType, expense.Id, reversed.Value.PostingDate, cancellationToken);
            if (items.IsFailure)
            {
                return items.Error!;
            }
        }

        if (expense.Lines.Any(static l => l.TaxCodeId is not null))
        {
            var taxReversed = await taxLedger.ReverseAsync(DocumentType, expense.Id, reversed.Value.PostingDate, reversed.Value.EntryId, cancellationToken);
            if (taxReversed.IsFailure)
            {
                return taxReversed.Error!;
            }
        }

        expense.Status = ExpenseStatuses.Reversed;
        expense.ReversalEntryId = reversed.Value.EntryId;
        expense.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.Reversed, After: new { reversalEntryId = expense.ReversalEntryId }, Reason: request.Reason, CompanyId: expense.CompanyId), cancellationToken);
        return await MapAsync(expense, true, cancellationToken);
    }

    public async Task<Result<ExpenseSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (expense.Status is not (ExpenseStatuses.Draft or ExpenseStatuses.PendingApproval or ExpenseStatuses.Rejected))
        {
            return Error.Conflict("expense.not_cancellable", $"A {expense.Status} expense is reversed, not cancelled.").WithWhy(("status", expense.Status));
        }

        if (expense.ApprovalRequestId is not null)
        {
            var withdrawn = await workflow.CancelAsync(DocumentType, expense.Id, "The expense was cancelled.", cancellationToken);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }
        }

        expense.Status = ExpenseStatuses.Cancelled;
        expense.ApprovalRequestId = null;
        expense.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, expense.Id, Number(expense), AuditActions.StateChanged, After: new { status = expense.Status }, CompanyId: expense.CompanyId), cancellationToken);
        return await MapAsync(expense, true, cancellationToken);
    }

    // ------------------------------------------------------------------ workflow subject, mapping

    public async Task<WorkflowSubject> SubjectAsync(Expense expense, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expense);
        var partner = expense.PartnerId is { } p ? await partners.FindAsync(p, cancellationToken) : null;
        var categoryIds = expense.Lines.Select(static l => l.CategoryId).Distinct().ToList();
        var codes = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).OrderBy(static c => c.Code).Select(static c => c.Code).ToListAsync(cancellationToken);
        return new WorkflowSubject(DocumentType, expense.Id, expense.CompanyId, Number(expense), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = expense.TotalGross,
            ["currency"] = expense.Currency,
            ["kind"] = expense.Kind,
            ["partnerCode"] = partner?.Code,
            ["lineCount"] = expense.Lines.Count,
            ["categories"] = string.Join(",", codes),
            ["hasTax"] = expense.TotalTax != 0m,
            ["expenseDate"] = expense.ExpenseDate,
        }, expense.CreatedBy);
    }

    private static string Number(Expense e) => e.Number ?? DraftIdentifiers.For(e.Id);

    private static string RoleOf(string kind) => kind switch
    {
        BankAccountKinds.Cash => AccountRoles.Cash,
        BankAccountKinds.PettyCash => AccountRoles.PettyCash,
        _ => AccountRoles.Bank,
    };

    private async Task<ExpenseSummary> MapAsync(Expense e, bool withLines, CancellationToken cancellationToken)
    {
        var partner = e.PartnerId is { } p ? await partners.FindAsync(p, cancellationToken) : null;
        var fund = e.FundId is { } f ? await db.Funds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == f, cancellationToken) : null;
        var paid = 0m;
        var remaining = 0m;
        var paymentStatus = "n/a";
        if (e.Status == ExpenseStatuses.Confirmed && e.Kind != ExpenseKinds.PettyCash)
        {
            var items = await payables.ItemsOfAsync(DocumentType, e.Id, cancellationToken);
            paid = items.Sum(static i => i.SettledTc);
            remaining = items.Sum(static i => i.RemainingTc);
            paymentStatus = remaining == 0m ? "paid" : paid == 0m ? "unpaid" : "partial";
        }

        IReadOnlyList<ExpenseLineSummary>? lines = null;
        if (withLines)
        {
            var categoryIds = e.Lines.Select(static l => l.CategoryId).Distinct().ToList();
            var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
            var mapped = new List<ExpenseLineSummary>(e.Lines.Count);
            foreach (var line in e.Lines.OrderBy(static l => l.LineNo))
            {
                var category = categories.GetValueOrDefault(line.CategoryId);
                var dimensions = line.DimensionSetId is { } setId ? await dimensionSets.GetAsync(setId, cancellationToken) : null;
                mapped.Add(new ExpenseLineSummary(line.Id, line.LineNo, line.CategoryId, category?.Code ?? string.Empty, category?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), line.AccountId, line.Description, line.Amount, line.TaxCodeId, line.TaxRatePct, line.TaxAmount, line.TaxRecoverable, dimensions));
            }

            lines = mapped;
        }

        return new ExpenseSummary(e.Id, e.CompanyId, Number(e), e.Kind, e.Status, e.PartnerId, partner?.Code, partner?.LegalName.Values, e.FundId, fund?.Code, e.ExpenseDate, e.DueDate, e.Currency, e.Reference, e.Description,
            e.TotalNet, e.TotalTax, e.TotalGross, paid, remaining, paymentStatus, e.JournalEntryId, e.ReversalEntryId, e.ApprovalRequestId, e.RejectionReason, e.CustomFields, e.CreatedAt, e.UpdatedAt, lines);
    }
}

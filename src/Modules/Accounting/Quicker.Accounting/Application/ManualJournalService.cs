using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Web;

namespace Quicker.Accounting.Application;

/// <summary>
/// Manual journals (roadmap 2.3, POSTING_RULES §8): drafted, optionally approved by someone else, posted through
/// the engine as one immutable entry. Opening balance journals balance themselves to the opening balance equity
/// account; accruals carry an automatic reversal date; control accounts are allowed only when the line names the
/// subledger item it adjusts. The document keeps the entry id and stays editable only while unposted.
/// </summary>
public sealed class ManualJournalService(
    AccountingDbContext db,
    ICompanyDirectory companies,
    IPostingService posting,
    INumberAllocator numbering,
    ICompanySettings settings,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string EntityType = "manual_journal";
    public const string ApprovalSetting = "accounting.journals.approval";

    public async Task<Result<Page<ManualJournalSummary>>> ListAsync(Guid companyId, string? status, DateOnly? from, DateOnly? to, PageRequest page, CancellationToken cancellationToken)
    {
        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var query = db.Set<ManualJournal>().Include(static j => j.Lines).Where(j => j.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(j => j.Status == normalized);
        }

        if (from is { } f)
        {
            query = query.Where(j => j.PostingDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(j => j.PostingDate <= t);
        }

        var result = await KeysetPaging.ByIdDescendingAsync(query, static j => j.Id, page, cancellationToken);
        return result.IsFailure ? result.Error! : result.Value.Map(j => Map(j, null));
    }

    public async Task<ManualJournalSummary?> GetAsync(Guid journalId, CancellationToken cancellationToken)
    {
        var journal = await db.Set<ManualJournal>().Include(static j => j.Lines).SingleOrDefaultAsync(j => j.Id == journalId, cancellationToken);
        if (journal is null)
        {
            return null;
        }

        var ids = journal.Lines.Select(static l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        return Map(journal, accounts);
    }

    public async Task<Result<ManualJournalSummary>> CreateAsync(Guid companyId, SaveJournalRequest request, Guid? templateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var now = clock.UtcNow;
        var journal = new ManualJournal { Id = Guid.CreateVersion7(), CompanyId = companyId, TemplateId = templateId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = now };
        var applied = await ApplyAsync(company, journal, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Set<ManualJournal>().Add(journal);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), AuditActions.Created, After: new { journal.CompanyId, journal.Kind, journal.PostingDate, journal.Currency, lines = journal.Lines.Count }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    public async Task<Result<ManualJournalSummary>> UpdateAsync(Guid journalId, SaveJournalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var journal = await db.Set<ManualJournal>().Include(static j => j.Lines).SingleOrDefaultAsync(j => j.Id == journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        if (journal.Status is not (JournalStatuses.Draft or JournalStatuses.Rejected))
        {
            return Error.Conflict("journal.not_editable", $"A {journal.Status} journal cannot be edited.").WithWhy(("status", journal.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(journal.CompanyId), cancellationToken))!;
        var before = new { journal.PostingDate, journal.Currency, lines = journal.Lines.Count, debit = journal.Lines.Sum(static l => l.Debit) };
        db.Set<ManualJournalLine>().RemoveRange(journal.Lines);
        journal.Lines.Clear();
        var applied = await ApplyAsync(company, journal, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        journal.Status = JournalStatuses.Draft;
        journal.RejectionReason = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), AuditActions.Updated, Before: before, After: new { journal.PostingDate, journal.Currency, lines = journal.Lines.Count, debit = journal.Lines.Sum(static l => l.Debit) }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    private async Task<Result> ApplyAsync(CompanyInfo company, ManualJournal journal, SaveJournalRequest request, CancellationToken cancellationToken)
    {
        var kind = Validation.OneOf(request.Kind, "journal.kind", JournalKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.");
        }

        var currency = await companies.FindCurrencyAsync(request.Currency?.Trim().ToUpperInvariant() ?? string.Empty, cancellationToken);
        if (currency is not { } tc)
        {
            return Error.Validation("journal.currency_unknown", $"Currency '{request.Currency}' is not in the ISO 4217 list.");
        }

        if (request.RateOverride is { } rate && (rate <= 0m || string.IsNullOrWhiteSpace(request.RateOverrideReason)))
        {
            return Error.Validation("journal.rate_override_invalid", "A rate override is a positive rate with a reason.");
        }

        if (request.AutoReverse && (request.AutoReverseOn is null || request.AutoReverseOn <= request.PostingDate))
        {
            return Error.Validation("journal.auto_reverse_date", "An auto-reversing journal names a reversal date after its posting date.");
        }

        if (request.BranchId is { } branchId)
        {
            var branch = await companies.FindBranchAsync(new BranchId(branchId), cancellationToken);
            if (branch is null || branch.CompanyId.Value != company.Id.Value)
            {
                return Error.NotFound("branch", branchId);
            }
        }

        var custom = await customFields.ValidateAsync(EntityType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("journal.lines_required", "A journal needs at least one line.");
        }

        var codes = request.Lines.Where(static l => l.AccountId is null && !string.IsNullOrWhiteSpace(l.AccountCode)).Select(static l => l.AccountCode!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var byCode = await db.Accounts.Where(a => a.ChartId == chartId && codes.Contains(a.Code)).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.Debit < 0m || line.Credit < 0m || (line.Debit > 0m && line.Credit > 0m))
            {
                return Error.Validation("journal.line_sides", $"Line {i + 1}: enter a debit or a credit, not both, never negative.").WithWhy(("line", i + 1));
            }

            if (!new Money(line.Debit, tc).IsRoundedToMinorUnit || !new Money(line.Credit, tc).IsRoundedToMinorUnit)
            {
                return Error.Validation("journal.amount_precision", $"Line {i + 1}: more decimals than {tc.Code} allows ({tc.MinorUnits}).").WithWhy(("line", i + 1), ("minorUnits", tc.MinorUnits));
            }

            Account? account = null;
            if (line.AccountId is { } accountId)
            {
                account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.ChartId == chartId, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(line.AccountCode))
            {
                byCode.TryGetValue(line.AccountCode.Trim(), out account);
            }

            if (account is null)
            {
                return Error.NotFound("account", (object?)line.AccountId ?? line.AccountCode ?? string.Empty).WithWhy(("line", i + 1));
            }

            if (account.IsHeader || !account.IsActive)
            {
                return Error.Validation("account.header", $"Line {i + 1}: '{account.Code}' is a header or inactive account.").WithWhy(("line", i + 1), ("account", account.Code));
            }

            if ((line.SubledgerType is null) != (line.SubledgerRef is null))
            {
                return Error.Validation("journal.subledger_pair", $"Line {i + 1}: a subledger reference comes with its type and vice versa.").WithWhy(("line", i + 1));
            }

            if (line.SubledgerType is { } subledger && !SubledgerTypes.All.Contains(subledger, StringComparer.Ordinal))
            {
                return Error.Validation("journal.subledger_type_invalid", $"Line {i + 1}: unknown subledger type '{subledger}'.").WithWhy(("line", i + 1));
            }

            journal.Lines.Add(new ManualJournalLine
            {
                Id = Guid.CreateVersion7(),
                JournalId = journal.Id,
                LineNo = i + 1,
                AccountId = account.Id,
                Debit = line.Debit,
                Credit = line.Credit,
                Dimensions = new Dictionary<string, Guid>((line.Dimensions ?? new Dictionary<string, Guid>(StringComparer.Ordinal)).Select(static p => new KeyValuePair<string, Guid>(p.Key.Trim().ToUpperInvariant(), p.Value)), StringComparer.Ordinal),
                PartnerId = line.PartnerId,
                SubledgerType = line.SubledgerType,
                SubledgerRef = line.SubledgerRef,
                TaxCodeId = line.TaxCodeId,
                Description = line.Description is null ? new LocalizedText() : new LocalizedText(line.Description),
                DueDate = line.DueDate,
            });
        }

        journal.Kind = kind.Value;
        journal.PostingDate = request.PostingDate;
        journal.DocumentDate = request.DocumentDate ?? request.PostingDate;
        journal.Currency = tc.Code;
        journal.RateType = string.IsNullOrWhiteSpace(request.RateType) ? RateTypes.Spot : request.RateType.Trim().ToLowerInvariant();
        journal.RateOverride = request.RateOverride;
        journal.RateOverrideReason = request.RateOverride is null ? null : request.RateOverrideReason?.Trim();
        journal.BranchId = request.BranchId;
        journal.Description = request.Description is null ? new LocalizedText() : new LocalizedText(request.Description);
        journal.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        journal.AutoReverse = request.AutoReverse || kind.Value == JournalKinds.Accrual;
        journal.AutoReverseOn = journal.AutoReverse ? request.AutoReverseOn : null;
        if (journal.AutoReverse && journal.AutoReverseOn is null)
        {
            return Error.Validation("journal.auto_reverse_date", "An accrual names the date its reversal posts on (usually the first day of the next period).");
        }

        journal.CustomFields = custom.Value;
        journal.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    // ------------------------------------------------------------------ workflow

    public async Task<Result<ManualJournalSummary>> SubmitAsync(Guid journalId, CancellationToken cancellationToken)
    {
        var journal = await LoadAsync(journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        if (journal.Status is not (JournalStatuses.Draft or JournalStatuses.Rejected))
        {
            return Error.Conflict("journal.not_submittable", $"A {journal.Status} journal cannot be submitted.").WithWhy(("status", journal.Status));
        }

        var balanced = Balanced(journal);
        if (balanced.IsFailure)
        {
            return balanced.Error!;
        }

        var now = clock.UtcNow;
        journal.SubmittedBy = principal.Principal?.UserId.Value;
        journal.SubmittedAt = now;
        journal.Status = await ApprovalRequiredAsync(journal.CompanyId, cancellationToken) ? JournalStatuses.PendingApproval : JournalStatuses.Approved;
        journal.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), AuditActions.StateChanged, Before: new { status = JournalStatuses.Draft }, After: new { status = journal.Status }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    public async Task<Result<ManualJournalSummary>> ApproveAsync(Guid journalId, CancellationToken cancellationToken)
    {
        var journal = await LoadAsync(journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        if (journal.Status != JournalStatuses.PendingApproval)
        {
            return Error.Conflict("journal.not_pending", $"A {journal.Status} journal is not waiting for approval.").WithWhy(("status", journal.Status));
        }

        var actor = principal.Principal?.UserId.Value;
        if (actor is not null && actor == journal.SubmittedBy)
        {
            return Error.Conflict("journal.approve_own", "The person who submitted a journal cannot approve it.").WithWhy(("submittedBy", journal.SubmittedBy));
        }

        var now = clock.UtcNow;
        journal.ApprovedBy = actor;
        journal.ApprovedAt = now;
        journal.Status = JournalStatuses.Approved;
        journal.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), "approved", After: new { approvedBy = actor }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    public async Task<Result<ManualJournalSummary>> RejectAsync(Guid journalId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("journal.reason_required", "Say why the journal is rejected.");
        }

        var journal = await LoadAsync(journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        if (journal.Status != JournalStatuses.PendingApproval)
        {
            return Error.Conflict("journal.not_pending", $"A {journal.Status} journal is not waiting for approval.").WithWhy(("status", journal.Status));
        }

        journal.Status = JournalStatuses.Rejected;
        journal.RejectionReason = reason.Trim();
        journal.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), "rejected", After: new { reason = reason.Trim() }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    public async Task<Result<ManualJournalSummary>> CancelAsync(Guid journalId, CancellationToken cancellationToken)
    {
        var journal = await LoadAsync(journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        if (journal.Status is JournalStatuses.Posted or JournalStatuses.Cancelled)
        {
            return Error.Conflict("journal.not_cancellable", $"A {journal.Status} journal cannot be cancelled; reverse the entry instead.").WithWhy(("status", journal.Status));
        }

        var before = journal.Status;
        journal.Status = JournalStatuses.Cancelled;
        journal.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, Number(journal), AuditActions.StateChanged, Before: new { status = before }, After: new { status = journal.Status }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    /// <summary>Posts the journal through the engine; an opening journal gets its balancing line on the opening balance equity role.</summary>
    public async Task<Result<ManualJournalSummary>> PostAsync(Guid journalId, CancellationToken cancellationToken)
    {
        var journal = await LoadAsync(journalId, cancellationToken);
        if (journal is null)
        {
            return Error.NotFound(EntityType, journalId);
        }

        var approvalRequired = await ApprovalRequiredAsync(journal.CompanyId, cancellationToken);
        var postable = journal.Status == JournalStatuses.Approved || (!approvalRequired && journal.Status == JournalStatuses.Draft);
        if (!postable)
        {
            return Error.Conflict("journal.not_postable", approvalRequired && journal.Status != JournalStatuses.Approved
                ? "This company requires approval before a journal is posted."
                : $"A {journal.Status} journal cannot be posted.").WithWhy(("status", journal.Status), ("approvalRequired", approvalRequired));
        }

        var balanced = Balanced(journal);
        if (balanced.IsFailure)
        {
            return balanced.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(journal.CompanyId), cancellationToken))!;
        var ids = journal.Lines.Select(static l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        var lines = journal.Lines.OrderBy(static l => l.LineNo).Select(l => new PostingLine(
            accounts[l.AccountId].DefaultRole ?? "Manual", l.Debit - l.Credit, null, l.AccountId, l.Dimensions, l.PartnerId, l.SubledgerType, l.SubledgerRef, l.TaxCodeId, null, l.Description, l.DueDate)).ToList();
        var difference = lines.Sum(static l => l.Amount);
        if (journal.Kind == JournalKinds.Opening && difference != 0m)
        {
            lines.Add(new PostingLine(AccountRoles.OpeningBalanceEquity, -difference, PostingKeys.None, Description: LocalizedText.Bilingual("Opening balance equity", "حقوق ملكية الأرصدة الافتتاحية")));
        }

        var number = journal.Number;
        if (number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(EntityType, company.Id, "MJ-" + company.Code, "MJ-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(EntityType, company.Id, journal.BranchId is { } b ? new BranchId(b) : null, journal.PostingDate, journal.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            number = allocated.Value.Text;
        }

        var posted = await posting.PostAsync(new PostingRequest(company.Id, "accounting", EntityType, journal.Id, journal.PostingDate, journal.Currency, lines, number, journal.DocumentDate,
            journal.Description, journal.BranchId is { } branch ? new BranchId(branch) : null, journal.RateType, journal.RateOverride, journal.RateOverrideReason, "manual_journal:" + journal.Id.ToString("N"),
            IsManual: true, IsOpeningEntry: journal.Kind == JournalKinds.Opening, AutoReverseOn: journal.AutoReverse ? journal.AutoReverseOn : null), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var now = clock.UtcNow;
        journal.Number = number;
        journal.JournalEntryId = posted.Value.EntryId;
        journal.Status = JournalStatuses.Posted;
        journal.PostedBy = principal.Principal?.UserId.Value;
        journal.PostedAt = now;
        journal.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, journal.Id, number, AuditActions.Posted, After: new { entryId = posted.Value.EntryId, entryNumber = posted.Value.Number, journal.PostingDate }), cancellationToken);
        return (await GetAsync(journal.Id, cancellationToken))!;
    }

    /// <summary>Drafts from a batch (JSON or CSV rows grouped by journal reference); all or nothing.</summary>
    public async Task<Result<JournalImportResult>> ImportAsync(Guid companyId, IReadOnlyList<SaveJournalRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return Error.Validation("import.empty", "Nothing to import.");
        }

        var ids = new List<Guid>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var created = await CreateAsync(companyId, requests[i], null, cancellationToken);
            if (created.IsFailure)
            {
                return created.Error!.WithWhy(("journal", i + 1));
            }

            ids.Add(created.Value.Id);
        }

        return new JournalImportResult(ids.Count, ids);
    }

    // ------------------------------------------------------------------ helpers

    private Task<ManualJournal?> LoadAsync(Guid journalId, CancellationToken cancellationToken) =>
        db.Set<ManualJournal>().Include(static j => j.Lines).SingleOrDefaultAsync(j => j.Id == journalId, cancellationToken);

    private async Task<bool> ApprovalRequiredAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(companyId, ApprovalSetting, cancellationToken);
        return value is { ValueKind: JsonValueKind.String } v && string.Equals(v.GetString(), "required", StringComparison.OrdinalIgnoreCase);
    }

    private static Result Balanced(ManualJournal journal)
    {
        if (journal.Kind == JournalKinds.Opening)
        {
            return Result.Success();
        }

        var debit = journal.Lines.Sum(static l => l.Debit);
        var credit = journal.Lines.Sum(static l => l.Credit);
        return debit == credit && debit > 0m
            ? Result.Success()
            : Error.Validation("journal.unbalanced", $"Debits {debit} and credits {credit} differ.").WithWhy(("debit", debit), ("credit", credit));
    }

    private static string Number(ManualJournal j) => j.Number ?? DraftIdentifiers.For(j.Id);

    private static ManualJournalSummary Map(ManualJournal j, Dictionary<Guid, Account>? accounts)
    {
        var lines = accounts is null ? null : j.Lines.OrderBy(static l => l.LineNo).Select(l =>
        {
            var account = accounts.GetValueOrDefault(l.AccountId);
            return new JournalLineSummaryView(l.Id, l.LineNo, l.AccountId, account?.Code ?? string.Empty, account?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.Debit, l.Credit, l.Dimensions,
                l.PartnerId, l.SubledgerType, l.SubledgerRef, l.TaxCodeId, l.Description.Values, l.DueDate);
        }).ToList();
        return new ManualJournalSummary(j.Id, j.CompanyId, Number(j), j.Kind, j.PostingDate, j.DocumentDate, j.Currency, j.RateType, j.RateOverride, j.RateOverrideReason, j.BranchId, j.Description.Values, j.Reference,
            j.Status, j.AutoReverse, j.AutoReverseOn, j.TemplateId, j.JournalEntryId, JsonDocument.Parse(j.CustomFields).RootElement.Clone(), j.Lines.Sum(static l => l.Debit), j.Lines.Sum(static l => l.Credit),
            j.SubmittedBy, j.SubmittedAt, j.ApprovedBy, j.ApprovedAt, j.RejectionReason, j.PostedBy, j.PostedAt, j.UpdatedAt, lines);
    }
}

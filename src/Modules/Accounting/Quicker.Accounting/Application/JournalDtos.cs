using System.Text.Json;

namespace Quicker.Accounting.Application;

public sealed record JournalLineRequest(
    decimal Debit = 0m,
    decimal Credit = 0m,
    string? AccountCode = null,
    Guid? AccountId = null,
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    Guid? PartnerId = null,
    string? SubledgerType = null,
    Guid? SubledgerRef = null,
    Guid? TaxCodeId = null,
    IReadOnlyDictionary<string, string>? Description = null,
    DateOnly? DueDate = null);

public sealed record SaveJournalRequest(
    DateOnly PostingDate,
    string Currency,
    IReadOnlyList<JournalLineRequest> Lines,
    string Kind = "manual",
    DateOnly? DocumentDate = null,
    IReadOnlyDictionary<string, string>? Description = null,
    string? Reference = null,
    Guid? BranchId = null,
    string RateType = "spot",
    decimal? RateOverride = null,
    string? RateOverrideReason = null,
    bool AutoReverse = false,
    DateOnly? AutoReverseOn = null,
    JsonElement? CustomFields = null);

public sealed record JournalLineSummaryView(
    Guid Id,
    int LineNo,
    Guid AccountId,
    string AccountCode,
    IReadOnlyDictionary<string, string> AccountName,
    decimal Debit,
    decimal Credit,
    IReadOnlyDictionary<string, Guid> Dimensions,
    Guid? PartnerId,
    string? SubledgerType,
    Guid? SubledgerRef,
    Guid? TaxCodeId,
    IReadOnlyDictionary<string, string> Description,
    DateOnly? DueDate);

public sealed record ManualJournalSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Kind,
    DateOnly PostingDate,
    DateOnly DocumentDate,
    string Currency,
    string RateType,
    decimal? RateOverride,
    string? RateOverrideReason,
    Guid? BranchId,
    IReadOnlyDictionary<string, string> Description,
    string? Reference,
    string Status,
    bool AutoReverse,
    DateOnly? AutoReverseOn,
    Guid? TemplateId,
    Guid? JournalEntryId,
    JsonElement CustomFields,
    decimal TotalDebit,
    decimal TotalCredit,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? RejectionReason,
    Guid? PostedBy,
    DateTimeOffset? PostedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<JournalLineSummaryView>? Lines = null);

public sealed record RejectJournalRequest(string Reason);

public sealed record JournalImportRequest(IReadOnlyList<SaveJournalRequest> Journals);

public sealed record JournalImportResult(int Created, IReadOnlyList<Guid> JournalIds);

/// <summary>A template line: amounts in fixed mode, percentages of the base amount in percentage mode (debit or credit side), accounts only in variable mode.</summary>
public sealed record RecurringLineRequest(string AccountCode, decimal Debit = 0m, decimal Credit = 0m, IReadOnlyDictionary<string, Guid>? Dimensions = null, IReadOnlyDictionary<string, string>? Description = null);

public sealed record SaveRecurringTemplateRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Cron,
    string Currency,
    IReadOnlyList<RecurringLineRequest> Lines,
    string AmountMode = "fixed",
    decimal? BaseAmount = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    IReadOnlyDictionary<string, string>? Description = null,
    bool RequiresReview = true,
    bool AutoReverse = false,
    bool IsActive = true);

public sealed record RecurringTemplateSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Cron,
    string TimeZone,
    DateOnly? NextRunOn,
    DateOnly? EndsOn,
    string AmountMode,
    decimal? BaseAmount,
    string Currency,
    IReadOnlyList<RecurringLineRequest> Lines,
    IReadOnlyDictionary<string, string> Description,
    bool RequiresReview,
    bool AutoReverse,
    bool IsActive,
    DateOnly? LastGeneratedOn,
    DateTimeOffset UpdatedAt);

public sealed record GenerateRecurringRequest(DateOnly? RunDate = null, decimal? BaseAmount = null);

public sealed record SaveDeferralRequest(
    string Kind,
    string BalanceAccountCode,
    string TargetAccountCode,
    DateOnly StartsOn,
    int Periods,
    decimal TotalAmount,
    string Currency,
    string Method = "straight_line",
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    IReadOnlyDictionary<string, string>? Description = null,
    string? SourceDocumentType = null,
    Guid? SourceLineId = null);

public sealed record DeferralLineSummary(int Sequence, Guid? FiscalPeriodId, DateOnly PostingDate, decimal Amount, string Status, Guid? JournalEntryId);

public sealed record DeferralScheduleSummary(
    Guid Id,
    Guid CompanyId,
    string Kind,
    string? SourceDocumentType,
    Guid? SourceLineId,
    Guid BalanceAccountId,
    string BalanceAccountCode,
    Guid TargetAccountId,
    string TargetAccountCode,
    DateOnly StartsOn,
    int Periods,
    string Method,
    decimal TotalAmount,
    string Currency,
    IReadOnlyDictionary<string, Guid> Dimensions,
    IReadOnlyDictionary<string, string> Description,
    string Status,
    decimal PostedAmount,
    decimal RemainingAmount,
    IReadOnlyList<DeferralLineSummary> Lines);

public sealed record DeferralPreview(IReadOnlyList<DeferralLineSummary> Lines, decimal TotalAmount);

/// <summary>What one run of the daily routines did for a company (or a tenant when <see cref="CompanyId"/> is null).</summary>
public sealed record RoutineRunResult(
    Guid? CompanyId,
    DateOnly AsOf,
    IReadOnlyList<RoutineOutcome> AutoReversals,
    IReadOnlyList<RoutineOutcome> RecurringJournals,
    IReadOnlyList<RoutineOutcome> DeferralPostings);

/// <summary>One item the routine handled: what it targeted, what it produced, or why it waited.</summary>
public sealed record RoutineOutcome(Guid TargetId, string Outcome, Guid? ProducedId, string? ProducedNumber, string? Problem);

public static class RoutineOutcomes
{
    public const string Posted = "posted";
    public const string Generated = "generated";
    public const string Waiting = "waiting";
}

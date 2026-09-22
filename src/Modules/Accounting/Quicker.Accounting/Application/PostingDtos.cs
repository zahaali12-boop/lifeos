namespace Quicker.Accounting.Application;

public sealed record SavePostingGroupRequest(string Kind, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive = true);

public sealed record PostingGroupSummary(Guid Id, string Kind, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive);

public sealed record SavePostingProfileRequest(string Code = "DEFAULT", IReadOnlyDictionary<string, string>? Name = null, DateOnly? ValidFrom = null);

public sealed record PostingRuleRequest(
    string AccountRole,
    string AccountCode,
    string? DocumentType = null,
    Guid? ItemPostingGroupId = null,
    Guid? PartnerPostingGroupId = null,
    Guid? TaxCodeId = null,
    Guid? WarehouseId = null,
    Guid? BranchId = null,
    Guid? BankAccountId = null,
    Guid? AssetCategoryId = null,
    Guid? ChargeTypeId = null);

public sealed record PostingRuleSummary(
    Guid Id,
    string AccountRole,
    Guid AccountId,
    string AccountCode,
    string? DocumentType,
    Guid? ItemPostingGroupId,
    Guid? PartnerPostingGroupId,
    Guid? TaxCodeId,
    Guid? WarehouseId,
    Guid? BranchId,
    Guid? BankAccountId,
    Guid? AssetCategoryId,
    Guid? ChargeTypeId,
    int Specificity);

public sealed record PostingProfileSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    int Version,
    DateOnly ValidFrom,
    string Status,
    bool IsCurrent,
    int RuleCount,
    IReadOnlyList<string> UnresolvedRoles,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PostingRuleSummary>? Rules = null);

public sealed record PostLineRequest(
    string AccountRole,
    decimal Amount,
    string? DocumentType = null,
    Guid? ItemPostingGroupId = null,
    Guid? PartnerPostingGroupId = null,
    Guid? TaxCodeId = null,
    Guid? WarehouseId = null,
    Guid? BranchId = null,
    Guid? BankAccountId = null,
    Guid? AssetCategoryId = null,
    Guid? ChargeTypeId = null,
    Guid? AccountId = null,
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    Guid? PartnerId = null,
    string? SubledgerType = null,
    Guid? SubledgerRef = null,
    decimal? TaxBase = null,
    IReadOnlyDictionary<string, string>? Description = null,
    DateOnly? DueDate = null);

public sealed record PostJournalRequest(
    Guid CompanyId,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    DateOnly PostingDate,
    string Currency,
    IReadOnlyList<PostLineRequest> Lines,
    string? SourceDocumentNumber = null,
    DateOnly? DocumentDate = null,
    IReadOnlyDictionary<string, string>? Description = null,
    Guid? BranchId = null,
    string RateType = "spot",
    decimal? RateOverride = null,
    string? RateOverrideReason = null,
    string? IdempotencyKey = null,
    bool IsManual = false,
    bool IsOpeningEntry = false,
    bool IsClosingEntry = false,
    DateOnly? AutoReverseOn = null);

public sealed record ReverseRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record JournalLineSummary(
    Guid Id,
    int LineNo,
    Guid AccountId,
    string AccountCode,
    IReadOnlyDictionary<string, string> AccountName,
    string AccountRole,
    Guid? PostingRuleId,
    decimal DebitTc,
    decimal CreditTc,
    decimal DebitFc,
    decimal CreditFc,
    decimal DebitRc,
    decimal CreditRc,
    Guid? DimensionSetId,
    Guid? BranchId,
    Guid? PartnerId,
    string? SubledgerType,
    Guid? SubledgerRef,
    IReadOnlyDictionary<string, string> Description,
    DateOnly? DueDate,
    bool IsRounding);

public sealed record EntryLinkSummary(Guid FromEntryId, Guid ToEntryId, string Relation, string Reason);

public sealed record JournalEntrySummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    DateOnly PostingDate,
    DateOnly DocumentDate,
    Guid FiscalYearId,
    Guid FiscalPeriodId,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    string? SourceDocumentNumber,
    IReadOnlyDictionary<string, string> Description,
    bool IsReversal,
    bool IsAutoReversal,
    DateOnly? AutoReverseOn,
    bool IsClosingEntry,
    bool IsOpeningEntry,
    bool IsManual,
    string CurrencyTc,
    string CurrencyFc,
    string? CurrencyRc,
    string RateType,
    decimal RateTcFc,
    decimal? RateFcRc,
    Guid? PostingProfileId,
    int LineCount,
    Guid? PostedBy,
    DateTimeOffset PostedAt,
    Guid? ReversedByEntryId,
    Guid? ReversesEntryId,
    decimal TotalDebitTc,
    decimal TotalDebitFc,
    IReadOnlyList<JournalLineSummary>? Lines = null,
    IReadOnlyList<EntryLinkSummary>? Links = null);

public sealed record BalanceRow(
    Guid AccountId,
    string AccountCode,
    Guid FiscalPeriodId,
    string CurrencyTc,
    Guid DimensionSetId,
    decimal DebitTc,
    decimal CreditTc,
    decimal DebitFc,
    decimal CreditFc,
    decimal DebitRc,
    decimal CreditRc);

public sealed record BalanceDifference(Guid AccountId, Guid FiscalPeriodId, string CurrencyTc, Guid DimensionSetId, string Column, decimal Stored, decimal Rebuilt);

public sealed record BalanceVerification(Guid? CompanyId, int StoredRows, int RebuiltRows, IReadOnlyList<BalanceDifference> Differences)
{
    public bool IsConsistent => Differences.Count == 0;
}

public sealed record RebuildResult(Guid? CompanyId, int RowsBefore, int RowsAfter);

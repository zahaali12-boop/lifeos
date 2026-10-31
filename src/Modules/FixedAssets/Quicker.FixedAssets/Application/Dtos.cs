using System.Text.Json;

namespace Quicker.FixedAssets.Application;

public sealed record SaveAssetBookRequest(Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, bool PostsToGl, bool IsActive = true);

public sealed record AssetBookSummary(Guid Id, Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, bool PostsToGl, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveAssetCategoryRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string DefaultMethod,
    int DefaultLifeMonths,
    decimal DefaultSalvagePct = 0m,
    decimal? DefaultRatePct = null,
    string Convention = "full_month",
    string? CostAccountCode = null,
    string? AccumulatedDepreciationAccountCode = null,
    string? DepreciationExpenseAccountCode = null,
    bool IsActive = true);

public sealed record AssetCategorySummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string DefaultMethod,
    int DefaultLifeMonths,
    decimal DefaultSalvagePct,
    decimal? DefaultRatePct,
    string Convention,
    string? CostAccountCode,
    string? AccumulatedDepreciationAccountCode,
    string? DepreciationExpenseAccountCode,
    bool IsActive,
    DateTimeOffset UpdatedAt);

/// <summary>How one book values the asset, where it departs from the category's defaults; blanks take the defaults.</summary>
public sealed record AssetBookValueRequest(Guid BookId, string? Method = null, int? LifeMonths = null, decimal? SalvageValue = null, decimal? RatePct = null, string? Convention = null, decimal? TotalUnits = null);

public sealed record SaveAssetRequest(
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Name,
    Guid CategoryId,
    DateOnly AcquisitionDate,
    decimal Cost,
    string AcquisitionSource,
    DateOnly? InServiceDate = null,
    string? Description = null,
    Guid? ParentAssetId = null,
    decimal Quantity = 1m,
    string? Unit = null,
    Guid? BranchId = null,
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    Guid? CustodianMembershipId = null,
    string? SerialNumber = null,
    Guid? SupplierPartnerId = null,
    string? SupplierReference = null,
    DateOnly? WarrantyUntil = null,
    Guid? AcquisitionBankAccountId = null,
    Guid? AcquisitionTaxCodeId = null,
    DateOnly? AcquisitionDueDate = null,
    decimal OpeningAccumulatedDepreciation = 0m,
    DateOnly? OpeningDepreciatedThrough = null,
    string? Notes = null,
    IReadOnlyList<AssetBookValueRequest>? Books = null,
    JsonElement? CustomFields = null);

public sealed record ScheduleLineSummary(Guid Id, DateOnly PeriodStart, DateOnly PeriodEnd, decimal Weight, decimal PlannedAmount, decimal PostedAmount, string Status, Guid? RunId);

public sealed record AssetBookValueSummary(
    Guid Id,
    Guid BookId,
    string BookCode,
    bool PostsToGl,
    string Method,
    int LifeMonths,
    decimal SalvageValue,
    decimal? RatePct,
    string Convention,
    DateOnly DepreciationStart,
    decimal? TotalUnits,
    decimal Cost,
    decimal AccumulatedDepreciation,
    decimal AccumulatedImpairment,
    decimal RevaluationSurplus,
    decimal NetBookValue,
    decimal MonthsConsumed,
    DateOnly? DepreciatedThrough,
    string Status,
    IReadOnlyList<ScheduleLineSummary>? Schedule);

public sealed record AssetTransactionSummary(
    Guid Id,
    Guid BookId,
    string BookCode,
    DateOnly PostingDate,
    string Kind,
    decimal CostDelta,
    decimal AccumulatedDepreciationDelta,
    decimal ImpairmentDelta,
    decimal SurplusDelta,
    decimal Proceeds,
    decimal GainLoss,
    Guid? JournalEntryId,
    Guid? ReversesId,
    Guid? RunId,
    string? Reference,
    DateTimeOffset CreatedAt);

public sealed record AssetVerificationSummary(Guid Id, DateOnly VerifiedOn, Guid? VerifiedBy, string Condition, string? LocationNote, string? Note);

public sealed record AssetSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    IReadOnlyDictionary<string, string> Name,
    string? Description,
    Guid CategoryId,
    string CategoryCode,
    IReadOnlyDictionary<string, string> CategoryName,
    Guid? ParentAssetId,
    DateOnly AcquisitionDate,
    DateOnly InServiceDate,
    decimal Cost,
    string Currency,
    decimal Quantity,
    string? Unit,
    Guid? BranchId,
    Guid? DimensionSetId,
    IReadOnlyDictionary<string, Guid>? Dimensions,
    Guid? CustodianMembershipId,
    string? SerialNumber,
    Guid? SupplierPartnerId,
    string? SupplierCode,
    string? SupplierReference,
    DateOnly? WarrantyUntil,
    string Status,
    string AcquisitionSource,
    Guid? AcquisitionBankAccountId,
    Guid? AcquisitionTaxCodeId,
    DateOnly? AcquisitionDueDate,
    decimal OpeningAccumulatedDepreciation,
    Guid? AcquisitionEntryId,
    DateOnly? DisposedOn,
    string? DisposalKind,
    string? Notes,
    JsonElement CustomFields,
    string? Method,
    int? LifeMonths,
    decimal AccumulatedDepreciation,
    decimal AccumulatedImpairment,
    decimal NetBookValue,
    DateOnly? DepreciatedThrough,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AssetBookValueSummary>? Books = null,
    IReadOnlyList<AssetTransactionSummary>? Transactions = null,
    IReadOnlyList<AssetVerificationSummary>? Verifications = null);

public sealed record AdditionRequest(decimal Amount, DateOnly Date, string Source, Guid? BankAccountId = null, Guid? SupplierPartnerId = null, DateOnly? DueDate = null, Guid? TaxCodeId = null, string? Reference = null, string? Description = null);

public sealed record ImpairmentRequest(decimal Amount, DateOnly Date, string Reason);

public sealed record RevaluationRequest(decimal FairValue, DateOnly Date, string Reason, int? NewLifeMonths = null);

public sealed record TransferRequest(DateOnly Date, Guid? BranchId = null, IReadOnlyDictionary<string, Guid>? Dimensions = null, Guid? CustodianMembershipId = null, string? Reason = null);

public sealed record DisposalRequest(DateOnly Date, string Kind, decimal Proceeds = 0m, string ProceedsSource = "none", Guid? BankAccountId = null, Guid? CustomerPartnerId = null, DateOnly? DueDate = null, decimal? Quantity = null, string? Reason = null);

public sealed record VerificationRequest(DateOnly VerifiedOn, string Condition = "good", string? LocationNote = null, string? Note = null);

public sealed record UsageRequest(Guid BookId, DateOnly Month, decimal Units, string? Note = null);

public sealed record ChangeEstimatesRequest(Guid BookId, int? LifeMonths = null, decimal? SalvageValue = null, string? Method = null, decimal? RatePct = null, decimal? TotalUnits = null);

public sealed record DepreciationRunRequest(Guid CompanyId, DateOnly Through, Guid? BookId = null, DateOnly? PostingDate = null);

public sealed record ReverseDepreciationRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record DepreciationRunSummary(
    Guid Id,
    Guid CompanyId,
    Guid BookId,
    string BookCode,
    string Number,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly PostingDate,
    string Status,
    Guid? JournalEntryId,
    Guid? ReversalEntryId,
    int AssetCount,
    decimal Total,
    DateTimeOffset CreatedAt);

public sealed record RegisterRow(
    Guid AssetId,
    string Number,
    IReadOnlyDictionary<string, string> Name,
    string CategoryCode,
    string Status,
    DateOnly AcquisitionDate,
    decimal Cost,
    decimal AccumulatedDepreciation,
    decimal AccumulatedImpairment,
    decimal NetBookValue,
    decimal DepreciationInRange);

public sealed record RegisterTotals(decimal Cost, decimal AccumulatedDepreciation, decimal AccumulatedImpairment, decimal NetBookValue, decimal DepreciationInRange);

public sealed record RegisterReport(Guid CompanyId, Guid BookId, string BookCode, DateOnly From, DateOnly AsOf, string Currency, IReadOnlyList<RegisterRow> Rows, RegisterTotals Totals);

public sealed record ForecastRow(DateOnly PeriodStart, DateOnly PeriodEnd, decimal Planned, decimal Posted, int Assets);

public sealed record ReconciliationRow(string Role, decimal Register);

public sealed record ReconciliationReport(Guid CompanyId, Guid BookId, string Currency, IReadOnlyList<ReconciliationRow> Rows, bool LedgerAgrees, IReadOnlyList<string> Problems);

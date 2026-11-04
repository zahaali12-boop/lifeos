using System.Text.Json;
using Quicker.FixedAssets.Contracts;
using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.FixedAssets.Domain;

/// <summary>A set of values kept for every asset of a company: the book that posts to the ledger, and memo books (a tax book) that only compute.</summary>
public sealed class AssetBook : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public LocalizedText Name { get; set; } = new();
    public bool PostsToGl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What kind of asset: the defaults its assets start with (method, life, salvage, convention) and the accounts its postings land on.</summary>
public sealed class AssetCategory : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public LocalizedText Name { get; set; } = new();
    public string DefaultMethod { get; set; } = DepreciationMethods.StraightLine;
    public int DefaultLifeMonths { get; set; } = 60;
    public decimal DefaultSalvagePct { get; set; }
    public decimal? DefaultRatePct { get; set; }
    public string Convention { get; set; } = DepreciationConventions.FullMonth;
    public string? CostAccountCode { get; set; }
    public string? AccumulatedDepreciationAccountCode { get; set; }
    public string? DepreciationExpenseAccountCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One asset of the register, in the company's functional currency; its values per book sit in <see cref="AssetBookValue"/>.</summary>
public sealed class Asset : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Number { get; set; } = string.Empty;
    public LocalizedText Name { get; set; } = new();
    public string? Description { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? ParentAssetId { get; set; }
    public DateOnly AcquisitionDate { get; set; }
    public DateOnly InServiceDate { get; set; }
    public decimal Cost { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public string? Unit { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DimensionSetId { get; set; }
    public Guid? CustodianMembershipId { get; set; }
    public string? SerialNumber { get; set; }
    public Guid? SupplierPartnerId { get; set; }
    public string? SupplierReference { get; set; }
    public DateOnly? WarrantyUntil { get; set; }
    public string Status { get; set; } = AssetStatuses.Draft;
    public string AcquisitionSource { get; set; } = AcquisitionSources.Supplier;
    public Guid? AcquisitionBankAccountId { get; set; }
    public Guid? AcquisitionTaxCodeId { get; set; }
    public DateOnly? AcquisitionDueDate { get; set; }
    public decimal OpeningAccumulatedDepreciation { get; set; }
    public DateOnly? OpeningDepreciatedThrough { get; set; }
    public Guid? AcquisitionEntryId { get; set; }
    public DateOnly? DisposedOn { get; set; }
    public string? DisposalKind { get; set; }
    public string? Notes { get; set; }
    public JsonElement CustomFields { get; set; } = JsonDocument.Parse("{}").RootElement;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<AssetBookValue> Books { get; } = [];
}

/// <summary>The asset as one book sees it: its method, life and salvage, and the running cost, depreciation, impairment and surplus.</summary>
public sealed class AssetBookValue : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public Guid BookId { get; set; }
    public string Method { get; set; } = DepreciationMethods.StraightLine;
    public int LifeMonths { get; set; }
    public decimal SalvageValue { get; set; }
    public decimal? RatePct { get; set; }
    public string Convention { get; set; } = DepreciationConventions.FullMonth;
    public DateOnly DepreciationStart { get; set; }
    public decimal? TotalUnits { get; set; }
    public decimal Cost { get; set; }
    public decimal AccumulatedDepreciation { get; set; }
    public decimal AccumulatedImpairment { get; set; }
    public decimal RevaluationSurplus { get; set; }
    /// <summary>Impairment losses taken to profit or loss over the asset's life, the most a later revaluation may reverse through profit or loss.</summary>
    public decimal ImpairmentRecognised { get; set; }
    /// <summary>Weighted months of life already depreciated (a half month counts a half), so a prospective plan knows what is left.</summary>
    public decimal MonthsConsumed { get; set; }
    public DateOnly? DepreciatedThrough { get; set; }
    public string Status { get; set; } = AssetStatuses.Draft;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ScheduleLine> Schedule { get; } = [];

    public decimal NetBookValue => Cost - AccumulatedDepreciation - AccumulatedImpairment;
}

/// <summary>One month of an asset's depreciation plan in a book: what is planned, what was posted, by which run.</summary>
public sealed class ScheduleLine : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid AssetBookId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal Weight { get; set; } = 1m;
    public decimal PlannedAmount { get; set; }
    public decimal PostedAmount { get; set; }
    public string Status { get; set; } = ScheduleStatuses.Planned;
    public Guid? TransactionId { get; set; }
    public Guid? RunId { get; set; }
}

public static class ScheduleStatuses
{
    public const string Planned = "planned";
    public const string Posted = "posted";
    public const string Skipped = "skipped";
}

/// <summary>The FA subledger: every movement of an asset's cost, accumulated depreciation, impairment and surplus in a book, with the journal that carries it when the book posts.</summary>
public sealed class AssetTransaction : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public Guid BookId { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Kind { get; set; } = string.Empty;
    public decimal CostDelta { get; set; }
    public decimal AccumulatedDepreciationDelta { get; set; }
    public decimal ImpairmentDelta { get; set; }
    public decimal SurplusDelta { get; set; }
    public decimal Proceeds { get; set; }
    public decimal GainLoss { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversesId { get; set; }
    public Guid? RunId { get; set; }
    public string? Reference { get; set; }
    public string Detail { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One posting of a book's depreciation through a month: the journal it wrote and the assets it covered.</summary>
public sealed class DepreciationRun : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BookId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Status { get; set; } = "posted";
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public int AssetCount { get; set; }
    public decimal Total { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Units an asset produced (hours run, kilometres driven) in a month, for the units-of-production method.</summary>
public sealed class AssetUsage : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid AssetBookId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public decimal Units { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A physical verification: who saw the asset where, in what condition, when.</summary>
public sealed class AssetVerification : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public DateOnly VerifiedOn { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string Condition { get; set; } = "good";
    public string? LocationNote { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

namespace Quicker.FixedAssets.Contracts;

/// <summary>The documents the module numbers and posts.</summary>
public static class FixedAssetDocumentTypes
{
    /// <summary>An asset of the register (numbered when it is created; its acquisition posts when it is activated).</summary>
    public const string Asset = "fa_asset";

    /// <summary>One depreciation run of a book through a month: one journal, one transaction per asset.</summary>
    public const string DepreciationRun = "fa_depreciation_run";

    /// <summary>The disposal of an asset (or part of it): the open item its proceeds may open carries this type.</summary>
    public const string Disposal = "fa_disposal";
}

public static class DepreciationMethods
{
    public const string StraightLine = "straight_line";
    public const string DecliningBalance = "declining_balance";
    public const string DoubleDeclining = "double_declining";
    public const string SumOfYears = "sum_of_years";
    public const string UnitsOfProduction = "units_of_production";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [StraightLine, DecliningBalance, DoubleDeclining, SumOfYears, UnitsOfProduction, None];
}

/// <summary>How the first (and so the last) month of an asset's life counts.</summary>
public static class DepreciationConventions
{
    /// <summary>A full month of depreciation in the month the asset enters service, none in the month it leaves.</summary>
    public const string FullMonth = "full_month";

    /// <summary>Half a month in the month it enters service and half in the month after its life, half in the month it leaves.</summary>
    public const string MidMonth = "mid_month";

    /// <summary>Pro rata by days in the first month and the last; pro rata by days in the month it leaves.</summary>
    public const string Daily = "daily";

    /// <summary>Depreciation starts the month after the asset enters service and runs through the month it leaves.</summary>
    public const string NextMonth = "next_month";

    public static readonly IReadOnlyList<string> All = [FullMonth, MidMonth, Daily, NextMonth];
}

public static class AssetStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string FullyDepreciated = "fully_depreciated";
    public const string Disposed = "disposed";

    public static readonly IReadOnlyList<string> All = [Draft, Active, FullyDepreciated, Disposed];
}

/// <summary>Where the money for an acquisition (or an addition) comes from, which is the credit side of its journal.</summary>
public static class AcquisitionSources
{
    /// <summary>Owed to a supplier: an AP open item on the partner, due on the due date.</summary>
    public const string Supplier = "supplier";

    /// <summary>Paid from a bank or cash account at once.</summary>
    public const string Bank = "bank";

    /// <summary>Already invoiced to the fixed-asset clearing account (a purchase invoice line on the FaClearing role).</summary>
    public const string Clearing = "clearing";

    /// <summary>An asset held before go-live: cost and accumulated depreciation to date against opening balance equity.</summary>
    public const string Opening = "opening";

    public static readonly IReadOnlyList<string> All = [Supplier, Bank, Clearing, Opening];
}

public static class AssetTransactionKinds
{
    public const string Acquisition = "acquisition";
    public const string Addition = "addition";
    public const string Depreciation = "depreciation";
    public const string Impairment = "impairment";
    public const string Revaluation = "revaluation";
    public const string Transfer = "transfer";
    public const string Disposal = "disposal";
    public const string Reversal = "reversal";

    public static readonly IReadOnlyList<string> All = [Acquisition, Addition, Depreciation, Impairment, Revaluation, Transfer, Disposal, Reversal];
}

public static class DisposalKinds
{
    public const string Sale = "sale";
    public const string Scrap = "scrap";

    public static readonly IReadOnlyList<string> All = [Sale, Scrap];
}

/// <summary>Where the proceeds of a sale land.</summary>
public static class ProceedsSources
{
    public const string Bank = "bank";
    public const string Customer = "customer";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Bank, Customer, None];
}

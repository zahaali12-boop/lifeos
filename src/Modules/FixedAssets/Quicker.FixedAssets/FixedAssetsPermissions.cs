using Quicker.Identity.Contracts;

namespace Quicker.FixedAssets;

/// <summary>Permission keys of the asset register, its postings, the depreciation runs and the set-up; the accountant template grants <c>fixed_assets.*</c>.</summary>
public static class FixedAssetsPermissions
{
    public const string AssetRead = "fixed_assets.asset.read";
    public const string AssetManage = "fixed_assets.asset.manage";
    public const string AssetPost = "fixed_assets.asset.post";
    public const string DepreciationRun = "fixed_assets.depreciation.run";
    public const string SetupManage = "fixed_assets.setup.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(AssetRead, "fixed_assets", "Read the asset register, books, schedules, runs and the reconciliation to the ledger"),
        new(AssetManage, "fixed_assets", "Create and edit draft assets, record verifications and usage, change estimates"),
        new(AssetPost, "fixed_assets", "Activate assets and post additions, impairments, revaluations, transfers and disposals"),
        new(DepreciationRun, "fixed_assets", "Post and reverse depreciation runs"),
        new(SetupManage, "fixed_assets", "Define asset categories (defaults and accounts) and books"),
    ];
}

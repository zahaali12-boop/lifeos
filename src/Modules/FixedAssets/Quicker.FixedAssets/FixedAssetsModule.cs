using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.FixedAssets.Application;
using Quicker.FixedAssets.Contracts;
using Quicker.FixedAssets.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;

namespace Quicker.FixedAssets;

public static class FixedAssetsModule
{
    public static IServiceCollection AddFixedAssetsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Entries of this module belong to its documents; the FA subledger is fa_transactions, kept by the module itself (A-140).
        services.AddSingleton(new PostingDocumentModule("fixed_assets"));
        PermissionCatalog.Register(FixedAssetsPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(FixedAssetDocumentTypes.Asset, FixedAssetsPermissions.AssetRead), new NumberedDocumentType(FixedAssetDocumentTypes.DepreciationRun, FixedAssetsPermissions.AssetRead));
        CustomFieldHosts.Register(new CustomFieldHost(FixedAssetDocumentTypes.Asset, "app.fa_assets", "custom_fields"));
        services.AddModuleDbContext<FixedAssetsDbContext>();
        services.AddScoped<BookService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<DepreciationService>();
        services.AddScoped<AssetService>();
        services.AddJobHandler<FixedAssetsMonthlyJob, FixedAssetsMonthlyPayload>();
        services.AddSingleton(new RecordReadPermission("fixed_asset", FixedAssetsPermissions.AssetRead));
        services.AddSingleton(new RecordReadPermission("depreciation_run", FixedAssetsPermissions.AssetRead));
        services.AddSingleton(new RecordReadPermission("asset_category", FixedAssetsPermissions.AssetRead));
        services.AddSingleton(new RecordReadPermission("asset_book", FixedAssetsPermissions.AssetRead));
        services.AddScoped<IRecordCompanies, FixedAssetsRecordCompanies>();
        return services;
    }
}

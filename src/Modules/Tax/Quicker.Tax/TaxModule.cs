using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Tax.Application;
using Quicker.Tax.Contracts;
using Quicker.Tax.Persistence;

namespace Quicker.Tax;

public static class TaxModule
{
    public static IServiceCollection AddTaxModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(TaxPermissions.All);
        services.AddModuleDbContext<TaxDbContext>();
        services.AddScoped<TaxAccess>();
        services.AddScoped<TaxSetupService>();
        services.AddScoped<TaxDeterminationService>();
        services.AddScoped<ITaxDetermination>(static sp => sp.GetRequiredService<TaxDeterminationService>());
        services.AddScoped<ITaxDirectory, TaxDirectory>();
        services.AddScoped<TaxLedgerService>();
        services.AddScoped<ITaxLedger>(static sp => sp.GetRequiredService<TaxLedgerService>());
        services.AddScoped<TaxReturnService>();
        services.AddScoped<UblExportService>();
        services.AddScoped<ClearanceService>();
        services.AddSingleton<ITaxClearanceRegistry, TaxClearanceRegistry>();
        return services;
    }
}

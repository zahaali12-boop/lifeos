using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Pricing.Application;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Persistence;

namespace Quicker.Pricing;

public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(PricingPermissions.All);
        services.AddModuleDbContext<PricingDbContext>();
        services.AddScoped<PricingAccess>();
        services.AddScoped<PricingRefs>();
        services.AddScoped<PricingService>();
        services.AddScoped<IPricing>(static sp => sp.GetRequiredService<PricingService>());
        services.AddScoped<PriceListService>();
        services.AddScoped<PricingRulesService>();
        return services;
    }
}

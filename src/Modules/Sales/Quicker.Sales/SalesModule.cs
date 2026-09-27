using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Sales.Application;
using Quicker.Sales.Persistence;

namespace Quicker.Sales;

public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(SalesPermissions.All);
        services.AddModuleDbContext<SalesDbContext>();
        services.AddScoped<QuotationService>();
        return services;
    }
}

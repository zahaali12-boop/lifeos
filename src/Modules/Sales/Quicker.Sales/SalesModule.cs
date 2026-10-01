using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Sales.Application;
using Quicker.Sales.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Sales;

public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(SalesPermissions.All);
        services.AddModuleDbContext<SalesDbContext>();
        services.AddScoped<QuotationService>();
        services.AddScoped<OrderService>();
        services.AddScoped<ShipmentService>();
        services.AddScoped<IWorkflowSubjectProvider, SalesOrderWorkflowSubject>();
        return services;
    }
}

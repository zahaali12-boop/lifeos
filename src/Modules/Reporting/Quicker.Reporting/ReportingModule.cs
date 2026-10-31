using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Messaging;
using Quicker.Reporting.Application;
using Quicker.Reporting.Application.Facts;
using Quicker.Reporting.Application.Semantic;

namespace Quicker.Reporting;

public static class ReportingModule
{
    /// <summary>Read models (the home dashboard, the sales lines) and the semantic layer of ADR-0034: the projected facts, the catalogue, the query engine.</summary>
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(ReportingPermissions.All);
        services.AddScoped<HomeDashboardService>();
        services.AddScoped<SalesLinesService>();
        services.AddScoped<ProjectionService>();
        services.AddScoped<QueryService>();
        services.AddJobHandler<ProjectionJob, ProjectionPayload>();
        // A posting kicks the tenant's projection; the platform schedule covers every tenant every two minutes.
        services.AddIntegrationEventHandler<ProjectOnJournalPosted, JournalEntryPosted>();
        services.AddIntegrationEventHandler<ProjectOnStockPosted, StockPosted>();
        return services;
    }
}

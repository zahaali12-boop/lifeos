using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Contracts;
using Quicker.Reporting.Application;
using Quicker.Reporting.Application.Dashboards;
using Quicker.Reporting.Application.Facts;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Schedules;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Persistence;

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
        services.AddModuleDbContext<ReportingDbContext>();
        services.AddScoped<ReportService>();
        services.AddScoped<LayoutService>();
        services.AddScoped<StatementService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<ScheduleRunner>();
        services.AddScoped<RunRecorder>();
        services.AddScoped<IPrintModelProvider, ReportPrintModel>();
        services.AddScoped<IPrintModelProvider, FinancialStatementPrintModel>();
        services.AddSingleton(new RecordReadPermission(DashboardService.AuditEntity, ReportingPermissions.ReportRun));
        services.AddSingleton(new RecordReadPermission(ScheduleService.AuditEntity, ReportingPermissions.ReportRun));
        services.AddJobHandler<ReportRunJob, ReportRunPayload>();
        services.AddSingleton(new RecordReadPermission(LayoutService.AuditEntity, ReportingPermissions.ReportRun));
        services.AddSingleton(new RecordReadPermission(ReportService.AuditEntity, ReportingPermissions.ReportRun));
        services.AddScoped<IRecordCompanies, ReportingRecordCompanies>();
        services.AddJobHandler<ProjectionJob, ProjectionPayload>();
        // A posting kicks the tenant's projection; the platform schedule covers every tenant every two minutes.
        services.AddIntegrationEventHandler<ProjectOnJournalPosted, JournalEntryPosted>();
        services.AddIntegrationEventHandler<ProjectOnStockPosted, StockPosted>();
        return services;
    }
}

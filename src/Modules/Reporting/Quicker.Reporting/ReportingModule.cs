using Microsoft.Extensions.DependencyInjection;
using Quicker.Reporting.Application;

namespace Quicker.Reporting;

public static class ReportingModule
{
    /// <summary>Read models only: no tables of its own yet, no permissions of its own (each section of a read model is guarded by the module it reads).</summary>
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<HomeDashboardService>();
        services.AddScoped<SalesLinesService>();
        return services;
    }
}

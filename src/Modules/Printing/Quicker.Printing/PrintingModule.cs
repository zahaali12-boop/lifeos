using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Application;
using Quicker.Printing.Persistence;

namespace Quicker.Printing;

public static class PrintingModule
{
    public static IServiceCollection AddPrintingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionCatalog.Register(PrintingPermissions.All);
        services.AddModuleDbContext<PrintingDbContext>();
        services.AddSingleton(configuration.GetSection(PrintOptions.SectionName).Get<PrintOptions>() ?? new PrintOptions());
        services.AddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        services.AddScoped<TemplateService>();
        services.AddScoped<PrintService>();
        services.AddScoped<ShareService>();
        services.AddScoped<PrintFailureRecorder>();
        services.AddJobHandler<PrintRenderJob, PrintRenderPayload>();
        services.AddJobHandler<PrintEmailJob, PrintEmailPayload>();
        return services;
    }
}

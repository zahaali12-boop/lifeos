using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Einvoicing.Application;
using Quicker.Einvoicing.Persistence;
using Quicker.Einvoicing.Zatca;
using Quicker.Identity.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Tax.Contracts;
using Quicker.Web;

namespace Quicker.Einvoicing;

public static class EinvoicingModule
{
    public static IServiceCollection AddEinvoicingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionCatalog.Register(EinvoicingPermissions.All);
        services.Configure<ZatcaOptions>(configuration.GetSection(ZatcaOptions.Section));
        services.AddModuleDbContext<EinvoicingDbContext>();
        services.AddSingleton<ZatcaSimulator>();
        services.AddHttpClient(ZatcaGateway.HttpClientName, static client => client.Timeout = TimeSpan.FromSeconds(60)).RestrictToPublicNetworks();
        services.AddScoped<ZatcaGateway>();
        services.AddScoped<SubmissionService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<ITaxClearanceProvider, ZatcaClearanceProvider>();
        services.AddSingleton(new RecordReadPermission(DeviceService.AuditEntity, EinvoicingPermissions.DeviceRead));
        services.AddSingleton(new RecordReadPermission(SubmissionService.AuditEntity, EinvoicingPermissions.SubmissionRead));
        services.AddScoped<IRecordCompanies, EinvoicingRecordCompanies>();
        return services;
    }
}

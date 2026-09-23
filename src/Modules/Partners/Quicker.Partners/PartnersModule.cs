using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Partners.Application;
using Quicker.Partners.Contracts;
using Quicker.Partners.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Tenancy.Contracts;

namespace Quicker.Partners;

public static class PartnersModule
{
    public static IServiceCollection AddPartnersModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(PartnersPermissions.All);
        CustomFieldHosts.Register(new CustomFieldHost("partner", "app.ptr_partners", "custom_fields"));
        services.AddModuleDbContext<PartnersDbContext>();
        services.AddScoped<PartnerService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<PartnerDirectory>();
        services.AddScoped<IPartnerDirectory>(static sp => sp.GetRequiredService<PartnerDirectory>());
        services.AddScoped<ITenantSetupStep, PartnersDefaults>();
        return services;
    }
}

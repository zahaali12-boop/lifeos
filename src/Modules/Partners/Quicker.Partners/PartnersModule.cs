using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
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
        CustomFieldHosts.Register(new CustomFieldHost(OpportunityService.EntityType, "app.ptr_opportunities", "custom_fields"));
        NumberedDocumentTypes.Register(new NumberedDocumentType(OpportunityService.EntityType, PartnersPermissions.CustomerRead));
        services.AddModuleDbContext<PartnersDbContext>();
        services.AddScoped<PartnerService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<SalesSetupService>();
        services.AddScoped<OpportunityService>();
        services.AddScoped<CrmActivityService>();
        services.AddScoped<CustomerDirectory>();
        services.AddScoped<ICustomerDirectory>(static sp => sp.GetRequiredService<CustomerDirectory>());
        services.AddScoped<IRecordCompanies, PartnersRecordCompanies>();
        services.AddScoped<PartnerDirectory>();
        services.AddScoped<IPartnerDirectory>(static sp => sp.GetRequiredService<PartnerDirectory>());
        services.AddScoped<ITenantSetupStep, PartnersDefaults>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("partner", PartnersPermissions.SupplierRead));
        services.AddSingleton(new RecordReadPermission("partner", PartnersPermissions.CustomerRead));
        services.AddSingleton(new RecordReadPermission(OpportunityService.EntityType, PartnersPermissions.CustomerRead));
        return services;
    }
}

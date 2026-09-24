using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Organization.Application;
using Quicker.Organization.Contracts;
using Quicker.Organization.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Tenancy.Contracts;

namespace Quicker.Organization;

public static class OrganizationModule
{
    public static IServiceCollection AddOrganizationModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionCatalog.Register(OrganizationPermissions.All);
        CustomFieldHosts.Register(new CustomFieldHost("company", "app.org_companies", "custom_fields"));

        services.Configure<RateProviderOptions>(configuration.GetSection(RateProviderOptions.SectionName));
        services.AddHttpClient<EcbRateProvider>(static client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<OpenExchangeRatesProvider>(static client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IExchangeRateProvider>(static sp => sp.GetRequiredService<EcbRateProvider>());
        services.AddScoped<IExchangeRateProvider>(static sp => sp.GetRequiredService<OpenExchangeRatesProvider>());

        services.AddModuleDbContext<OrganizationDbContext>();

        services.AddScoped<CompanyService>();
        services.AddScoped<ICompanyDirectory>(static sp => sp.GetRequiredService<CompanyService>());
        services.AddScoped<ICompanySettings>(static sp => sp.GetRequiredService<CompanyService>());
        services.AddScoped<FiscalCalendarService>();
        services.AddScoped<IFiscalPeriodResolver>(static sp => sp.GetRequiredService<FiscalCalendarService>());
        services.AddScoped<IPostingWindows>(static sp => sp.GetRequiredService<FiscalCalendarService>());
        services.AddScoped<CurrencyService>();
        services.AddScoped<IExchangeRateResolver>(static sp => sp.GetRequiredService<CurrencyService>());
        services.AddScoped<DimensionService>();
        services.AddScoped<IDimensionSets>(static sp => sp.GetRequiredService<DimensionService>());
        services.AddScoped<IDimensionDirectory>(static sp => sp.GetRequiredService<DimensionService>());
        services.AddScoped<UomService>();
        services.AddScoped<IUomConversions>(static sp => sp.GetRequiredService<UomService>());
        services.AddScoped<IUomDirectory>(static sp => sp.GetRequiredService<UomService>());
        services.AddScoped<BusinessCalendarService>();
        services.AddScoped<IWorkingDayCalendar>(static sp => sp.GetRequiredService<BusinessCalendarService>());
        services.AddScoped<ITenantSetupStep, OrganizationDefaults>();
        services.AddJobHandler<RateImportJob, ImportRatesRequest>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("company", OrganizationPermissions.CompanyRead));
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Quicker.Organization;

public static class OrganizationModule
{
    public static IServiceCollection AddOrganizationModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Placeholder for M1.6 services. Data queries will be added after the domain model is materialized in tests.
        // Initial focus: schema validation and RLS tests to ensure tenant isolation.

        return services;
    }
}

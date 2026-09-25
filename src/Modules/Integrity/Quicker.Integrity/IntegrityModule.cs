using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Integrity.Application;
using Quicker.Integrity.Contracts;
using Quicker.Messaging;

namespace Quicker.Integrity;

public static class IntegrityPermissions
{
    public const string Run = "platform.integrity.run";

    public static readonly PermissionDefinition[] All =
    [
        new(Run, "platform", "Run the invariant harness for the tenant or a company and read its report", IsSensitive: true),
    ];
}

public static class IntegrityModule
{
    public static IServiceCollection AddIntegrityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(IntegrityPermissions.All);
        services.AddScoped<InvariantHarness>();
        services.AddScoped<IInvariantHarness>(static sp => sp.GetRequiredService<InvariantHarness>());
        services.AddJobHandler<IntegrityCheckAllJob, IntegrityCheckAllPayload>();
        return services;
    }
}

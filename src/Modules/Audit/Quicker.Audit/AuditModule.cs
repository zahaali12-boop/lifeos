using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quicker.Audit.Application;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Messaging;

namespace Quicker.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionCatalog.Register(AuditPermissions.All);

        services.Configure<AuditOptions>(configuration.GetSection(AuditOptions.SectionName));
        services.AddSingleton(static sp => sp.GetRequiredService<IOptions<AuditOptions>>().Value);
        services.AddSingleton<IAuditAnchorStore>(static sp =>
        {
            var anchoring = sp.GetRequiredService<AuditOptions>().Anchoring;
            return anchoring.Store switch
            {
                "file" => new FileAuditAnchorStore(anchoring.Path),
                _ => throw new InvalidOperationException($"Unknown audit anchor store '{anchoring.Store}'. Supported: file."),
            };
        });

        // One writer per unit of work; the interceptor feeds it and the host commits it with the transaction.
        services.AddScoped<AuditWriter>();
        services.AddScoped<IAuditSink>(static sp => sp.GetRequiredService<AuditWriter>());
        services.AddScoped<IInterceptor, AuditSaveChangesInterceptor>();

        services.AddScoped<AuditQueries>();
        services.AddScoped<ChainAnchoring>();
        services.AddScoped<ChainVerifier>();
        services.AddSingleton<AuditChainJobs>();
        services.AddJobHandler<AuditAnchorAllJob, AuditAnchorAllPayload>();
        services.AddJobHandler<AuditVerifyAllJob, AuditVerifyAllPayload>();
        return services;
    }
}

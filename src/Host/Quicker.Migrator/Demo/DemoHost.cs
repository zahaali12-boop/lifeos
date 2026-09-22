using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quicker.Accounting;
using Quicker.Audit;
using Quicker.Collaboration;
using Quicker.Identity;
using Quicker.Integration;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering;
using Quicker.Organization;
using Quicker.Persistence;
using Quicker.Storage;
using Quicker.Tenancy;
using Quicker.Web;

namespace Quicker.Migrator.Demo;

/// <summary>
/// The module composition of the API and worker hosts without their HTTP pipeline or background loops, so the
/// seeder runs the very services an administrator would: sign-up rules, company validation, audit trail, outbox.
/// </summary>
internal static class DemoHost
{
    public static IHost Build(string ownerConnection, string appConnection, IClock clock)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error); // query-shape advice is the modules' concern, not the seeder's output
        var scratch = Path.Combine(Path.GetTempPath(), "quicker-demo-seed");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Quicker:Db:OwnerConnection"] = ownerConnection,
            ["Quicker:Db:AppConnection"] = appConnection,
            ["Quicker:Db:StatementTimeoutSeconds"] = "120",
            ["Quicker:Db:LockTimeoutSeconds"] = "30",
            ["Quicker:Email:Provider"] = "capture",
            ["Quicker:Storage:Provider"] = "filesystem",
            ["Quicker:Storage:Path"] = Path.Combine(scratch, "storage"),
            ["Quicker:Audit:Anchoring:Path"] = Path.Combine(scratch, "audit-anchors"),
        });

        var services = builder.Services;
        var configuration = builder.Configuration;
        services.Configure<DbOptions>(configuration.GetSection(DbOptions.SectionName));
        services.AddSingleton(static sp => sp.GetRequiredService<IOptions<DbOptions>>().Value);
        services.AddSingleton(static sp => DataSources.ForApp(sp.GetRequiredService<DbOptions>()));
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        services.AddSingleton(clock);
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddQuickerEmail(configuration);
        services.AddQuickerStorage(configuration);
        services.AddQuickerWebCore();
        services.AddQuickerMessaging(configuration);

        services.AddTenancyModule();
        services.AddAuditModule(configuration);
        services.AddIdentityModule(configuration);
        services.AddOrganizationModule(configuration);
        services.AddNumberingModule();
        services.AddIntegrationModule();
        services.AddCollaborationModule();
        services.AddAccountingModule();
        return builder.Build();
    }
}

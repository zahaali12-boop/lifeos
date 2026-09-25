using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quicker.Accounting;
using Quicker.Audit;
using Quicker.Banking;
using Quicker.Collaboration;
using Quicker.Identity;
using Quicker.Identity.Contracts;
using Quicker.Integration;
using Quicker.Integrity;
using Quicker.Inventory;
using Quicker.Items;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering;
using Quicker.Organization;
using Quicker.Partners;
using Quicker.Payables;
using Quicker.Persistence;
using Quicker.Pricing;
using Quicker.Purchasing;
using Quicker.Storage;
using Quicker.Tenancy;
using Quicker.Web;
using Quicker.Workflow;

namespace Quicker.Migrator.Demo;

/// <summary>
/// The module composition of the API and worker hosts without their HTTP pipeline or background loops, so the
/// seeder runs the very services an administrator would: sign-up rules, company validation, audit trail, outbox.
/// </summary>
internal static class DemoHost
{
    /// <remarks>
    /// Maintenance commands that must read what the running system wrote (the audit anchor store) pass the
    /// deployment's settings, layered over the seeder's scratch defaults; the connections given here always win.
    /// </remarks>
    public static IHost Build(string ownerConnection, string appConnection, IClock clock, IConfiguration? deployment = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error); // query-shape advice is the modules' concern, not the seeder's output
        var scratch = Path.Combine(Path.GetTempPath(), "quicker-demo-seed");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Quicker:Db:StatementTimeoutSeconds"] = "120",
            ["Quicker:Db:LockTimeoutSeconds"] = "30",
            ["Quicker:Email:Provider"] = "capture",
            ["Quicker:Storage:Provider"] = "filesystem",
            ["Quicker:Storage:Path"] = Path.Combine(scratch, "storage"),
            ["Quicker:Audit:Anchoring:Path"] = Path.Combine(scratch, "audit-anchors"),
        });
        if (deployment is not null)
        {
            builder.Configuration.AddConfiguration(deployment);
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Quicker:Db:OwnerConnection"] = ownerConnection,
            ["Quicker:Db:AppConnection"] = appConnection,
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
        services.AddIntegrityModule();
        services.AddItemsModule();
        services.AddInventoryModule();
        services.AddWorkflowModule();
        services.AddPartnersModule();
        services.AddPricingModule();
        services.AddPayablesModule();
        services.AddBankingModule();
        services.AddPurchasingModule();

        // The seed stores no secrets (no bank account numbers, no provider keys), and the migrator is not given the
        // platform key: a protector that refuses keeps it that way instead of encrypting under a key the API would not
        // share.
        services.AddSingleton<ISecretProtector, RefusingSecretProtector>();
        return builder.Build();
    }

    private sealed class RefusingSecretProtector : ISecretProtector
    {
        public string ProtectString(string value) => throw new InvalidOperationException("The demo seed stores no secrets; the platform key is the API's.");

        public string UnprotectString(string protectedBase64) => throw new InvalidOperationException("The demo seed reads no secrets; the platform key is the API's.");
    }
}

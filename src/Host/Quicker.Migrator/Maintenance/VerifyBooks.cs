using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Migrator.Demo;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Migrator.Maintenance;

public sealed record TenantVerification(Guid TenantId, string Slug, InvariantReport Report);

public sealed record VerifyResult(IReadOnlyList<TenantVerification> Tenants)
{
    public bool Passed => Tenants.All(static t => t.Report.Passed);
}

/// <summary>
/// <c>verify [--tenant SLUG]</c>: the invariant harness (roadmap 2.6) over every active tenant, or one, each in its
/// own unit of work as the system actor, the way the daily <c>integrity.check_all</c> job runs it. The audit-chain
/// check reads the deployment's anchor store, so the host takes the deployment's configuration. Each tenant's
/// chain verification is recorded, as every verification is.
/// </summary>
public static class VerifyBooks
{
    public static async Task<VerifyResult> RunAsync(string ownerConnection, string appConnection, IConfiguration? deployment, string? tenantSlug, CancellationToken cancellationToken = default)
    {
        using var host = DemoHost.Build(ownerConnection, appConnection, SystemClock.Instance, deployment);
        var tenants = await InTenantAsync(host, null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        var selected = tenantSlug is null
            ? tenants.Where(static t => t.Status == "active").ToList()
            : tenants.Where(t => string.Equals(t.Slug, tenantSlug.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (tenantSlug is not null && selected.Count == 0)
        {
            throw new InvalidOperationException($"Tenant '{tenantSlug}' does not exist.");
        }

        var results = new List<TenantVerification>();
        foreach (var tenant in selected.OrderBy(static t => t.Slug, StringComparer.Ordinal))
        {
            var report = await InTenantAsync(host, tenant.Id.Value, static (sp, ct) => sp.GetRequiredService<IInvariantHarness>().RunAsync(null, ct), cancellationToken);
            results.Add(new TenantVerification(tenant.Id.Value, tenant.Slug, report));
        }

        return new VerifyResult(results);
    }

    public static void Print(VerifyResult result)
    {
        if (result.Tenants.Count == 0)
        {
            Console.WriteLine("No active tenant to verify.");
            return;
        }

        foreach (var tenant in result.Tenants)
        {
            Console.WriteLine($"Tenant '{tenant.Slug}': {(tenant.Report.Passed ? "every check passed" : "FAILED " + string.Join(", ", tenant.Report.FailedCodes))}");
            foreach (var check in tenant.Report.Checks)
            {
                Console.WriteLine($"  {(check.Passed ? "ok  " : "FAIL")} {check.Code}: {check.Summary}");
                foreach (var problem in check.Problems)
                {
                    Console.WriteLine($"         {problem}");
                }
            }
        }
    }

    private static async Task<T> InTenantAsync<T>(Microsoft.Extensions.Hosting.IHost host, Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "verify-" + Uuid7.New().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: cancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        var result = await work(services, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

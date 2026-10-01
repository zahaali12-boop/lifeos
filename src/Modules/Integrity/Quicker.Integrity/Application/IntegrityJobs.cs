using Microsoft.Extensions.DependencyInjection;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Integrity.Application;

public sealed record IntegrityCheckAllPayload(Guid? CompanyId = null);

/// <summary>
/// Platform schedule <c>integrity.daily</c>: the harness for every active tenant, each in its own unit of work as the
/// system actor; a tenant whose books, chain or isolation fail a check fails the job so operators see it.
/// Bound to a tenant (a tenant schedule), it runs for that tenant only inside the job's own unit of work.
/// </summary>
public sealed class IntegrityCheckAllJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, IInvariantHarness harness) : IJobHandler<IntegrityCheckAllPayload>
{
    public static string JobType => "integrity.check_all";

    public async Task<object?> ExecuteAsync(IntegrityCheckAllPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is not null)
        {
            var report = await harness.RunAsync(payload?.CompanyId, cancellationToken);
            return report.Passed ? new { tenants = 1, passed = 1, failed = 0 } : throw Failed([report]);
        }

        var reports = new List<InvariantReport>();
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            reports.Add(await InTenantAsync(tenantId, (sp, ct) => sp.GetRequiredService<IInvariantHarness>().RunAsync(payload?.CompanyId, ct), cancellationToken));
            await context.ReportProgressAsync(new { tenants = reports.Count, failed = reports.Count(static r => !r.Passed) }, cancellationToken);
        }

        var failing = reports.Where(static r => !r.Passed).ToList();
        return failing.Count == 0 ? new { tenants = reports.Count, passed = reports.Count, failed = 0 } : throw Failed(failing);
    }

    private static JobFailedException Failed(IReadOnlyList<InvariantReport> failing) =>
        new($"{failing.Count} tenant(s) failed the invariant harness: {string.Join("; ", failing.Select(static r => $"{r.TenantId}: {string.Join(", ", r.FailedCodes)}"))}");

    private async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        return tenants.Where(static t => t.Status == "active").Select(static t => t.Id.Value).ToList();
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Uuid7.New().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

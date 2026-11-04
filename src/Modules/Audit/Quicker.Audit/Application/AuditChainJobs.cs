using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Audit.Application;

/// <summary>
/// The platform-wide anchoring and verification runs (ADR-0015): one unit of work per tenant under a system
/// context, plus the platform chain. The worker host schedules them daily from M1.8; until then they run on demand.
/// </summary>
public sealed class AuditChainJobs(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext)
{
    public async Task<IReadOnlyList<AuditAnchor>> AnchorAllAsync(CancellationToken cancellationToken = default)
    {
        var anchors = new List<AuditAnchor>();
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            var anchor = await InChainAsync(tenantId, static (sp, ct) => sp.GetRequiredService<ChainAnchoring>().AnchorTenantAsync(ct), cancellationToken);
            if (anchor is not null)
            {
                anchors.Add(anchor);
            }
        }

        var platform = await InChainAsync(null, static (sp, ct) => sp.GetRequiredService<ChainAnchoring>().AnchorPlatformAsync(ct), cancellationToken);
        if (platform is not null)
        {
            anchors.Add(platform);
        }

        return anchors;
    }

    public async Task<IReadOnlyList<ChainVerification>> VerifyAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ChainVerification>();
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            results.Add(await InChainAsync(tenantId, static (sp, ct) => sp.GetRequiredService<ChainVerifier>().VerifyTenantAsync(ct), cancellationToken));
        }

        results.Add(await InChainAsync(null, static (sp, ct) => sp.GetRequiredService<ChainVerifier>().VerifyPlatformAsync(ct), cancellationToken));
        return results;
    }

    private async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        var tenants = await InChainAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        return tenants.Where(static t => t.Status is not "deleting").Select(static t => t.Id.Value).ToList();
    }

    /// <summary>Runs work in its own scope and unit of work bound to a tenant (system actor) or to no tenant.</summary>
    private async Task<T> InChainAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
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

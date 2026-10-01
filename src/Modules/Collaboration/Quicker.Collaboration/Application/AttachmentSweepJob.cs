using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Collaboration.Application;

public sealed record AttachmentSweepPayload;

/// <summary>
/// The daily sweep of attachment files nothing refers to (schedule <c>collaboration.attachment_sweep</c>): for one
/// workspace when queued inside it, otherwise for every active workspace, each in its own unit of work.
/// </summary>
public sealed class AttachmentSweepJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext) : IJobHandler<AttachmentSweepPayload>
{
    public static string JobType => "collaboration.attachments.sweep";

    public async Task<object?> ExecuteAsync(AttachmentSweepPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is { } only)
        {
            return new { removed = await InTenantAsync(only.Value, static (sp, ct) => sp.GetRequiredService<AttachmentService>().SweepOrphansAsync(ct), cancellationToken) };
        }

        var removed = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            removed += await InTenantAsync(tenant.Id.Value, static (sp, ct) => sp.GetRequiredService<AttachmentService>().SweepOrphansAsync(ct), cancellationToken);
        }

        return new { tenants = tenants.Count, removed };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
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

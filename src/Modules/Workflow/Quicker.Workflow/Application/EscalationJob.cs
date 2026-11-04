using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Workflow.Application;

public sealed record EscalationPayload;

/// <summary>The SLA timer (ADR-0020): for one tenant when queued inside it, otherwise for every active tenant, as the hourly schedule does.</summary>
public sealed class EscalationJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, WorkflowEngine engine) : IJobHandler<EscalationPayload>
{
    public static string JobType => "workflow.escalate";

    public async Task<object?> ExecuteAsync(EscalationPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is not null)
        {
            return new { escalated = await engine.EscalateDueAsync(cancellationToken) };
        }

        var total = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            total += await InTenantAsync(tenant.Id.Value, static (sp, ct) => sp.GetRequiredService<WorkflowEngine>().EscalateDueAsync(ct), cancellationToken);
        }

        return new { tenants = tenants.Count, escalated = total };
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

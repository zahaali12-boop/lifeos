using Quicker.Messaging.Jobs;

namespace Quicker.Audit.Application;

public sealed record AuditAnchorAllPayload;

/// <summary>Daily platform job (schedule <c>audit.anchor_daily</c>): anchors every tenant chain and the platform chain.</summary>
public sealed class AuditAnchorAllJob(AuditChainJobs jobs) : IJobHandler<AuditAnchorAllPayload>
{
    public static string JobType => "audit.anchor_all";

    public async Task<object?> ExecuteAsync(AuditAnchorAllPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        var anchors = await jobs.AnchorAllAsync(cancellationToken);
        return new { anchored = anchors.Count };
    }
}

public sealed record AuditVerifyAllPayload;

/// <summary>Daily platform job (schedule <c>audit.verify_daily</c>): verifies every chain; a broken chain fails the job so operators are alerted.</summary>
public sealed class AuditVerifyAllJob(AuditChainJobs jobs) : IJobHandler<AuditVerifyAllPayload>
{
    public static string JobType => "audit.verify_all";

    public async Task<object?> ExecuteAsync(AuditVerifyAllPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        var results = await jobs.VerifyAllAsync(cancellationToken);
        var broken = results.Where(static r => r.Status is not ("ok" or "empty")).ToList();
        if (broken.Count > 0)
        {
            throw new JobFailedException($"{broken.Count} chain(s) failed verification: {string.Join(", ", broken.Select(static b => $"{b.TenantId?.ToString() ?? "platform"}={b.Status}"))}");
        }

        return new { verified = results.Count };
    }
}

using Quicker.Kernel.Results;
using Quicker.Messaging;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Web;

namespace Quicker.Api;

/// <summary>Background work as seen by tenants (their jobs and schedules) and operators (every tenant, the outbox, dead letters).</summary>
public static class PlatformEndpoints
{
    public static RouteGroupBuilder MapPlatformEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var platform = api.MapGroup("/platform").WithTags("Platform").RequireAuthorization();

        var jobs = platform.MapGroup("/jobs");
        jobs.MapGet("/", async (string? state, string? type, int? limit, bool? allTenants, CurrentPrincipal current, JobAdmin admin, CancellationToken ct) =>
            TypedResults.Ok(await admin.ListAsync(current.Required.TenantId.Value, allTenants == true && current.Required.IsPlatformOperator, state, type, limit ?? 100, ct)))
            .RequirePermission(PlatformPermissions.JobRead);
        jobs.MapGet("/types", (JobAdmin admin) => Results.Ok(admin.JobTypes.OrderBy(static t => t, StringComparer.Ordinal)))
            .RequirePermission(PlatformPermissions.JobRead);
        jobs.MapGet("/{jobId:guid}", async (Guid jobId, CurrentPrincipal current, JobAdmin admin, CancellationToken ct) =>
            ApiProblems.Found(await admin.GetAsync(jobId, current.Required.TenantId.Value, current.Required.IsPlatformOperator, ct), "job", jobId))
            .RequirePermission(PlatformPermissions.JobRead);
        jobs.MapPost("/{jobId:guid}/retry", async (Guid jobId, CurrentPrincipal current, JobAdmin admin, CancellationToken ct) =>
            await admin.RetryAsync(jobId, current.Required.TenantId.Value, current.Required.IsPlatformOperator, ct) ? Results.NoContent() : ApiProblems.From(Error.Conflict("job.not_retryable", "Only failed or dead jobs can be retried.")))
            .RequirePermission(PlatformPermissions.JobManage);
        jobs.MapPost("/{jobId:guid}/cancel", async (Guid jobId, CurrentPrincipal current, JobAdmin admin, CancellationToken ct) =>
            await admin.CancelAsync(jobId, current.Required.TenantId.Value, current.Required.IsPlatformOperator, ct) ? Results.NoContent() : ApiProblems.From(Error.Conflict("job.not_cancellable", "Only queued jobs can be cancelled.")))
            .RequirePermission(PlatformPermissions.JobManage);

        var schedules = platform.MapGroup("/schedules");
        schedules.MapGet("/", async (bool? allTenants, CurrentPrincipal current, Scheduler scheduler, CancellationToken ct) =>
            TypedResults.Ok(await scheduler.ListAsync(current.Required.TenantId.Value, allTenants == true && current.Required.IsPlatformOperator, ct)))
            .RequirePermission(PlatformPermissions.ScheduleRead);
        schedules.MapPut("/", async (SaveScheduleRequest request, CurrentPrincipal current, Scheduler scheduler, JobAdmin admin, CancellationToken ct) =>
            ApiProblems.Ok(await scheduler.SaveAsync(current.Required.TenantId.Value, request, admin.JobTypes, ct)))
            .RequirePermission(PlatformPermissions.ScheduleManage)
            .WithSummary("Create or replace a tenant schedule by code: cron (five fields), time zone, job type and payload");
        schedules.MapDelete("/{scheduleId:guid}", async (Guid scheduleId, CurrentPrincipal current, Scheduler scheduler, CancellationToken ct) =>
            await scheduler.DeleteAsync(scheduleId, current.Required.TenantId.Value, current.Required.IsPlatformOperator, ct) ? Results.NoContent() : ApiProblems.From(Error.NotFound("schedule", scheduleId)))
            .RequirePermission(PlatformPermissions.ScheduleManage);

        // ---------------------------------------------------------------- operators
        var ops = platform.MapGroup("/ops").RequireOperator();
        ops.MapGet("/outbox", async (string? state, Guid? tenantId, int? limit, OutboxAdmin outbox, CancellationToken ct) =>
            TypedResults.Ok(await outbox.ListAsync(state ?? "dead", tenantId, limit ?? 100, ct)))
            .WithSummary("Outbox messages by state: dead (default), discarded, pending, published, all");
        ops.MapPost("/outbox/{messageId:guid}/retry", async (Guid messageId, OutboxAdmin outbox, CancellationToken ct) =>
            await outbox.RetryAsync(messageId, ct) ? Results.NoContent() : ApiProblems.From(Error.Conflict("outbox.not_dead", "Only dead-lettered messages that were not discarded can be retried.")));
        ops.MapPost("/outbox/{messageId:guid}/discard", async (Guid messageId, DiscardOutboxMessageRequest request, CurrentPrincipal current, OutboxAdmin outbox, ILogger<OutboxAdmin> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            {
                return ApiProblems.From(Error.Validation("outbox.reason_required", "Say why the message is discarded, in up to 500 characters."));
            }

            var by = $"{current.Required.UserId.Value} {current.Required.Email}";
            if (!await outbox.DiscardAsync(messageId, request.Reason, by, ct))
            {
                return ApiProblems.From(Error.Conflict("outbox.not_dead", "Only dead-lettered messages that were not discarded can be discarded."));
            }

            logger.LogWarning("Outbox message {EventId} discarded by {Operator}: {Reason}", messageId, by, request.Reason);
            return Results.NoContent();
        }).WithSummary("Give up on a dead letter (reason required): its handlers never run and its aggregate's later events flow");
        ops.MapPut("/schedules", async (SaveScheduleRequest request, Scheduler scheduler, JobAdmin admin, CancellationToken ct) =>
            ApiProblems.Ok(await scheduler.SaveAsync(null, request, admin.JobTypes, ct)))
            .WithSummary("Create or replace a platform-wide schedule (no tenant)");

        return api;
    }
}

/// <summary>Why an operator gives up on a dead letter; kept on the message.</summary>
public sealed record DiscardOutboxMessageRequest(string Reason);

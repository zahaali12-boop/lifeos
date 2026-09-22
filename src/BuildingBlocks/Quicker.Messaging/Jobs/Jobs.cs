using System.Text.Json;
using Dapper;
using Quicker.Kernel.Ids;
using Quicker.Persistence;

namespace Quicker.Messaging.Jobs;

/// <summary>Exponential backoff shared by the outbox and jobs: 1 s, 2 s, 4 s … capped at one hour.</summary>
public static class Backoff
{
    public static int Seconds(int attempts) => (int)Math.Min(3600L, 1L << Math.Clamp(attempts - 1, 0, 20));

    public static TimeSpan For(int attempts) => TimeSpan.FromSeconds(Seconds(attempts));
}

/// <summary>A unit of background work: type, payload, when it may run, its priority and an optional idempotency key per tenant and type.</summary>
public sealed record JobRequest(string Type, object? Payload = null, DateTimeOffset? RunAfter = null, int Priority = 0, string? IdempotencyKey = null, int MaxAttempts = 5);

/// <summary>Enqueues jobs inside the current unit of work, so a job exists only if the change that needs it committed.</summary>
public interface IJobQueue
{
    /// <summary>Returns the job id; with an idempotency key, the id of the job that already carries that key.</summary>
    Task<Guid> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default);
}

public interface IJobContext
{
    Guid JobId { get; }

    TenantId? TenantId { get; }

    int Attempt { get; }

    /// <summary>Publishes progress for the UI (visible immediately; independent of the job's transaction).</summary>
    Task ReportProgressAsync(object progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes one job type inside a unit of work bound to the job's tenant (or none). Return value is stored as the
/// job's result. Throw <see cref="JobFailedException"/> for a permanent failure; any other exception retries with
/// backoff until the attempt budget is spent, after which the job is dead-lettered.
/// </summary>
public interface IJobHandler<in TPayload>
{
    static abstract string JobType { get; }

    Task<object?> ExecuteAsync(TPayload payload, IJobContext context, CancellationToken cancellationToken);
}

public sealed class JobFailedException(string message) : Exception(message);

public sealed record JobHandlerRegistration(string JobType, Type PayloadType, Type HandlerType);

public sealed class JobHandlerRegistry(IEnumerable<JobHandlerRegistration> registrations)
{
    private readonly Dictionary<string, JobHandlerRegistration> _byType = registrations.ToDictionary(static r => r.JobType, StringComparer.Ordinal);

    public JobHandlerRegistration? For(string jobType) => _byType.GetValueOrDefault(jobType);

    public IReadOnlyCollection<string> JobTypes => _byType.Keys;
}

public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class JobQueue(IUnitOfWorkAccessor unitOfWork) : IJobQueue
{
    public async Task<Guid> EnqueueAsync(JobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            throw new ArgumentException("A job type is required.", nameof(request));
        }

        var uow = unitOfWork.Current;
        var context = uow.Context;
        return await uow.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO ops.jobs (id, tenant_id, type, payload, priority, idempotency_key, run_after, max_attempts, correlation_id, actor)
            VALUES (@id, @tenant, @type, @payload::jsonb, @priority, @key, COALESCE(@runAfter, now()), @maxAttempts, @correlation, @actor)
            ON CONFLICT (tenant_id, type, idempotency_key) WHERE idempotency_key IS NOT NULL DO UPDATE SET type = EXCLUDED.type
            RETURNING id
            """, new
        {
            id = Uuid7.New(),
            tenant = context.IsAnonymous ? (Guid?)null : context.TenantId.Value,
            type = request.Type,
            payload = JsonSerializer.Serialize(request.Payload ?? new { }, JobJson.Options),
            priority = request.Priority,
            key = request.IdempotencyKey,
            runAfter = request.RunAfter,
            maxAttempts = Math.Max(1, request.MaxAttempts),
            correlation = context.CorrelationId ?? context.RequestId,
            actor = $"{context.ActorType}:{context.ActorDisplay}",
        }, uow.Transaction, cancellationToken: cancellationToken));
    }
}

/// <summary>A stored job as the runner and the UI see it.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public int Priority { get; set; }

    public string State { get; set; } = string.Empty;

    public string? IdempotencyKey { get; set; }

    public DateTimeOffset RunAfter { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset? HeartbeatAt { get; set; }

    public string? Progress { get; set; }

    public string? Result { get; set; }

    public string? Error { get; set; }

    public Guid? ScheduleId { get; set; }

    public string? CorrelationId { get; set; }

    public string Actor { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Dead = "dead";
}

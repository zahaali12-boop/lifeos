using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Messaging.Jobs;

/// <summary>
/// Claims one queued job at a time with <c>FOR UPDATE SKIP LOCKED</c> (highest priority, earliest run_after), runs it
/// in a unit of work bound to its tenant and writes the final state in that same transaction, so the job's effect
/// and its completion commit together. A heartbeat on a side connection lets <see cref="ReclaimStaleAsync"/> put a
/// crashed worker's jobs back in the queue.
/// </summary>
public sealed class JobRunner(
    NpgsqlDataSource dataSource,
    IUnitOfWorkFactory unitOfWorkFactory,
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantContext,
    JobHandlerRegistry registry,
    IOptions<WorkerOptions> options,
    ILogger<JobRunner> logger)
{
    public string InstanceName { get; } = options.Value.InstanceName ?? $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Claims and runs one job; false when the queue has nothing runnable.</summary>
    public async Task<bool> RunOneAsync(CancellationToken cancellationToken)
    {
        JobRecord? job;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            job = await connection.QuerySingleOrDefaultAsync<JobRecord>(new CommandDefinition("""
                UPDATE ops.jobs j
                SET state = 'running', locked_by = @instance, locked_at = now(), heartbeat_at = now(), started_at = COALESCE(j.started_at, now()), attempts = j.attempts + 1, error = NULL
                WHERE j.id = (
                  SELECT id FROM ops.jobs
                  WHERE state = 'queued' AND run_after <= now()
                  ORDER BY priority DESC, run_after, created_at
                  LIMIT 1
                  FOR UPDATE SKIP LOCKED)
                RETURNING j.id, j.tenant_id, j.type, j.payload::text AS payload, j.priority, j.state, j.idempotency_key, j.run_after, j.attempts, j.max_attempts,
                          j.locked_by, j.locked_at, j.heartbeat_at, j.progress::text AS progress, j.result::text AS result, j.error, j.schedule_id, j.correlation_id, j.actor,
                          j.created_at, j.started_at, j.finished_at
                """, new { instance = InstanceName }, cancellationToken: cancellationToken));
        }

        if (job is null)
        {
            return false;
        }

        await ExecuteAsync(job, cancellationToken);
        return true;
    }

    /// <summary>Jobs whose worker stopped heartbeating go back to the queue (attempt already counted by the claim).</summary>
    public async Task<int> ReclaimStaleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var reclaimed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE ops.jobs
            SET state = CASE WHEN attempts >= max_attempts THEN 'dead' ELSE 'queued' END,
                error = COALESCE(error, 'worker ' || COALESCE(locked_by, '?') || ' stopped heartbeating'),
                locked_by = NULL, locked_at = NULL, heartbeat_at = NULL,
                finished_at = CASE WHEN attempts >= max_attempts THEN now() ELSE NULL END
            WHERE state = 'running' AND heartbeat_at < now() - make_interval(secs => @seconds)
            """, new { seconds = options.Value.JobReclaimAfterSeconds }, cancellationToken: cancellationToken));
        if (reclaimed > 0)
        {
            logger.LogWarning("Reclaimed {Count} job(s) from workers that stopped heartbeating", reclaimed);
        }

        return reclaimed;
    }

    private async Task ExecuteAsync(JobRecord job, CancellationToken cancellationToken)
    {
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(job.Id, heartbeatStop.Token);
        try
        {
            await RunInUnitOfWorkAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown: hand the job back untouched (the claim's attempt is undone).
            await FinishAsync(job.Id, JobStates.Queued, error: null, undoAttempt: true, delaySeconds: null, CancellationToken.None);
            throw;
        }
        catch (JobFailedException ex)
        {
            logger.LogError(ex, "Job {JobId} ({Type}) failed permanently: {Error}", job.Id, job.Type, ex.Message);
            await FinishAsync(job.Id, JobStates.Failed, ex.Message, undoAttempt: false, delaySeconds: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var exhausted = job.Attempts >= job.MaxAttempts;
            var error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Job {JobId} ({Type}) attempt {Attempt}/{Max} failed", job.Id, job.Type, job.Attempts, job.MaxAttempts);
            await FinishAsync(job.Id, exhausted ? JobStates.Dead : JobStates.Queued, error, undoAttempt: false, exhausted ? null : Backoff.Seconds(job.Attempts), CancellationToken.None);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunInUnitOfWorkAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var registration = registry.For(job.Type) ?? throw new JobFailedException($"No handler is registered for job type '{job.Type}'.");
        var requestId = "job-" + job.Id.ToString("N")[^12..];
        var context = (job.TenantId is { } tenant ? TenantContext.System(new TenantId(tenant), requestId) : TenantContext.Anonymous(requestId)) with { CorrelationId = job.CorrelationId };

        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition("SELECT set_config('statement_timeout', @t, true)", new { t = $"{options.Value.JobStatementTimeoutSeconds}s" }, unitOfWork.Transaction, cancellationToken: cancellationToken));

        var payload = JsonSerializer.Deserialize(job.Payload, registration.PayloadType, JobJson.Options)
            ?? throw new JobFailedException($"Job {job.Id} payload could not be read as {registration.PayloadType.Name}.");
        var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        var method = registration.HandlerType.GetMethod("ExecuteAsync", [registration.PayloadType, typeof(IJobContext), typeof(CancellationToken)])
            ?? throw new JobFailedException($"{registration.HandlerType.Name} does not implement ExecuteAsync({registration.PayloadType.Name}).");
        var jobContext = new JobContext(dataSource, job.Id, job.TenantId is { } t ? new TenantId(t) : null, job.Attempts);
        object? result;
        try
        {
            result = await (Task<object?>)method.Invoke(handler, [payload, jobContext, cancellationToken])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ops.jobs SET state = 'succeeded', result = @result::jsonb, error = NULL, finished_at = now(), locked_by = NULL, locked_at = NULL, heartbeat_at = NULL WHERE id = @id",
            new { id = job.Id, result = JsonSerializer.Serialize(result, JobJson.Options) }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task FinishAsync(Guid jobId, string state, string? error, bool undoAttempt, int? delaySeconds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE ops.jobs
            SET state = @state, error = @error, attempts = CASE WHEN @undo THEN GREATEST(attempts - 1, 0) ELSE attempts END,
                run_after = CASE WHEN @delay IS NULL THEN run_after ELSE now() + make_interval(secs => @delay) END, locked_by = NULL, locked_at = NULL, heartbeat_at = NULL,
                finished_at = CASE WHEN @state IN ('failed', 'dead') THEN now() ELSE NULL END
            WHERE id = @id AND state = 'running'
            """, new { id = jobId, state, error = error is { Length: > 2000 } ? error[..2000] : error, undo = undoAttempt, delay = delaySeconds }, cancellationToken: cancellationToken));
    }

    private async Task HeartbeatAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.JobHeartbeatSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.jobs SET heartbeat_at = now() WHERE id = @id AND state = 'running'", new { id = jobId }, cancellationToken: cancellationToken));
        }
    }

    private sealed class JobContext(NpgsqlDataSource dataSource, Guid jobId, TenantId? tenantId, int attempt) : IJobContext
    {
        public Guid JobId => jobId;

        public TenantId? TenantId => tenantId;

        public int Attempt => attempt;

        public async Task ReportProgressAsync(object progress, CancellationToken cancellationToken = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.jobs SET progress = @progress::jsonb, heartbeat_at = now() WHERE id = @id", new { id = jobId, progress = JsonSerializer.Serialize(progress, JobJson.Options) }, cancellationToken: cancellationToken));
        }
    }
}

/// <summary>Job and schedule administration for tenants (their own) and operators (all).</summary>
public sealed class JobAdmin(NpgsqlDataSource dataSource, JobHandlerRegistry registry)
{
    private const string Columns = """
        id, tenant_id, type, payload::text AS payload, priority, state, idempotency_key, run_after, attempts, max_attempts, locked_by, locked_at, heartbeat_at,
        progress::text AS progress, result::text AS result, error, schedule_id, correlation_id, actor, created_at, started_at, finished_at
        """;

    public IReadOnlyCollection<string> JobTypes => registry.JobTypes;

    public async Task<IReadOnlyList<JobRecord>> ListAsync(Guid? tenantId, bool allTenants, string? state, string? type, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<JobRecord>(new CommandDefinition($"""
            SELECT {Columns} FROM ops.jobs
            WHERE (@all OR tenant_id IS NOT DISTINCT FROM @tenant) AND (@state IS NULL OR state = @state) AND (@type IS NULL OR type = @type)
            ORDER BY created_at DESC
            LIMIT @limit
            """, new { all = allTenants, tenant = tenantId, state, type, limit = Math.Clamp(limit, 1, 500) }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<JobRecord?> GetAsync(Guid id, Guid? tenantId, bool allTenants, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobRecord>(new CommandDefinition($"SELECT {Columns} FROM ops.jobs WHERE id = @id AND (@all OR tenant_id IS NOT DISTINCT FROM @tenant)", new { id, all = allTenants, tenant = tenantId }, cancellationToken: cancellationToken));
    }

    /// <summary>Queues a failed or dead job again with a fresh attempt budget.</summary>
    public async Task<bool> RetryAsync(Guid id, Guid? tenantId, bool allTenants, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ops.jobs SET state = 'queued', attempts = 0, run_after = now(), error = NULL, finished_at = NULL WHERE id = @id AND state IN ('failed', 'dead') AND (@all OR tenant_id IS NOT DISTINCT FROM @tenant)",
            new { id, all = allTenants, tenant = tenantId }, cancellationToken: cancellationToken)) == 1;
    }

    /// <summary>Cancels a job that has not started; running jobs finish (they hold the effect's transaction).</summary>
    public async Task<bool> CancelAsync(Guid id, Guid? tenantId, bool allTenants, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ops.jobs SET state = 'failed', error = 'cancelled', finished_at = now() WHERE id = @id AND state = 'queued' AND (@all OR tenant_id IS NOT DISTINCT FROM @tenant)",
            new { id, all = allTenants, tenant = tenantId }, cancellationToken: cancellationToken)) == 1;
    }
}

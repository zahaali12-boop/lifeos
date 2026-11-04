using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Messaging.Jobs;

public sealed class ScheduleRecord
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string JobType { get; set; } = string.Empty;

    public string Cron { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public Guid? LastJobId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record SaveScheduleRequest(string Code, string JobType, string Cron, string TimeZone = "UTC", object? Payload = null, bool Enabled = true);

/// <summary>
/// Enqueues jobs for schedules that are due. One instance ticks at a time (transaction-scoped advisory lock), each
/// due schedule is claimed with SKIP LOCKED, and the job's idempotency key is the schedule id plus the slot it
/// fires for, so a retried tick never enqueues a slot twice.
/// </summary>
public sealed class Scheduler(NpgsqlDataSource dataSource, IClock clock, ILogger<Scheduler> logger)
{
    private const string LeaderLockKey = "quicker:scheduler";

    /// <summary>Runs one tick; returns the number of jobs enqueued, or -1 when another instance holds the leader lock.</summary>
    public async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var leader = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_xact_lock(hashtext(@key))", new { key = LeaderLockKey }, transaction, cancellationToken: cancellationToken));
        if (!leader)
        {
            return -1;
        }

        var now = clock.UtcNow;
        var due = (await connection.QueryAsync<ScheduleRecord>(new CommandDefinition("""
            SELECT id, tenant_id, code, job_type, cron, time_zone, payload::text AS payload, enabled, next_run_at, last_run_at, last_job_id, created_at, updated_at
            FROM ops.schedules
            WHERE enabled AND (next_run_at IS NULL OR next_run_at <= @now)
            ORDER BY next_run_at NULLS FIRST
            FOR UPDATE SKIP LOCKED
            """, new { now }, transaction, cancellationToken: cancellationToken))).ToList();

        var enqueued = 0;
        foreach (var schedule in due)
        {
            var parsed = CronExpression.Parse(schedule.Cron);
            if (parsed.IsFailure || !TryTimeZone(schedule.TimeZone, out var timeZone))
            {
                logger.LogError("Schedule {Code} ({Id}) disabled: cron '{Cron}' or time zone '{TimeZone}' is invalid", schedule.Code, schedule.Id, schedule.Cron, schedule.TimeZone);
                await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.schedules SET enabled = false WHERE id = @id", new { id = schedule.Id }, transaction, cancellationToken: cancellationToken));
                continue;
            }

            if (schedule.NextRunAt is null)
            {
                // First sight of the schedule: arm it without firing for the past.
                await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.schedules SET next_run_at = @next WHERE id = @id", new { id = schedule.Id, next = parsed.Value.Next(now, timeZone) }, transaction, cancellationToken: cancellationToken));
                continue;
            }

            var slot = schedule.NextRunAt.Value;
            var jobId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO ops.jobs (id, tenant_id, type, payload, idempotency_key, schedule_id, actor)
                VALUES (@id, @tenant, @type, @payload::jsonb, @key, @schedule, 'system:scheduler')
                ON CONFLICT (tenant_id, type, idempotency_key) WHERE idempotency_key IS NOT NULL DO UPDATE SET type = EXCLUDED.type
                RETURNING id
                """, new
            {
                id = Uuid7.New(),
                tenant = schedule.TenantId,
                type = schedule.JobType,
                payload = schedule.Payload,
                key = $"schedule:{schedule.Id:N}:{slot.ToUniversalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}",
                schedule = schedule.Id,
            }, transaction, cancellationToken: cancellationToken));

            // Slots missed while the worker was down collapse into the one just fired; the next one is computed from now.
            await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.schedules SET last_run_at = @slot, last_job_id = @job, next_run_at = @next WHERE id = @id",
                new { id = schedule.Id, slot, job = jobId, next = parsed.Value.Next(now, timeZone) }, transaction, cancellationToken: cancellationToken));
            enqueued++;
        }

        await transaction.CommitAsync(cancellationToken);
        return enqueued;
    }

    // ------------------------------------------------------------------ administration

    public async Task<IReadOnlyList<ScheduleRecord>> ListAsync(Guid? tenantId, bool allTenants, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ScheduleRecord>(new CommandDefinition("""
            SELECT id, tenant_id, code, job_type, cron, time_zone, payload::text AS payload, enabled, next_run_at, last_run_at, last_job_id, created_at, updated_at
            FROM ops.schedules WHERE (@all OR tenant_id IS NOT DISTINCT FROM @tenant) ORDER BY code
            """, new { all = allTenants, tenant = tenantId }, cancellationToken: cancellationToken))).ToList();
    }

    public async Task<Result<ScheduleRecord>> SaveAsync(Guid? tenantId, SaveScheduleRequest request, IReadOnlyCollection<string> knownJobTypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(knownJobTypes);
        var code = request.Code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (code.Length is 0 or > 64 || !code.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.' or '-'))
        {
            return Error.Validation("schedule.code_invalid", "Schedule codes are lower-case letters, digits, '_', '.' or '-'.");
        }

        if (!knownJobTypes.Contains(request.JobType, StringComparer.Ordinal))
        {
            return Error.Validation("schedule.job_type_unknown", $"No job type '{request.JobType}' is registered.").WithWhy(("known", knownJobTypes.OrderBy(static t => t, StringComparer.Ordinal).ToList()));
        }

        var cron = CronExpression.Parse(request.Cron);
        if (cron.IsFailure)
        {
            return cron.Error!;
        }

        if (!TryTimeZone(request.TimeZone, out var timeZone))
        {
            return Error.Validation("schedule.time_zone_invalid", "Use an IANA time zone id such as Asia/Baghdad.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var next = request.Enabled ? cron.Value.Next(clock.UtcNow, timeZone) : null;
        return await connection.QuerySingleAsync<ScheduleRecord>(new CommandDefinition("""
            INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled, next_run_at)
            VALUES (@id, @tenant, @code, @type, @cron, @tz, @payload::jsonb, @enabled, @next)
            ON CONFLICT (tenant_id, code) DO UPDATE SET job_type = EXCLUDED.job_type, cron = EXCLUDED.cron, time_zone = EXCLUDED.time_zone, payload = EXCLUDED.payload, enabled = EXCLUDED.enabled, next_run_at = EXCLUDED.next_run_at
            RETURNING id, tenant_id, code, job_type, cron, time_zone, payload::text AS payload, enabled, next_run_at, last_run_at, last_job_id, created_at, updated_at
            """, new { id = Uuid7.New(), tenant = tenantId, code, type = request.JobType, cron = cron.Value.Text, tz = timeZone.Id, payload = JsonSerializer.Serialize(request.Payload ?? new { }, JobJson.Options), enabled = request.Enabled, next }, cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(Guid id, Guid? tenantId, bool allTenants, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM ops.schedules WHERE id = @id AND (@all OR tenant_id IS NOT DISTINCT FROM @tenant)", new { id, all = allTenants, tenant = tenantId }, cancellationToken: cancellationToken)) == 1;
    }

    private static bool TryTimeZone(string? id, out TimeZoneInfo timeZone)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var found))
        {
            timeZone = found;
            return true;
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }
}

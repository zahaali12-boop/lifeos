using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Quicker.Observability;

/// <summary>
/// Samples the platform's backlog every few seconds and exposes it as observable gauges: outbox lag and pending
/// messages, overdue and dead jobs. The same numbers back the readiness checks and the admin "system health" page.
/// </summary>
public sealed class PlatformGauges(NpgsqlDataSource dataSource, ILogger<PlatformGauges> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private long _outboxLagSeconds;
    private long _outboxPending;
    private long _jobsOverdue;
    private long _jobsDead;
    private long _jobsRunning;

    public PlatformGauges Register()
    {
        Telemetry.Meter.CreateObservableGauge("quicker.outbox.lag", () => Volatile.Read(ref _outboxLagSeconds), unit: "s", description: "Age of the oldest unpublished outbox message");
        Telemetry.Meter.CreateObservableGauge("quicker.outbox.pending", () => Volatile.Read(ref _outboxPending), unit: "{message}", description: "Unpublished, not dead outbox messages");
        Telemetry.Meter.CreateObservableGauge("quicker.jobs.overdue", () => Volatile.Read(ref _jobsOverdue), unit: "{job}", description: "Queued jobs past their run time");
        Telemetry.Meter.CreateObservableGauge("quicker.jobs.dead", () => Volatile.Read(ref _jobsDead), unit: "{job}", description: "Dead-lettered jobs");
        Telemetry.Meter.CreateObservableGauge("quicker.jobs.running", () => Volatile.Read(ref _jobsRunning), unit: "{job}", description: "Jobs being executed");
        return this;
    }

    /// <summary>The last sample, for the admin page and tests.</summary>
    public (long OutboxLagSeconds, long OutboxPending, long JobsOverdue, long JobsDead, long JobsRunning) Snapshot =>
        (Volatile.Read(ref _outboxLagSeconds), Volatile.Read(ref _outboxPending), Volatile.Read(ref _jobsOverdue), Volatile.Read(ref _jobsDead), Volatile.Read(ref _jobsRunning));

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<(long? Lag, long Pending, long Overdue, long Dead, long Running)>(new CommandDefinition("""
            SELECT
              (SELECT floor(extract(epoch FROM (now() - min(occurred_at))))::bigint FROM ops.outbox_messages WHERE published_at IS NULL AND dead_at IS NULL) AS lag,
              (SELECT count(*) FROM ops.outbox_messages WHERE published_at IS NULL AND dead_at IS NULL) AS pending,
              (SELECT count(*) FROM ops.jobs WHERE state = 'queued' AND run_after <= now()) AS overdue,
              (SELECT count(*) FROM ops.jobs WHERE state = 'dead') AS dead,
              (SELECT count(*) FROM ops.jobs WHERE state = 'running') AS running
            """, cancellationToken: cancellationToken));
        Volatile.Write(ref _outboxLagSeconds, row.Lag ?? 0);
        Volatile.Write(ref _outboxPending, row.Pending);
        Volatile.Write(ref _jobsOverdue, row.Overdue);
        Volatile.Write(ref _jobsDead, row.Dead);
        Volatile.Write(ref _jobsRunning, row.Running);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Register();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Platform gauges could not be sampled");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;

namespace Quicker.Messaging.Worker;

/// <summary>Wake-up signals shared by the listener (NOTIFY) and the loops; a poll interval is the fallback.</summary>
public sealed class WorkerSignals : IDisposable
{
    public WakeSignal Outbox { get; } = new();

    public WakeSignal Jobs { get; } = new();

    public void Dispose()
    {
        Outbox.Dispose();
        Jobs.Dispose();
    }
}

public sealed class WakeSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Dispose() => _semaphore.Dispose();

    public void Set()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _semaphore.WaitAsync(timeout, cancellationToken);
}

/// <summary>Holds one LISTEN connection and turns NOTIFY into wake signals; reconnects with backoff when the connection drops.</summary>
public sealed class NotifyListener(NpgsqlDataSource dataSource, WorkerSignals signals, ILogger<NotifyListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) =>
                {
                    if (e.Channel == "quicker_outbox")
                    {
                        signals.Outbox.Set();
                    }
                    else if (e.Channel == "quicker_jobs")
                    {
                        signals.Jobs.Set();
                    }
                };
                await using (var listen = new NpgsqlCommand("LISTEN quicker_outbox; LISTEN quicker_jobs;", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                failures = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
            {
                failures++;
                logger.LogWarning(ex, "LISTEN connection lost; reconnecting (attempt {Attempt})", failures);
                await Task.Delay(Backoff.For(failures), stoppingToken);
            }
        }
    }
}

/// <summary>Drains the outbox whenever signalled or every poll interval.</summary>
public sealed class OutboxLoop(OutboxDispatcher dispatcher, WorkerSignals signals, IOptions<WorkerOptions> options, ILogger<OutboxLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await dispatcher.RunOnceAsync(stoppingToken) > 0)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
            {
                logger.LogWarning(ex, "Outbox dispatch failed; retrying after the poll interval");
            }

            await signals.Outbox.WaitAsync(poll, stoppingToken);
        }
    }
}

/// <summary>Runs jobs on a fixed number of concurrent slots, reclaiming stale ones periodically.</summary>
public sealed class JobLoop(JobRunner runner, WorkerSignals signals, IOptions<WorkerOptions> options, ILogger<JobLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slots = Enumerable.Range(0, Math.Max(1, options.Value.JobConcurrency)).Select(_ => SlotAsync(stoppingToken)).ToList();
        slots.Add(ReclaimAsync(stoppingToken));
        await Task.WhenAll(slots);
    }

    private async Task SlotAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await runner.RunOneAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
            {
                logger.LogWarning(ex, "Job slot failed; retrying after the poll interval");
            }

            await signals.Jobs.WaitAsync(poll, stoppingToken);
        }
    }

    private async Task ReclaimAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.JobReclaimAfterSeconds / 4));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                if (await runner.ReclaimStaleAsync(stoppingToken) > 0)
                {
                    signals.Jobs.Set();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
            {
                logger.LogWarning(ex, "Reclaiming stale jobs failed; retrying later");
            }
        }
    }
}

/// <summary>Ticks the scheduler; only the leader (advisory lock) enqueues.</summary>
public sealed class SchedulerLoop(Scheduler scheduler, IOptions<WorkerOptions> options, ILogger<SchedulerLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.SchedulerTickSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException)
            {
                logger.LogWarning(ex, "Scheduler tick failed; retrying later");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

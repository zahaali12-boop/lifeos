using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Messaging.Worker;

namespace Quicker.Messaging.Tests;

/// <summary>The hosted loops as the worker runs them: NOTIFY wakes them within milliseconds (polling is set far beyond the timeout).</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkerLoopTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Notify_wakes_the_outbox_and_job_loops_without_polling()
    {
        TestGates.Reset();
        var services = Api.Services;
        var signals = new WorkerSignals();
        var options = Options.Create(new WorkerOptions { PollIntervalSeconds = 600, JobConcurrency = 2, JobReclaimAfterSeconds = 600 });
        var loggers = services.GetRequiredService<ILoggerFactory>();
        var listener = new NotifyListener(services.GetRequiredService<NpgsqlDataSource>(), signals, loggers.CreateLogger<NotifyListener>());
        var outboxLoop = new OutboxLoop(services.GetRequiredService<OutboxDispatcher>(), signals, options, loggers.CreateLogger<OutboxLoop>());
        var jobLoop = new JobLoop(services.GetRequiredService<JobRunner>(), signals, options, loggers.CreateLogger<JobLoop>());

        await listener.StartAsync(CancellationToken.None);
        await outboxLoop.StartAsync(CancellationToken.None);
        await jobLoop.StartAsync(CancellationToken.None);
        try
        {
            // Give the LISTEN connection a moment to subscribe, then produce work through ordinary units of work.
            await Task.Delay(500);
            var ws = await Api.SignupAsync();
            Guid jobId = Guid.Empty;
            await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
            {
                await sp.GetRequiredService<IOutbox>().PublishAsync(new ThingHappened(Guid.NewGuid(), 1, "ok"));
                jobId = await sp.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("test.effect", new EffectPayload("woken")));
            });

            // Handlers commit their own units of work before the dispatcher commits the claim that marks the batch
            // published, so wait for both signs of life rather than reading the outbox the instant the effects appear.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            Dictionary<string, int> effects;
            long unpublished;
            do
            {
                await Task.Delay(100);
                effects = await Effects.ReadAsync(Api, ws.TenantId, "test.");
                unpublished = await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE tenant_id = @t AND published_at IS NULL", new { t = ws.TenantId });
            }
            while ((effects.Count < 2 || unpublished > 0) && DateTime.UtcNow < deadline);

            effects.Count.ShouldBe(2, "both loops were woken by NOTIFY (poll interval is 10 minutes)");
            (await TenantWork.QueryOwnerAsync<string>(Api, "SELECT state FROM ops.jobs WHERE id = @id", new { id = jobId })).ShouldBe("succeeded");
            unpublished.ShouldBe(0);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(jobLoop.StopAsync(stop.Token), outboxLoop.StopAsync(stop.Token), listener.StopAsync(stop.Token));
            jobLoop.Dispose();
            outboxLoop.Dispose();
            listener.Dispose();
            signals.Dispose();
        }
    }
}

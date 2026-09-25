using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;

namespace Quicker.Messaging.Tests;

/// <summary>ADR-0010 jobs: SKIP LOCKED claims, exactly-once effect across a crash, heartbeat reclaim, retries, dead letters, idempotency keys, the tenant API.</summary>
[Collection(ApiCollection.Name)]
public sealed class JobTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private JobRunner Runner => Api.Services.GetRequiredService<JobRunner>();

    private static async Task DrainAsync(JobRunner runner, int slots, CancellationToken cancellationToken)
    {
        var workers = Enumerable.Range(0, slots).Select(_ => Task.Run(async () =>
        {
            while (await runner.RunOneAsync(cancellationToken))
            {
            }
        }, cancellationToken)).ToList();
        await Task.WhenAll(workers);
    }

    [Fact]
    public async Task Jobs_enqueue_with_the_transaction_run_on_parallel_slots_and_survive_a_worker_death_with_one_effect_each()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var queue = sp.GetRequiredService<IJobQueue>();
            for (var i = 1; i <= 20; i++)
            {
                await queue.EnqueueAsync(new JobRequest("test.effect", new EffectPayload($"j{i:00}", Act: i == 7)));
            }
        });
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) => await sp.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("test.effect", new EffectPayload("rolled-back"))), commit: false);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.jobs WHERE tenant_id = @t", new { t = ws.TenantId })).ShouldBe(20);

        // Three slots; slot holding job 7 dies (cancellation) while inside its transaction.
        TestGates.Arm(blocks: 1);
        using var crash = new CancellationTokenSource();
        var firstWorker = DrainAsync(Runner, 3, crash.Token);
        await TestGates.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await crash.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => firstWorker);

        var afterCrash = await TenantWork.QueryOwnerRowAsync<(long Queued, long Running, long Succeeded)>(Api,
            "SELECT count(*) FILTER (WHERE state = 'queued'), count(*) FILTER (WHERE state = 'running'), count(*) FILTER (WHERE state = 'succeeded') FROM ops.jobs WHERE tenant_id = @t", new { t = ws.TenantId });
        afterCrash.Running.ShouldBe(0, "a cancelled job is handed back to the queue");
        afterCrash.Queued.ShouldBeGreaterThan(0);
        (await TenantWork.QueryOwnerAsync<int>(Api, "SELECT attempts FROM ops.jobs WHERE tenant_id = @t AND payload->>'key' = 'j07'", new { t = ws.TenantId })).ShouldBe(0, "the interrupted attempt does not count");

        await DrainAsync(Runner, 3, CancellationToken.None);
        var effects = await Effects.ReadAsync(Api, ws.TenantId, "test.job.");
        effects.Count.ShouldBe(20);
        effects.Values.ShouldAllBe(static v => v == 1);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.jobs WHERE tenant_id = @t AND state = 'succeeded' AND result->>'key' IS NOT NULL AND finished_at IS NOT NULL AND locked_by IS NULL", new { t = ws.TenantId })).ShouldBe(20);
    }

    [Fact]
    public async Task Failures_retry_with_backoff_then_dead_letter_permanent_failures_fail_at_once_and_retries_are_explicit()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        Guid flaky = Guid.Empty;
        Guid broken = Guid.Empty;
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var queue = sp.GetRequiredService<IJobQueue>();
            flaky = await queue.EnqueueAsync(new JobRequest("test.effect", new EffectPayload("flaky", Act: true), MaxAttempts: 2));
            broken = await queue.EnqueueAsync(new JobRequest("test.effect", new EffectPayload("broken", Permanent: true)));
        });

        TestGates.Arm(failures: 2);
        await DrainAsync(Runner, 1, CancellationToken.None);
        var admin = Api.Services.GetRequiredService<JobAdmin>();
        var flakyJob = (await admin.GetAsync(flaky, ws.TenantId, false, CancellationToken.None))!;
        flakyJob.State.ShouldBe("queued");
        flakyJob.Attempts.ShouldBe(1);
        flakyJob.Error!.ShouldContain("simulated failure");
        flakyJob.RunAfter.ShouldBeGreaterThan(flakyJob.CreatedAt);
        var brokenJob = (await admin.GetAsync(broken, ws.TenantId, false, CancellationToken.None))!;
        brokenJob.State.ShouldBe("failed");
        brokenJob.Error.ShouldBe("permanently broken");
        (await Effects.ReadAsync(Api, ws.TenantId, "test.job.")).ShouldBeEmpty("a failed attempt's effect rolls back with it");

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await DrainAsync(Runner, 1, CancellationToken.None);
        flakyJob = (await admin.GetAsync(flaky, ws.TenantId, false, CancellationToken.None))!;
        flakyJob.State.ShouldBe("dead");
        flakyJob.Attempts.ShouldBe(2);

        (await admin.RetryAsync(flaky, ws.TenantId, false, CancellationToken.None)).ShouldBeTrue();
        await DrainAsync(Runner, 1, CancellationToken.None);
        flakyJob = (await admin.GetAsync(flaky, ws.TenantId, false, CancellationToken.None))!;
        flakyJob.State.ShouldBe("succeeded");
        flakyJob.Result!.Replace(" ", "", StringComparison.Ordinal).ShouldContain("\"attempt\":1");
        (await Effects.ReadAsync(Api, ws.TenantId, "test.job."))["test.job.flaky"].ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_keys_dedupe_heartbeats_keep_a_running_job_and_stale_ones_are_reclaimed()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        Guid first = Guid.Empty;
        Guid second = Guid.Empty;
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var queue = sp.GetRequiredService<IJobQueue>();
            first = await queue.EnqueueAsync(new JobRequest("test.effect", new EffectPayload("once"), IdempotencyKey: "import:file-1"));
            second = await queue.EnqueueAsync(new JobRequest("test.effect", new EffectPayload("twice"), IdempotencyKey: "import:file-1"));
        });
        second.ShouldBe(first);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.jobs WHERE tenant_id = @t", new { t = ws.TenantId })).ShouldBe(1);

        // A job that reports progress and keeps running: heartbeat advances, progress is visible, reclaim leaves it alone.
        Guid longRunning = Guid.Empty;
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
            longRunning = await sp.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("test.effect", new EffectPayload("long", Act: true, Progress: true), Priority: 10)));
        TestGates.Arm(blocks: 1);
        var running = Task.Run(async () => await Runner.RunOneAsync(CancellationToken.None));
        await TestGates.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = await TenantWork.QueryOwnerRowAsync<(string State, DateTimeOffset? Heartbeat, string Progress)>(Api, "SELECT state, heartbeat_at, progress::text FROM ops.jobs WHERE id = @id", new { id = longRunning });
        before.State.ShouldBe("running");
        before.Progress.ShouldContain("50");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        (await Runner.ReclaimStaleAsync(CancellationToken.None)).ShouldBe(0);
        var later = await TenantWork.QueryOwnerRowAsync<(string State, DateTimeOffset? Heartbeat)>(Api, "SELECT state, heartbeat_at FROM ops.jobs WHERE id = @id", new { id = longRunning });
        later.State.ShouldBe("running");
        later.Heartbeat!.Value.ShouldBeGreaterThan(before.Heartbeat!.Value);
        TestGates.Release.Release();
        (await running).ShouldBeTrue();
        (await TenantWork.QueryOwnerAsync<string>(Api, "SELECT state FROM ops.jobs WHERE id = @id", new { id = longRunning })).ShouldBe("succeeded");

        // A job whose worker vanished (stale heartbeat) goes back to the queue and runs again, once.
        await DrainAsync(Runner, 1, CancellationToken.None);
        await TenantWork.ExecuteOwnerAsync(Api, "UPDATE ops.jobs SET state = 'running', locked_by = 'dead-worker', heartbeat_at = now() - interval '1 minute', attempts = 1 WHERE id = @id", new { id = first });
        await TenantWork.ExecuteOwnerAsync(Api, "DELETE FROM app.org_settings WHERE tenant_id = @t AND key = 'test.job.once'", new { t = ws.TenantId });
        (await Runner.ReclaimStaleAsync(CancellationToken.None)).ShouldBe(1);
        var reclaimed = await TenantWork.QueryOwnerRowAsync<(string State, string Error)>(Api, "SELECT state, error FROM ops.jobs WHERE id = @id", new { id = first });
        reclaimed.State.ShouldBe("queued");
        reclaimed.Error.ShouldContain("dead-worker");
        await DrainAsync(Runner, 1, CancellationToken.None);
        (await TenantWork.QueryOwnerAsync<string>(Api, "SELECT state FROM ops.jobs WHERE id = @id", new { id = first })).ShouldBe("succeeded");
        (await Effects.ReadAsync(Api, ws.TenantId, "test.job.once"))["test.job.once"].ShouldBe(1);
    }

    [Fact]
    public async Task Tenants_see_and_manage_only_their_own_jobs_and_schedules_through_the_api()
    {
        var ws = await Api.SignupAsync();
        var other = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        using var outsider = Api.ClientFor(other.AccessToken);
        Guid queued = Guid.Empty;
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
            queued = await sp.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest("test.effect", new EffectPayload("api"), RunAfter: DateTimeOffset.UtcNow.AddHours(1))));

        var mine = await owner.GetAsync("/api/v1/platform/jobs");
        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mine.ReadJsonAsync()).EnumerateArray().Select(static j => j.GetProperty("id").GetGuid()).ShouldContain(queued);
        (await (await outsider.GetAsync("/api/v1/platform/jobs")).ReadJsonAsync()).EnumerateArray().Select(static j => j.GetProperty("id").GetGuid()).ShouldNotContain(queued);
        (await outsider.GetAsync($"/api/v1/platform/jobs/{queued}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.PostAsync($"/api/v1/platform/jobs/{queued}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.PostAsync($"/api/v1/platform/jobs/{queued}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await owner.GetAsync($"/api/v1/platform/jobs/{queued}")).ReadJsonAsync()).GetProperty("state").GetString().ShouldBe("failed");
        (await owner.PostAsync($"/api/v1/platform/jobs/{queued}/retry", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await owner.GetAsync("/api/v1/platform/jobs/types")).ReadJsonAsync()).EnumerateArray().Select(static t => t.GetString()).ShouldContain("audit.anchor_all");

        var schedule = await owner.PutAsJsonAsync("/api/v1/platform/schedules", new { code = "nightly-effect", jobType = "test.effect", cron = "0 1 * * *", timeZone = "Asia/Baghdad", payload = new { key = "nightly" } }, ApiFixture.Json);
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, await schedule.Content.ReadAsStringAsync());
        var saved = await schedule.ReadJsonAsync();
        saved.GetProperty("nextRunAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
        (await (await outsider.GetAsync("/api/v1/platform/schedules")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await (await owner.GetAsync("/api/v1/platform/schedules")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await (await owner.PutAsJsonAsync("/api/v1/platform/schedules", new { code = "bad", jobType = "test.effect", cron = "every day" }, ApiFixture.Json)).ErrorCodeAsync()).ShouldBe("schedule.cron_invalid");
        (await (await owner.PutAsJsonAsync("/api/v1/platform/schedules", new { code = "bad", jobType = "nope", cron = "0 1 * * *" }, ApiFixture.Json)).ErrorCodeAsync()).ShouldBe("schedule.job_type_unknown");
        (await outsider.DeleteAsync($"/api/v1/platform/schedules/{saved.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/v1/platform/schedules/{saved.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetAsync("/api/v1/platform/ops/outbox")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // operators only
    }
}

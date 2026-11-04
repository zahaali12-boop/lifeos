using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;

namespace Quicker.Messaging.Tests;

/// <summary>Cron evaluation in time zones, and the scheduler's leader lock, arming, firing and slot idempotency.</summary>
[Collection(ApiCollection.Name)]
public sealed class SchedulerAndCronTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly TimeZoneInfo Baghdad = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad");

    [Fact]
    public async Task Every_shipped_schedule_names_a_job_the_host_runs_with_a_cron_and_time_zone_that_parse()
    {
        // The platform schedules the seeds ship (ids 0199a000-…): a typo in a job type would only fail at night, in production.
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        var shipped = (await db.QueryAsync<(string Code, string JobType, string Cron, string TimeZone)>(
            "SELECT code, job_type, cron, time_zone FROM ops.schedules WHERE tenant_id IS NULL AND id::text LIKE '0199a000-0000-7000-8000-%' ORDER BY code")).ToList();
        shipped.Count.ShouldBeGreaterThanOrEqualTo(11);
        var known = Api.Services.GetRequiredService<JobHandlerRegistry>().JobTypes;
        shipped.Where(s => !known.Contains(s.JobType)).Select(static s => $"{s.Code} → {s.JobType}").ShouldBeEmpty();
        shipped.Where(static s => CronExpression.Parse(s.Cron).IsFailure).Select(static s => $"{s.Code}: {s.Cron}").ShouldBeEmpty();
        shipped.Where(static s => !TimeZoneInfo.TryFindSystemTimeZoneById(s.TimeZone, out _)).Select(static s => $"{s.Code}: {s.TimeZone}").ShouldBeEmpty();
        shipped.Select(static s => s.Code).ShouldContain("collaboration.attachment_sweep");
    }

    [Theory]
    [InlineData("0 2 * * *", "2026-09-22T08:00:00Z", "UTC", "2026-09-23T02:00:00Z")]
    [InlineData("*/15 * * * *", "2026-09-22T08:07:00Z", "UTC", "2026-09-22T08:15:00Z")]
    [InlineData("0 9 * * 0-4", "2026-09-24T07:00:00Z", "Asia/Baghdad", "2026-09-27T06:00:00Z")]
    [InlineData("30 1 1 * *", "2026-09-22T08:00:00Z", "UTC", "2026-10-01T01:30:00Z")]
    [InlineData("0 0 29 2 *", "2026-09-22T08:00:00Z", "UTC", "2028-02-29T00:00:00Z")]
    [InlineData("0 12 15 * 1", "2026-09-22T08:00:00Z", "UTC", "2026-09-28T12:00:00Z")]
    [InlineData("5 4 * * 7", "2026-09-22T08:00:00Z", "UTC", "2026-09-27T04:05:00Z")]
    public void Next_occurrence_follows_vixie_cron_semantics(string cron, string after, string timeZone, string expected)
    {
        var expression = CronExpression.Parse(cron).Value;
        var next = expression.Next(DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        next.ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    public void Invalid_expressions_are_rejected(string cron) => CronExpression.Parse(cron).Error!.Code.ShouldBe("schedule.cron_invalid");

    [Fact]
    public void Day_of_week_ranges_in_baghdad_skip_the_weekend()
    {
        var expression = CronExpression.Parse("0 9 * * 0-4").Value;
        var thursday = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.FromHours(3)); // Thursday 09:00 Baghdad
        var next = expression.Next(thursday, Baghdad)!.Value;
        TimeZoneInfo.ConvertTime(next, Baghdad).DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        next.ShouldBe(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public async Task The_scheduler_arms_new_schedules_fires_due_slots_once_and_yields_to_the_leader()
    {
        var scheduler = Api.Services.GetRequiredService<Scheduler>();
        var ws = await Api.SignupAsync();
        var saved = await scheduler.SaveAsync(ws.TenantId, new SaveScheduleRequest("hourly-effect", "test.effect", "0 * * * *", "UTC", new { key = "scheduled" }), ["test.effect"], CancellationToken.None);
        saved.IsSuccess.ShouldBeTrue(saved.Error?.Message);
        saved.Value.NextRunAt.ShouldNotBeNull();
        var armed = saved.Value.NextRunAt!.Value;
        armed.Minute.ShouldBe(0);
        armed.ShouldBeGreaterThan(Api.Clock.UtcNow);

        // Not due: nothing fires. Due (clock moved past the slot): exactly one job, and a repeated tick for the same slot adds none.
        (await scheduler.TickAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(0);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.jobs WHERE schedule_id = @s", new { s = saved.Value.Id })).ShouldBe(0);

        var original = Api.Clock.UtcNow;
        try
        {
            Api.Clock.Set(armed.AddMinutes(1));
            (await scheduler.TickAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
            var fired = await TenantWork.QueryOwnerRowAsync<(long Count, string Key)>(Api, "SELECT count(*), min(idempotency_key) FROM ops.jobs WHERE schedule_id = @s", new { s = saved.Value.Id });
            fired.Count.ShouldBe(1);
            fired.Key.ShouldStartWith($"schedule:{saved.Value.Id:N}:");
            var rearmed = await TenantWork.QueryOwnerRowAsync<(DateTimeOffset? Next, DateTimeOffset? Last, Guid? LastJob)>(Api, "SELECT next_run_at, last_run_at, last_job_id FROM ops.schedules WHERE id = @s", new { s = saved.Value.Id });
            rearmed.Next.ShouldBe(armed.AddHours(1));
            rearmed.Last.ShouldBe(armed);
            rearmed.LastJob.ShouldNotBeNull();

            // Rewinding the schedule to the same slot (as a retried tick would) cannot enqueue the slot twice.
            await TenantWork.ExecuteOwnerAsync(Api, "UPDATE ops.schedules SET next_run_at = @slot WHERE id = @s", new { slot = armed, s = saved.Value.Id });
            await scheduler.TickAsync(CancellationToken.None);
            (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.jobs WHERE schedule_id = @s", new { s = saved.Value.Id })).ShouldBe(1);

            // The job carries the schedule's payload and tenant and runs like any other.
            var runner = Api.Services.GetRequiredService<JobRunner>();
            while (await runner.RunOneAsync(CancellationToken.None))
            {
            }

            (await TenantWork.QueryOwnerAsync<string>(Api, "SELECT state FROM ops.jobs WHERE schedule_id = @s", new { s = saved.Value.Id })).ShouldBe("succeeded");
            (await Effects.ReadAsync(Api, ws.TenantId, "test.job.scheduled"))["test.job.scheduled"].ShouldBe(1);
        }
        finally
        {
            Api.Clock.Set(original);
        }

        // Another instance holding the leader lock makes this tick a no-op.
        await using var rival = new NpgsqlConnection(Api.Db.AppConnectionString);
        await rival.OpenAsync();
        await using (var tx = await rival.BeginTransactionAsync())
        {
            (await rival.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_xact_lock(hashtext('quicker:scheduler'))", transaction: tx)).ShouldBeTrue();
            (await scheduler.TickAsync(CancellationToken.None)).ShouldBe(-1);
        }

        (await scheduler.TickAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(0);
        (await scheduler.DeleteAsync(saved.Value.Id, ws.TenantId, false, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Platform_schedules_are_seeded_and_the_audit_jobs_run_end_to_end()
    {
        var scheduler = Api.Services.GetRequiredService<Scheduler>();
        var platform = await scheduler.ListAsync(null, true, CancellationToken.None);
        platform.Select(static s => s.Code).ShouldContain("audit.anchor_daily");
        platform.Select(static s => s.Code).ShouldContain("audit.verify_daily");
        platform.Where(static s => s.TenantId is null).Select(static s => s.JobType).ShouldContain("ops.outbox_archive");

        await Api.SignupAsync();
        Guid anchor = Guid.Empty;
        Guid verify = Guid.Empty;
        await TenantWork.InTenantAsync(Api, Guid.Empty, async (sp, _) =>
        {
            var queue = sp.GetRequiredService<IJobQueue>();
            anchor = await queue.EnqueueAsync(new JobRequest("audit.anchor_all"));
            verify = await queue.EnqueueAsync(new JobRequest("audit.verify_all", RunAfter: DateTimeOffset.UtcNow.AddSeconds(-1)));
        });
        var runner = Api.Services.GetRequiredService<JobRunner>();
        while (await runner.RunOneAsync(CancellationToken.None))
        {
        }

        var admin = Api.Services.GetRequiredService<JobAdmin>();
        var anchored = (await admin.GetAsync(anchor, null, true, CancellationToken.None))!;
        anchored.State.ShouldBe("succeeded", anchored.Error);
        anchored.Result!.ShouldContain("anchored");
        var verified = (await admin.GetAsync(verify, null, true, CancellationToken.None))!;
        verified.State.ShouldBe("succeeded", verified.Error);
        verified.Result!.ShouldContain("verified");
    }
}

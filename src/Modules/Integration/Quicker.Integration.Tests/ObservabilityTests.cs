using System.Diagnostics;
using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Observability;

namespace Quicker.Integration.Tests;

/// <summary>Slice 1.11: a request trace carries the tenant, user and database spans; readiness fails on outbox lag; the health payload shape.</summary>
[Collection(ApiCollection.Name)]
public sealed class ObservabilityTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task A_request_trace_shows_the_tenant_the_user_and_the_database_spans()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        (await owner.GetAsync("/api/v1/organization/companies")).StatusCode.ShouldBe(HttpStatusCode.OK);

        Activity request;
        lock (activities)
        {
            request = activities.Single(a => a.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn" && a.GetTagItem(Telemetry.TenantTag) is string tenant && tenant == ws.TenantId.ToString());
        }

        request.GetTagItem(Telemetry.UserTag).ShouldBe(ws.UserId.ToString());
        request.GetTagItem(Telemetry.MembershipTag).ShouldBe(ws.MembershipId.ToString());
        request.GetTagItem(Telemetry.ActorTag).ShouldBe("user");
        List<Activity> children;
        lock (activities)
        {
            children = activities.Where(a => a.TraceId == request.TraceId && a != request).ToList();
        }

        children.ShouldContain(a => a.Source.Name == "Npgsql", "the database work of the request is a child span of the request");
    }

    [Fact]
    public async Task Readiness_fails_while_the_outbox_lags_and_the_health_payload_lists_every_check()
    {
        var live = await (await Api.Client.GetAsync("/health/live")).ReadJsonAsync();
        live.GetProperty("status").GetString().ShouldBe("live");

        var ready = await Api.Client.GetAsync("/health/ready");
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await ready.ReadJsonAsync();
        payload.GetProperty("status").GetString().ShouldBe("ready");
        payload.GetProperty("migrations").GetInt64().ShouldBeGreaterThan(0);
        payload.GetProperty("checks").EnumerateArray().Select(static c => c.GetProperty("name").GetString()).ShouldBe(["database", "outbox", "jobs"]);

        // An unpublished message older than the threshold: the dispatcher is not keeping up, readiness goes red.
        var ws = await Api.SignupAsync();
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        var stale = Guid.CreateVersion7();
        await db.ExecuteAsync("INSERT INTO ops.outbox_messages (id, tenant_id, occurred_at, event_type, aggregate_type, aggregate_id, payload, next_attempt_at) VALUES (@id, @t, now() - interval '2 hours', 'test.stale', 'probe', @a, '{}', now() + interval '1 day')", new { id = stale, t = ws.TenantId, a = Guid.NewGuid() });
        try
        {
            var lagging = await Api.Client.GetAsync("/health/ready");
            lagging.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var report = await lagging.ReadJsonAsync();
            report.GetProperty("status").GetString().ShouldBe("unready");
            var outbox = report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "outbox");
            outbox.GetProperty("status").GetString().ShouldBe("unready");
            outbox.GetProperty("data").GetProperty("lagSeconds").GetInt64().ShouldBeGreaterThanOrEqualTo(7000);
            report.GetProperty("error").GetString()!.ShouldContain("outbox lag");
        }
        finally
        {
            await db.ExecuteAsync("DELETE FROM ops.outbox_messages WHERE id = @id", new { id = stale });
        }

        (await Api.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var deps = await (await Api.Client.GetAsync("/health/deps")).ReadJsonAsync();
        deps.GetProperty("checks").GetArrayLength().ShouldBe(3);
        deps.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "jobs").GetProperty("data").TryGetProperty("dead", out _).ShouldBeTrue();
    }
}

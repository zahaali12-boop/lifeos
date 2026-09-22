using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Collaboration.Tests;

/// <summary>Notifications: announcement fan-out, in-app inbox, preferences per kind, email through SMTP with retry, isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Announcements_reach_every_member_in_app_and_by_email_according_to_preferences()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (clerkToken, clerkMembership, _) = await host.InviteAsync(ws, "clerk", "collaboration.attachment.read");
        using var clerk = Api.ClientFor(clerkToken);

        // The clerk keeps announcements in-app only; everything else stays on both channels.
        var prefs = await (await clerk.PutAsJsonAsync("/api/v1/collaboration/notifications/preferences", new[] { new { kind = "system.announcement", inApp = true, email = false } }, Json)).ReadJsonAsync();
        prefs.EnumerateArray().Select(static p => p.GetProperty("kind").GetString()).ShouldBe(["*", "system.announcement"]);
        (await (await clerk.PutAsJsonAsync("/api/v1/collaboration/notifications/preferences", new[] { new { kind = "Sales Invoice", inApp = true, email = true } }, Json)).ErrorCodeAsync()).ShouldBe("notification.preference_invalid");

        host.Smtp.Received.Clear();
        var announced = await owner.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "Month-end close", ar = "إقفال الشهر" }, body = new { en = "Post everything by Thursday.", ar = "أنهوا الترحيل قبل الخميس." }, link = "https://app.example.test/close" }, Json);
        announced.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var outcome = await announced.ReadJsonAsync();
        outcome.GetProperty("inApp").GetInt32().ShouldBe(2);
        outcome.GetProperty("emails").GetInt32().ShouldBe(1);

        // The clerk's inbox.
        var inbox = (await (await clerk.GetAsync("/api/v1/collaboration/notifications")).ReadJsonAsync()).EnumerateArray().ToList();
        var note = inbox.ShouldHaveSingleItem();
        note.GetProperty("kind").GetString().ShouldBe("system.announcement");
        note.GetProperty("title").Map()["ar"].ShouldBe("إقفال الشهر");
        note.GetProperty("body").Map()["en"].ShouldBe("Post everything by Thursday.");
        note.GetProperty("link").GetString().ShouldBe("https://app.example.test/close");
        note.GetProperty("actorMembershipId").GetGuid().ShouldBe(ws.MembershipId);
        note.GetProperty("readAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await (await clerk.GetAsync("/api/v1/collaboration/notifications/unread-count")).ReadJsonAsync()).GetProperty("count").GetInt32().ShouldBe(1);
        var read = await (await clerk.PostAsync($"/api/v1/collaboration/notifications/{note.GetProperty("id").GetGuid()}/read", null)).ReadJsonAsync();
        read.GetProperty("readAt").GetDateTimeOffset().ShouldBe(Api.Clock.UtcNow);
        (await (await clerk.GetAsync("/api/v1/collaboration/notifications/unread-count")).ReadJsonAsync()).GetProperty("count").GetInt32().ShouldBe(0);
        (await (await clerk.GetAsync("/api/v1/collaboration/notifications?unreadOnly=true")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);

        // The owner gets the email (their preferences are the defaults) once the worker runs the send job.
        await host.RunWorkerAsync();
        var mail = await host.Smtp.WaitForAsync(m => m.To.Contains(ws.OwnerEmail, StringComparer.OrdinalIgnoreCase), TimeSpan.FromSeconds(10));
        mail.From.ShouldBe("quicker@example.test");
        mail.Header("Subject").ShouldBe("Month-end close");
        mail.Body.ShouldContain("Post everything by Thursday.");
        mail.Body.ShouldContain("https://app.example.test/close");
        host.Smtp.Received.Count(m => m.Header("Subject") == "Month-end close").ShouldBe(1, "the clerk opted out of announcement emails");
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        var log = (await db.QueryAsync<(string To, string Status, int Attempts, Guid? Membership)>("SELECT to_address, status, attempts, membership_id FROM app.col_email_log WHERE tenant_id = @t", new { t = ws.TenantId })).ToList();
        log.ShouldHaveSingleItem().ShouldBe((ws.OwnerEmail, "sent", 1, ws.MembershipId));
        clerkMembership.ShouldNotBe(ws.MembershipId);

        // Only members with the permission announce; other tenants see nothing; a foreign id is not found.
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "nope" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "  " } }, Json)).ErrorCodeAsync()).ShouldBe("announcement.title_required");
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await (await outsider.GetAsync("/api/v1/collaboration/notifications")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await outsider.PostAsync($"/api/v1/collaboration/notifications/{note.GetProperty("id").GetGuid()}/read", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Read-all and paging by cursor.
        await owner.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "Second" } }, Json);
        await owner.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "Third" } }, Json);
        var latest = (await (await clerk.GetAsync("/api/v1/collaboration/notifications?limit=1")).ReadJsonAsync()).EnumerateArray().Single();
        latest.GetProperty("title").Map()["en"].ShouldBe("Third");
        var older = (await (await clerk.GetAsync($"/api/v1/collaboration/notifications?before={latest.GetProperty("id").GetGuid()}")).ReadJsonAsync()).EnumerateArray().Select(static n => n.GetProperty("title").Map()["en"]).ToList();
        older.ShouldBe(["Second", "Month-end close"]);
        (await (await clerk.PostAsync("/api/v1/collaboration/notifications/read-all", null)).ReadJsonAsync()).GetProperty("marked").GetInt32().ShouldBe(2);
        (await (await clerk.GetAsync("/api/v1/collaboration/notifications/unread-count")).ReadJsonAsync()).GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_refused_email_is_retried_with_backoff_and_the_log_shows_each_attempt()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        await host.RunWorkerAsync(); // emails queued by earlier tests must not consume the refusal
        host.Smtp.RejectNext = true;
        await owner.PostAsJsonAsync("/api/v1/collaboration/notifications/announce", new { title = new { en = "Flaky relay" }, body = new { en = "first try fails" } }, Json);
        await host.RunWorkerAsync();

        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        const string sql = "SELECT e.status, e.attempts, e.last_error, j.state FROM app.col_email_log e JOIN ops.jobs j ON j.id = e.job_id WHERE e.tenant_id = @t";
        var failed = await db.QuerySingleAsync<(string Status, int Attempts, string? Error, string JobState)>(sql, new { t = ws.TenantId });
        failed.Status.ShouldBe("queued");
        failed.Attempts.ShouldBe(1);
        failed.Error!.ShouldContain("relay refused this message");
        failed.JobState.ShouldBe("queued");

        await Task.Delay(TimeSpan.FromSeconds(1.2)); // the first backoff step
        await host.RunWorkerAsync();
        var sent = await db.QuerySingleAsync<(string Status, int Attempts, string? Error, string JobState)>(sql, new { t = ws.TenantId });
        sent.ShouldBe(("sent", 2, null, "succeeded"));
        (await host.Smtp.WaitForAsync(m => m.Header("Subject") == "Flaky relay", TimeSpan.FromSeconds(10))).Body.ShouldContain("first try fails");
    }
}

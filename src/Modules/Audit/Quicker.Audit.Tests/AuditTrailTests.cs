using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Audit.Tests;

/// <summary>ADR-0015 through the public API: every mutation leaves an event, the explorer and export work, secrets stay out.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuditTrailTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static async Task<List<JsonElement>> TimelineAsync(HttpClient client, string entityType, Guid entityId)
    {
        var response = await client.GetAsync($"/api/v1/audit/records/{entityType}/{entityId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await response.ReadJsonAsync()).EnumerateArray()];
    }

    private static string? Str(JsonElement e, string property) => e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    [Fact]
    public async Task Every_mutation_leaves_an_event_with_before_and_after_values()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        // Sign-up: tenant and owner, recorded in the new tenant's chain although the request started anonymous.
        var tenantEvents = await TimelineAsync(owner, "tenant", ws.TenantId);
        tenantEvents.Single().GetProperty("action").GetString().ShouldBe("created");
        tenantEvents.Single().GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(ws.UserId);
        var userEvents = await TimelineAsync(owner, "user", ws.UserId);
        userEvents[0].GetProperty("action").GetString().ShouldBe("created");
        userEvents[0].GetProperty("after").GetProperty("email").GetString().ShouldBe(ws.OwnerEmail);
        var membershipEvents = await TimelineAsync(owner, "membership", ws.MembershipId);
        membershipEvents.Single().GetProperty("action").GetString().ShouldBe("permission_changed"); // owner role assignment, enriched with the captured membership row
        membershipEvents.Single().GetProperty("after").ValueKind.ShouldBe(JsonValueKind.Object);

        // Role: created by capture, permission change explicit + captured name diff, deleted by capture.
        var created = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = "clerk", name = new { en = "Clerk" }, description = "desk", grants = new[] { "identity.user.read" } }, Json)).ReadJsonAsync();
        var roleId = created.GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/roles/{roleId}", new { code = "clerk", name = new { en = "Clerk", ar = "كاتب" }, description = "front desk", grants = new[] { "identity.user.read", "identity.role.read" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var roleEvents = await TimelineAsync(owner, "role", roleId);
        roleEvents.Select(static e => e.GetProperty("action").GetString()).ShouldBe(["created", "permission_changed", "deleted"]);
        roleEvents[0].GetProperty("after").GetProperty("code").GetString().ShouldBe("clerk");
        roleEvents[0].GetProperty("after").GetProperty("name").GetProperty("en").GetString().ShouldBe("Clerk");
        roleEvents[0].GetProperty("entityDisplay").GetString().ShouldBe("clerk");
        var permissionChanged = roleEvents[1];
        permissionChanged.GetProperty("before").GetProperty("grants").GetArrayLength().ShouldBe(1);
        permissionChanged.GetProperty("after").GetProperty("grants").GetArrayLength().ShouldBe(2);
        permissionChanged.GetProperty("diff").GetProperty("grants").GetProperty("new").GetArrayLength().ShouldBe(2);
        permissionChanged.GetProperty("diff").GetProperty("description").GetProperty("old").GetString().ShouldBe("desk");
        roleEvents[2].GetProperty("before").GetProperty("description").GetString().ShouldBe("front desk");
        roleEvents.Select(static e => e.GetProperty("seq").GetInt64()).ShouldBeInOrder();
        roleEvents.All(e => e.GetProperty("actor").GetProperty("id").GetGuid() == ws.UserId).ShouldBeTrue();
        roleEvents.All(e => e.GetProperty("actor").GetProperty("display").GetString() == ws.OwnerEmail).ShouldBeTrue();
        tenantEvents.Single().GetProperty("actor").GetProperty("display").GetString().ShouldBe(ws.OwnerEmail); // named even though sign-up started anonymous

        // API key: captured creation, explicit revocation inheriting the captured diff.
        var key = await (await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "integration", scopes = new[] { "identity.user.read" } }, Json)).ReadJsonAsync();
        var keyId = key.GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/v1/api-keys/{keyId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var keyEvents = await TimelineAsync(owner, "api_key", keyId);
        keyEvents.Select(static e => e.GetProperty("action").GetString()).ShouldBe(["created", "revoked"]);
        keyEvents[0].GetProperty("after").GetProperty("name").GetString().ShouldBe("integration");
        keyEvents[0].GetProperty("after").TryGetProperty("keyHash", out _).ShouldBeFalse();
        keyEvents[1].GetProperty("diff").GetProperty("revokedAt").GetProperty("old").ValueKind.ShouldBe(JsonValueKind.Null);
        keyEvents[1].GetProperty("diff").GetProperty("revokedAt").GetProperty("new").ValueKind.ShouldBe(JsonValueKind.String);

        // Profile update: captured with a field diff.
        (await owner.PatchAsJsonAsync("/api/v1/me", new { displayName = "Owner Renamed" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        userEvents = await TimelineAsync(owner, "user", ws.UserId);
        var updated = userEvents.Last(static e => e.GetProperty("action").GetString() == "updated");
        updated.GetProperty("diff").GetProperty("displayName").GetProperty("old").GetString().ShouldBe("Owner " + ws.Slug);
        updated.GetProperty("diff").GetProperty("displayName").GetProperty("new").GetString().ShouldBe("Owner Renamed");
        updated.GetProperty("before").EnumerateObject().Select(static p => p.Name).ShouldBe(["displayName"]);

        // Invitation, acceptance (anonymous request re-homed to the tenant) and disabling.
        var clerkEmail = $"clerk-{ws.Slug}@example.test";
        var invited = await (await owner.PostAsJsonAsync("/api/v1/users/invite", new { email = clerkEmail, displayName = "Clerk", roleIds = Array.Empty<Guid>() }, Json)).ReadJsonAsync();
        var membershipId = invited.GetProperty("membershipId").GetGuid();
        var token = Api.Emails.LastTo(clerkEmail)!.TextBody.Split("token=")[1].Trim();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "clerk-passphrase-long-enough" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsync($"/api/v1/users/{membershipId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        membershipEvents = await TimelineAsync(owner, "membership", membershipId);
        membershipEvents.Select(static e => e.GetProperty("action").GetString()).ShouldBe(["invited", "updated", "state_changed"]);
        membershipEvents[0].GetProperty("after").GetProperty("email").GetString().ShouldBe(clerkEmail); // the explicit event keeps its own snapshot
        membershipEvents[1].GetProperty("diff").GetProperty("status").GetProperty("new").GetString().ShouldBe("active");
        membershipEvents[1].GetProperty("actor").GetProperty("display").GetString().ShouldNotBe("anonymous");
        membershipEvents[2].GetProperty("before").GetProperty("status").GetString().ShouldBe("active");
        membershipEvents[2].GetProperty("after").GetProperty("status").GetString().ShouldBe("disabled");
        membershipEvents.All(static e => e.GetProperty("entityDisplay").GetString()!.Contains('@')).ShouldBeTrue();

        // A login appears as a login event (sign-up issued the first session), never as a row update of the user's counters.
        var loginsBefore = userEvents.Count(static e => e.GetProperty("action").GetString() == "login");
        loginsBefore.ShouldBe(1);
        var login = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        userEvents = await TimelineAsync(owner, "user", ws.UserId);
        userEvents.Count(static e => e.GetProperty("action").GetString() == "login").ShouldBe(2);
        userEvents.Where(static e => e.GetProperty("action").GetString() == "updated").Any(static e => e.GetProperty("diff").TryGetProperty("lastLoginAt", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task Explorer_filters_pages_newest_first_and_exports_are_audited()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        foreach (var code in new[] { "r1", "r2", "r3" })
        {
            (await owner.PostAsJsonAsync("/api/v1/roles", new { code, name = new { en = code.ToUpperInvariant() }, description = "", grants = new[] { "identity.user.read" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Newest first, keyset paging over the role events (the three above plus the seeded templates).
        var page1 = await (await owner.GetAsync("/api/v1/audit/events?entityType=role&limit=2")).ReadJsonAsync();
        page1.GetProperty("items").GetArrayLength().ShouldBe(2);
        page1.GetProperty("items")[0].GetProperty("entityDisplay").GetString().ShouldBe("r3");
        var cursor = page1.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        var seen = page1.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("seq").GetInt64()).ToList();
        while (cursor is not null)
        {
            var page = await (await owner.GetAsync($"/api/v1/audit/events?entityType=role&limit=2&cursor={cursor}")).ReadJsonAsync();
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("seq").GetInt64()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        }

        var roleCreations = 3 + Identity.RoleTemplates.All.Count;
        seen.Count.ShouldBe(roleCreations);
        seen.Distinct().Count().ShouldBe(roleCreations);
        seen.ShouldBeInOrder(SortDirection.Descending);

        var all = await (await owner.GetAsync("/api/v1/audit/events?limit=200")).ReadJsonAsync();
        all.GetProperty("items").EnumerateArray().Count(static i => i.GetProperty("entityType").GetString() == "role" && i.GetProperty("action").GetString() == "created").ShouldBe(roleCreations);

        var byActor = await (await owner.GetAsync($"/api/v1/audit/events?actorId={ws.UserId}&action=created&q=r2")).ReadJsonAsync();
        byActor.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("entityDisplay").GetString()).ShouldBe(["r2"]);
        (await (await owner.GetAsync($"/api/v1/audit/events?actorId={Guid.NewGuid()}")).ReadJsonAsync()).GetProperty("items").GetArrayLength().ShouldBe(0);

        var detail = await (await owner.GetAsync($"/api/v1/audit/events/{page1.GetProperty("items")[0].GetProperty("id").GetGuid()}")).ReadJsonAsync();
        detail.GetProperty("hash").GetString()!.Length.ShouldBe(64);
        detail.GetProperty("after").GetProperty("code").GetString().ShouldBe("r3");

        var export = await owner.GetAsync("/api/v1/audit/export?entityType=role&action=created");
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-ndjson");
        var lines = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(roleCreations);
        lines.ShouldAllBe(static l => l.Contains("\"action\":\"created\"", StringComparison.Ordinal));

        var exported = await (await owner.GetAsync("/api/v1/audit/events?action=exported")).ReadJsonAsync();
        exported.GetProperty("items").GetArrayLength().ShouldBe(1);
        var exportDetail = await (await owner.GetAsync($"/api/v1/audit/events/{exported.GetProperty("items")[0].GetProperty("id").GetGuid()}")).ReadJsonAsync();
        exportDetail.GetProperty("details").GetProperty("rows").GetInt32().ShouldBe(roleCreations);
        exportDetail.GetProperty("details").GetProperty("filter").GetProperty("entityType").GetString().ShouldBe("role");

        // Reading the log needs the permission; another tenant sees nothing of this one.
        var other = await Api.SignupAsync();
        using var otherOwner = Api.ClientFor(other.AccessToken);
        (await TimelineAsync(otherOwner, "role", page1.GetProperty("items")[0].GetProperty("entityId").GetGuid())).ShouldBeEmpty();
        (await otherOwner.GetAsync($"/api/v1/audit/events/{page1.GetProperty("items")[0].GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var apiKey = await (await otherOwner.PostAsJsonAsync("/api/v1/api-keys", new { name = "ro", scopes = new[] { "identity.user.read" } }, Json)).ReadJsonAsync();
        using var limited = Api.ClientForApiKey(apiKey.GetProperty("key").GetString()!);
        var forbidden = await limited.GetAsync("/api/v1/audit/events");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await forbidden.ReadJsonAsync()).GetProperty("why").GetProperty("permission").GetString().ShouldBe("audit.event.read");
    }

    [Fact]
    public async Task Secrets_never_enter_the_log()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var key = await (await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "integration" }, Json)).ReadJsonAsync();
        var secret = key.GetProperty("key").GetString()!.Split('.')[1];
        (await owner.PostAsJsonAsync("/api/v1/sso-connections", new
        {
            code = "entra",
            displayName = "Entra",
            authority = "https://login.example.test/tenant",
            clientId = "client-1",
            clientSecret = "super-secret-client-value",
            scopes = "openid profile email",
            emailDomains = new[] { "example.test" },
            jitProvisioning = true,
        }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await owner.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = ws.OwnerPassword, newPassword = "another-long-passphrase-42" }, Json)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var export = await (await owner.GetAsync("/api/v1/audit/export")).Content.ReadAsStringAsync();
        export.ShouldNotContain(secret);
        export.ShouldNotContain("super-secret-client-value");
        export.ShouldNotContain("keyHash");
        export.ShouldNotContain("passwordHash");
        export.ShouldNotContain("clientSecretEnc");
        export.ShouldNotContain(ws.OwnerPassword);
        export.ShouldContain("password_changed");

        // The SSO connection event shows the connection without its secret.
        var sso = await (await owner.GetAsync("/api/v1/audit/events?entityType=sso_connection")).ReadJsonAsync();
        var ssoDetail = await (await owner.GetAsync($"/api/v1/audit/events/{sso.GetProperty("items")[0].GetProperty("id").GetGuid()}")).ReadJsonAsync();
        ssoDetail.GetProperty("after").GetProperty("clientId").GetString().ShouldBe("client-1");
        ssoDetail.GetProperty("after").TryGetProperty("clientSecretEnc", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Anonymous_security_events_land_on_the_platform_chain_for_operators_only()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = "definitely-wrong-password" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var refused = await owner.GetAsync("/api/v1/audit/platform/events");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCodeAsync()).ShouldBe("auth.operator_required");

        // The tenant chain never sees the failed attempt (the request had no tenant).
        var tenantView = await (await owner.GetAsync("/api/v1/audit/events?action=login_failed")).ReadJsonAsync();
        tenantView.GetProperty("items").GetArrayLength().ShouldBe(0);

        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await db.ExecuteAsync("UPDATE control.users SET is_platform_operator = true WHERE id = @id", new { id = ws.UserId });
        }

        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        using var operatorClient = Api.ClientFor(login.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        var platform = await (await operatorClient.GetAsync($"/api/v1/audit/platform/events?entityId={ws.UserId}&action=login_failed")).ReadJsonAsync();
        var failed = platform.GetProperty("items").EnumerateArray().ToList();
        failed.Count.ShouldBe(1);
        failed[0].GetProperty("reason").GetString().ShouldBe("bad_password");
        failed[0].GetProperty("actor").GetProperty("type").GetString().ShouldBe("anonymous");
        failed[0].GetProperty("entityDisplay").GetString().ShouldBe(ws.OwnerEmail);

        var verification = await (await operatorClient.PostAsync("/api/v1/audit/platform/chain/verify", null)).ReadJsonAsync();
        verification.GetProperty("status").GetString().ShouldBe("ok");
        verification.GetProperty("chain").GetString().ShouldBe("platform");
        verification.GetProperty("toSeq").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }
}

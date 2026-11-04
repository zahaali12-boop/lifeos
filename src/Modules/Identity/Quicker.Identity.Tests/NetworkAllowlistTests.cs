using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

[Collection(ApiCollection.Name)]
public sealed class NetworkAllowlistTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private static object Policy(params string[]? allowlist) => new
    {
        mfaRequired = false,
        sessionLifetimeHours = 336,
        accessTokenMinutes = 10,
        passwordMinLength = 12,
        stepUpWindowMinutes = 5,
        lockoutThreshold = 10,
        lockoutMinutes = 15,
        allowPasswordLogin = true,
        ipAllowlist = allowlist,
    };

    [Fact]
    public async Task An_allow_list_admits_members_only_from_its_networks_and_cannot_lock_out_the_admin_who_sets_it()
    {
        var ws = await Api.SignupAsync();
        using var office = Api.ClientFrom("203.0.113.5", ws.AccessToken);

        // A list that leaves out the address the change comes from would lock its author out; malformed entries are named.
        var lockout = await office.PutAsJsonAsync("/api/v1/tenant/policy", Policy("10.0.0.0/8"), Json);
        lockout.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await lockout.ErrorCodeAsync()).ShouldBe("tenant.policy_ip_lockout");
        var invalid = await office.PutAsJsonAsync("/api/v1/tenant/policy", Policy("203.0.113.0/24", "office-router", "10.0.0.0/33"), Json);
        (await invalid.ErrorCodeAsync()).ShouldBe("tenant.policy_ip_invalid");
        var why = (await invalid.ReadJsonAsync()).GetProperty("why").GetProperty("invalid").EnumerateArray().Select(static e => e.GetString()).ToList();
        why.ShouldBe(["office-router", "10.0.0.0/33"]);

        // Saved trimmed and without repeats.
        (await office.PutAsJsonAsync("/api/v1/tenant/policy", Policy(" 203.0.113.0/24 ", "2001:db8::/32", "203.0.113.0/24", "198.51.100.77"), Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = await (await office.GetAsync("/api/v1/tenant/policy")).ReadJsonAsync();
        saved.GetProperty("ipAllowlist").EnumerateArray().Select(static e => e.GetString()).ShouldBe(["203.0.113.0/24", "2001:db8::/32", "198.51.100.77"]);

        // The same session is honoured from inside the list (a range, a single address, IPv6) and refused from outside it,
        // including from a caller whose address is unknown.
        (await office.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var single = Api.ClientFrom("198.51.100.77", ws.AccessToken))
        {
            (await single.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var v6 = Api.ClientFrom("2001:db8::1", ws.AccessToken))
        {
            (await v6.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var cafe = Api.ClientFrom("198.51.100.78", ws.AccessToken);
        (await cafe.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var unknown = Api.ClientFor(ws.AccessToken))
        {
            (await unknown.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Signing in and refreshing from outside are refused after the password is checked, and say why.
        using var anonymousCafe = Api.ClientFrom("198.51.100.78");
        var outside = await anonymousCafe.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json);
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await outside.ErrorCodeAsync()).ShouldBe("auth.ip_not_allowed");
        var wrongPassword = await anonymousCafe.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = "not-the-password-at-all" }, Json);
        (await wrongPassword.ErrorCodeAsync()).ShouldNotBe("auth.ip_not_allowed");
        var refresh = await anonymousCafe.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = ws.RefreshToken }, Json);
        (await refresh.ErrorCodeAsync()).ShouldBe("auth.ip_not_allowed");

        using var anonymousOffice = Api.ClientFrom("203.0.113.9");
        var inside = await (await anonymousOffice.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        inside.GetProperty("status").GetString().ShouldBe("ok");

        // A forwarded address is believed only from a trusted proxy; anyone else sending the header is judged by their own.
        using (var viaProxy = Api.ClientFrom("192.0.2.10", ws.AccessToken))
        {
            viaProxy.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");
            (await viaProxy.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var spoofed = Api.ClientFrom("198.51.100.78", ws.AccessToken))
        {
            spoofed.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");
            (await spoofed.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var spoofedSignIn = Api.ClientFrom("198.51.100.78"))
        {
            spoofedSignIn.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.20");
            var spoofedLogin = await spoofedSignIn.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json);
            (await spoofedLogin.ErrorCodeAsync()).ShouldBe("auth.ip_not_allowed");
        }

        // Clearing the list opens the workspace to every network again.
        (await office.PutAsJsonAsync("/api/v1/tenant/policy", Policy(null), Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cafe.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

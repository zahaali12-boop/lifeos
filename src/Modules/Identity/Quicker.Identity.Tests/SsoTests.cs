using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

[Collection(ApiCollection.Name)]
public sealed class SsoTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    [Fact]
    public async Task Oidc_code_flow_provisions_the_member_maps_groups_to_roles_and_hands_the_spa_a_one_time_code()
    {
        await using var idp = await FakeOidcProvider.StartAsync();
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        var roles = await (await owner.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var auditorRole = roles.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "auditor").GetProperty("id").GetGuid();

        var connection = await owner.PostAsJsonAsync("/api/v1/sso-connections", new
        {
            code = "corp",
            displayName = "Corporate SSO",
            authority = idp.Issuer,
            clientId = idp.ClientId,
            clientSecret = idp.ClientSecret,
            scopes = "openid profile email",
            emailDomains = new[] { "example.test" },
            jitProvisioning = true,
            groupClaim = "groups",
            groupRoleMap = new Dictionary<string, Guid> { ["finance-auditors"] = auditorRole },
        }, Json);
        connection.StatusCode.ShouldBe(HttpStatusCode.Created);

        idp.UserEmail = $"sso-{ws.Slug}@example.test";
        idp.Groups = ["finance-auditors"];

        // 1. Start: the API redirects to the provider with PKCE, state and nonce.
        var start = await Api.Client.GetAsync($"/api/v1/auth/sso/{ws.Slug}/corp/start?returnTo=/dashboard");
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var authorizeUrl = start.Headers.Location!.ToString();
        authorizeUrl.ShouldStartWith(idp.Issuer + "/authorize");
        authorizeUrl.ShouldContain("code_challenge_method=S256");

        // 2. The provider authenticates the user and redirects back with a code.
        using var idpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var providerRedirect = await idpClient.GetAsync(new Uri(authorizeUrl));
        providerRedirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var callbackUrl = providerRedirect.Headers.Location!.ToString();
        callbackUrl.ShouldStartWith("http://localhost/api/v1/auth/sso/callback");

        // 3. The callback exchanges the code, validates the id_token and redirects the SPA with a one-time code.
        var callback = await Api.Client.GetAsync(callbackUrl.Replace("http://localhost", string.Empty, StringComparison.Ordinal));
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var spaUrl = callback.Headers.Location!.ToString();
        spaUrl.ShouldStartWith("http://localhost/dashboard?sso_code=");
        idp.TokenRequests.ShouldBe(1);

        // 4. The SPA exchanges the code for tokens; the code is single use.
        var ssoCode = spaUrl.Split("sso_code=")[1];
        var exchanged = await Api.Client.PostAsJsonAsync("/api/v1/auth/sso/exchange", new { code = ssoCode }, Json);
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await exchanged.ReadJsonAsync();
        tokens.GetProperty("user").GetProperty("email").GetString().ShouldBe(idp.UserEmail);
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/sso/exchange", new { code = ssoCode }, Json)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The JIT-provisioned member holds the auditor role from the group mapping.
        using var ssoUser = Api.ClientFor(tokens.GetProperty("accessToken").GetString()!);
        var me = await (await ssoUser.GetAsync("/api/v1/me")).ReadJsonAsync();
        me.GetProperty("authMethods").GetString().ShouldBe("sso");
        me.GetProperty("permissions").EnumerateArray().Select(static p => p.GetString()).ShouldContain("audit.event.read");
        (await ssoUser.GetAsync("/api/v1/roles")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Oidc_refuses_users_outside_the_allowed_domains_and_tampered_state()
    {
        await using var idp = await FakeOidcProvider.StartAsync();
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await owner.PostAsJsonAsync("/api/v1/sso-connections", new
        {
            code = "corp",
            displayName = "Corporate SSO",
            authority = idp.Issuer,
            clientId = idp.ClientId,
            clientSecret = idp.ClientSecret,
            scopes = "openid email",
            emailDomains = new[] { "allowed.test" },
            jitProvisioning = true,
        }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        idp.UserEmail = "outsider@elsewhere.test";
        var start = await Api.Client.GetAsync($"/api/v1/auth/sso/{ws.Slug}/corp/start");
        using var idpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var providerRedirect = await idpClient.GetAsync(start.Headers.Location!);
        var callbackUrl = providerRedirect.Headers.Location!.ToString().Replace("http://localhost", string.Empty, StringComparison.Ordinal);

        var refused = await Api.Client.GetAsync(callbackUrl);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCodeAsync()).ShouldBe("sso.domain_not_allowed");

        var tampered = await Api.Client.GetAsync("/api/v1/auth/sso/callback?code=abc&state=forged");
        tampered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await tampered.ErrorCodeAsync()).ShouldBe("sso.state_invalid");
    }

    [Fact]
    public async Task Password_sign_in_turns_off_only_while_a_connection_is_active_and_that_connection_cannot_then_be_removed()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        static object Policy(bool allowPasswordLogin, int stepUpWindowMinutes = 5) => new
        {
            mfaRequired = false,
            sessionLifetimeHours = 336,
            accessTokenMinutes = 10,
            passwordMinLength = 12,
            stepUpWindowMinutes,
            lockoutThreshold = 10,
            lockoutMinutes = 15,
            allowPasswordLogin,
        };

        var locked = await owner.PutAsJsonAsync("/api/v1/tenant/policy", Policy(false), Json);
        locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await locked.ErrorCodeAsync()).ShouldBe("tenant.password_login_required");
        var zeroWindow = await owner.PutAsJsonAsync("/api/v1/tenant/policy", Policy(true, stepUpWindowMinutes: 0), Json);
        (await zeroWindow.ErrorCodeAsync()).ShouldBe("tenant.policy_invalid");

        var connection = new { code = "corp", displayName = "Corporate SSO", authority = "https://idp.example.test", clientId = "quicker", clientSecret = "s3cret", scopes = "openid email", emailDomains = new[] { "example.test" }, jitProvisioning = false, isActive = true };
        var created = await owner.PostAsJsonAsync("/api/v1/sso-connections", connection, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync("/api/v1/tenant/policy", Policy(false), Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // With password sign-in off, the only active connection is the only way in: it can be neither disabled nor removed.
        var disabled = await owner.PutAsJsonAsync($"/api/v1/sso-connections/{id}", connection with { isActive = false }, Json);
        (await disabled.ErrorCodeAsync()).ShouldBe("sso.connection_required");
        var removed = await owner.DeleteAsync($"/api/v1/sso-connections/{id}");
        (await removed.ErrorCodeAsync()).ShouldBe("sso.connection_required");

        (await owner.PutAsJsonAsync("/api/v1/tenant/policy", Policy(true), Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/v1/sso-connections/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_provider_on_a_private_network_is_not_called_and_the_start_says_why()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var connection = new { code = "intranet", displayName = "Intranet SSO", authority = "https://10.20.30.40", clientId = "quicker", clientSecret = "s3cret", scopes = "openid email", emailDomains = new[] { "example.test" }, jitProvisioning = false, isActive = true };
        (await owner.PostAsJsonAsync("/api/v1/sso-connections", connection, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var start = await Api.Client.GetAsync($"/api/v1/auth/sso/{ws.Slug}/intranet/start");
        start.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await start.ErrorCodeAsync()).ShouldBe("sso.provider_unreachable");
        (await start.Content.ReadAsStringAsync()).ShouldContain("not on the public internet (10.20.30.40)");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OtpNet;
using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    [Fact]
    public async Task Signup_creates_tenant_owner_default_roles_and_a_session()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);

        var me = await (await client.GetAsync("/api/v1/me")).ReadJsonAsync();
        me.GetProperty("isOwner").GetBoolean().ShouldBeTrue();
        me.GetProperty("user").GetProperty("email").GetString().ShouldBe(ws.OwnerEmail);
        me.GetProperty("permissions")[0].GetString().ShouldBe("*");

        var roles = await (await client.GetAsync("/api/v1/roles")).ReadJsonAsync();
        roles.EnumerateArray().Select(static r => r.GetProperty("code").GetString()).ShouldContain("owner");
        roles.EnumerateArray().Count().ShouldBe(10);

        var members = await (await client.GetAsync("/api/v1/users")).ReadJsonAsync();
        var owner = members.EnumerateArray().Single();
        owner.GetProperty("assignments").EnumerateArray().Single().GetProperty("roleCode").GetString().ShouldBe("owner");
    }

    [Fact]
    public async Task Duplicate_slug_and_duplicate_email_are_refused()
    {
        var ws = await Api.SignupAsync();
        var sameSlug = await Api.Client.PostAsJsonAsync("/api/v1/auth/signup", new { tenantName = "x", slug = ws.Slug, ownerEmail = "other@example.test", ownerName = "x", password = "correct-horse-battery-staple" }, Json);
        sameSlug.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameSlug.ErrorCodeAsync()).ShouldBe("tenant.slug_taken");

        var sameEmail = await Api.Client.PostAsJsonAsync("/api/v1/auth/signup", new { tenantName = "x", slug = "other-" + ws.Slug, ownerEmail = ws.OwnerEmail, ownerName = "x", password = "correct-horse-battery-staple" }, Json);
        sameEmail.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameEmail.ErrorCodeAsync()).ShouldBe("user.email_taken");
    }

    [Fact]
    public async Task Weak_passwords_are_refused_with_the_rule_that_failed()
    {
        var response = await Api.Client.PostAsJsonAsync("/api/v1/auth/signup", new { tenantName = "x", slug = "weak" + Guid.NewGuid().ToString("N")[^8..], ownerEmail = "weak@example.test", ownerName = "x", password = "short" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.ReadJsonAsync();
        body.GetProperty("code").GetString().ShouldBe("password.too_short");
        body.GetProperty("why").GetProperty("minLength").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_email_and_refuses_wrong_passwords_without_leaking_existence()
    {
        var ws = await Api.SignupAsync();
        var ok = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail.ToUpperInvariant(), password = ws.OwnerPassword }, Json);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("ok");

        var wrong = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = "wrong-password-value" }, Json);
        var unknown = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody-" + ws.Slug + "@example.test", password = "wrong-password-value" }, Json);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.ErrorCodeAsync()).ShouldBe(await unknown.ErrorCodeAsync());
    }

    [Fact]
    public async Task Ten_failed_logins_lock_the_account_and_the_counter_survives_the_refusals()
    {
        var ws = await Api.SignupAsync();
        for (var i = 0; i < 10; i++)
        {
            var response = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = "wrong-password-value" }, Json);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json);
        locked.StatusCode.ShouldBe(HttpStatusCode.Locked);
        (await locked.ErrorCodeAsync()).ShouldBe("auth.locked");

        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        var after = await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_whole_family()
    {
        var ws = await Api.SignupAsync();
        var first = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = ws.RefreshToken }, Json)).ReadJsonAsync();
        var rotated = first.GetProperty("refreshToken").GetString()!;
        rotated.ShouldNotBe(ws.RefreshToken);

        var reuse = await Api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = ws.RefreshToken }, Json);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reuse.ErrorCodeAsync()).ShouldBe("auth.session_revoked");

        var rotatedNow = await Api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = rotated }, Json);
        (await rotatedNow.ErrorCodeAsync()).ShouldBe("auth.session_revoked");

        using var client = Api.ClientFor(ws.AccessToken);
        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sessions_expire_with_the_tenant_policy_and_logout_revokes_immediately()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);
        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = await (await client.GetAsync("/api/v1/me/sessions")).ReadJsonAsync();
        sessions.EnumerateArray().Single().GetProperty("isCurrent").GetBoolean().ShouldBeTrue();

        (await client.PostAsJsonAsync("/api/v1/me/logout", new { }, Json)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refresh = await Api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = ws.RefreshToken }, Json);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Totp_enrolment_makes_mfa_mandatory_and_rejects_replayed_codes()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);

        var enrol = await (await client.PostAsync("/api/v1/me/mfa/totp/enroll", null)).ReadJsonAsync();
        var secret = Base32Encoding.ToBytes(enrol.GetProperty("secret").GetString()!);
        var methodId = enrol.GetProperty("methodId").GetGuid();
        var totp = new Totp(secret);

        var confirm = await client.PostAsJsonAsync("/api/v1/me/mfa/totp/confirm", new { methodId, code = totp.ComputeTotp(Api.Clock.UtcNow.UtcDateTime) }, Json);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = (await confirm.ReadJsonAsync()).GetProperty("codes").EnumerateArray().Select(static c => c.GetString()!).ToList();
        codes.Count.ShouldBe(10);

        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        login.GetProperty("status").GetString().ShouldBe("mfa_required");
        var challenge = login.GetProperty("challengeToken").GetString();

        Api.Clock.Advance(TimeSpan.FromSeconds(31)); // next time step, otherwise the enrolment code would be a replay
        var code = totp.ComputeTotp(Api.Clock.UtcNow.UtcDateTime);
        var verified = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge, code }, Json);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verified.ReadJsonAsync()).GetProperty("tokens").GetProperty("user").GetProperty("hasMfa").GetBoolean().ShouldBeTrue();

        var replay = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge, code }, Json);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await replay.ErrorCodeAsync()).ShouldBe("auth.mfa_invalid");

        // A recovery code works exactly once.
        var login2 = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        var challenge2 = login2.GetProperty("challengeToken").GetString();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge2, recoveryCode = codes[0] }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var login3 = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        var again = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = login3.GetProperty("challengeToken").GetString(), recoveryCode = codes[0] }, Json);
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sensitive_actions_require_recent_authentication_and_step_up_restores_it()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);

        Api.Clock.Advance(TimeSpan.FromMinutes(6)); // beyond the 5-minute step-up window, within the token lifetime
        var stale = await client.PostAsJsonAsync("/api/v1/api-keys", new { name = "k" }, Json);
        stale.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stale.ErrorCodeAsync()).ShouldBe("auth.step_up_required");

        var stepUp = await client.PostAsJsonAsync("/api/v1/me/step-up", new { password = ws.OwnerPassword }, Json);
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fresh = (await stepUp.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
        using var freshClient = Api.ClientFor(fresh);
        (await freshClient.PostAsJsonAsync("/api/v1/api-keys", new { name = "k" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Password_reset_uses_a_single_use_emailed_token_and_revokes_sessions()
    {
        var ws = await Api.SignupAsync();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/password/forgot", new { email = ws.OwnerEmail }, Json)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var mail = Api.Emails.LastTo(ws.OwnerEmail).ShouldNotBeNull();
        var token = mail.TextBody.Split("token=")[1].Trim();

        var reset = await Api.Client.PostAsJsonAsync("/api/v1/auth/password/reset", new { token, password = "another-long-passphrase-42" }, Json);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var client = Api.ClientFor(ws.AccessToken);
        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = "another-long-passphrase-42" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/password/reset", new { token, password = "yet-another-long-passphrase" }, Json)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Tenant_policy_can_require_mfa_before_the_first_session()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);
        var policy = await (await client.GetAsync("/api/v1/tenant/policy")).ReadJsonAsync();
        var update = await client.PutAsJsonAsync("/api/v1/tenant/policy", new { mfaRequired = true, sessionLifetimeHours = policy.GetProperty("sessionLifetimeHours").GetInt32(), accessTokenMinutes = 10, passwordMinLength = 12, stepUpWindowMinutes = 5, lockoutThreshold = 10, lockoutMinutes = 15, allowPasswordLogin = true }, Json);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        login.GetProperty("status").GetString().ShouldBe("mfa_enrollment_required");
        var challenge = login.GetProperty("challengeToken").GetString();

        var enrol = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/enroll/totp", new { challengeToken = challenge }, Json)).ReadJsonAsync();
        var totp = new Totp(Base32Encoding.ToBytes(enrol.GetProperty("secret").GetString()!));
        var confirm = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/enroll/totp/confirm", new
        {
            challengeToken = challenge,
            methodId = enrol.GetProperty("methodId").GetGuid(),
            code = totp.ComputeTotp(Api.Clock.UtcNow.UtcDateTime),
            email = ws.OwnerEmail,
            password = ws.OwnerPassword,
        }, Json);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await confirm.ReadJsonAsync();
        body.GetProperty("login").GetProperty("status").GetString().ShouldBe("mfa_required");
        body.GetProperty("recoveryCodes").GetArrayLength().ShouldBe(10);
    }
}

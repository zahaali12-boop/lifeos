using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Identity.Application;
using Quicker.Identity.Contracts;
using Quicker.Identity.Security;
using Quicker.Tenancy.Contracts;
using Quicker.Web;

namespace Quicker.Identity.Api;

/// <summary>The identity surface of /api/v1 (ADR-0012, ADR-0014). Every mutation runs in the request's unit of work.</summary>
public static class IdentityEndpoints
{
    public static RouteGroupBuilder MapIdentityEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        // ---------------------------------------------------------------- anonymous authentication flows
        var auth = api.MapGroup("/auth").WithTags("Authentication");

        auth.MapPost("/signup", async (SignupRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            ApiProblems.Ok(await accounts.SignupAsync(request, Client(http), ct)))
            .WithSummary("Create a workspace and its owner account");

        auth.MapPost("/login", async (LoginRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.LoginAsync(request, Client(http), ct)))
            .WithSummary("Password sign-in; may answer with a tenant choice or an MFA challenge");

        auth.MapPost("/select-tenant", async (SelectTenantRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SelectTenantAsync(request, Client(http), ct)));

        auth.MapPost("/mfa/verify", async (MfaVerifyRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.VerifyMfaAsync(request, Client(http), ct)))
            .WithSummary("Complete an MFA challenge with a TOTP code, a recovery code or a WebAuthn assertion");

        auth.MapPost("/mfa/webauthn/options", async (MfaWebAuthnOptionsRequest request, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.WebAuthnLoginOptionsAsync(request.ChallengeToken, ct)));

        auth.MapPost("/mfa/enroll/totp", async (MfaWebAuthnOptionsRequest request, AccountService accounts, CancellationToken ct) =>
        {
            var user = accounts.UserFromEnrollmentChallenge(request.ChallengeToken);
            return user.IsFailure ? ApiProblems.From(user.Error!) : Results.Ok(await accounts.StartTotpAsync(user.Value, ct));
        }).WithSummary("Start TOTP enrolment when the workspace policy requires MFA before the first session");

        auth.MapPost("/mfa/enroll/totp/confirm", async (TotpConfirmWithChallenge request, HttpContext http, AccountService accounts, AuthService service, CancellationToken ct) =>
        {
            var user = accounts.UserFromEnrollmentChallenge(request.ChallengeToken);
            if (user.IsFailure)
            {
                return ApiProblems.From(user.Error!);
            }

            var confirmed = await accounts.ConfirmTotpAsync(user.Value, new TotpConfirmRequest(request.MethodId, request.Code), ct);
            if (confirmed.IsFailure)
            {
                return ApiProblems.From(confirmed.Error!);
            }

            // Enrolment done: continue the login as an MFA challenge so the same code proves possession.
            var login = await service.LoginAsync(new LoginRequest(request.Email, request.Password, request.TenantSlug), Client(http), ct);
            return ApiProblems.From(login, r => Results.Ok(new { login = r, recoveryCodes = confirmed.Value.Codes }));
        });

        auth.MapPost("/refresh", async (RefreshRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.RefreshAsync(request, Client(http), ct)))
            .WithSummary("Rotate a refresh token");

        auth.MapPost("/password/forgot", async (ForgotPasswordRequest request, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.ForgotPasswordAsync(request, ct);
            return Results.Accepted();
        });

        auth.MapPost("/password/reset", async (ResetPasswordRequest request, AccountService accounts, CancellationToken ct) =>
            ApiProblems.NoContent(await accounts.ResetPasswordAsync(request, ct)));

        auth.MapPost("/invitations/accept", async (AcceptInvitationRequest request, HttpContext http, AccountService accounts, CancellationToken ct) =>
            ApiProblems.Ok(await accounts.AcceptInvitationAsync(request, Client(http), ct)));

        auth.MapGet("/sso/{tenantSlug}/{connectionCode}/start", async (string tenantSlug, string connectionCode, string? returnTo, SsoService sso, CancellationToken ct) =>
            ApiProblems.From(await sso.StartAsync(tenantSlug, connectionCode, returnTo, ct), static r => Results.Redirect(r.RedirectUrl)))
            .WithSummary("Redirects to the tenant's identity provider");

        auth.MapGet("/sso/callback", async (string? code, string? state, string? error, HttpContext http, SsoService sso, CancellationToken ct) =>
            ApiProblems.From(await sso.CallbackAsync(code, state, error, Client(http), ct), static url => Results.Redirect(url)))
            .ExcludeFromDescription();

        auth.MapPost("/sso/exchange", async (SsoExchangeRequest request, SsoService sso, CancellationToken ct) =>
            ApiProblems.Ok(await sso.ExchangeAsync(request.Code, ct)));

        // ---------------------------------------------------------------- authenticated: self-service
        var me = api.MapGroup("/me").WithTags("My account").RequireAuthorization();

        me.MapGet("/", async (CurrentPrincipal current, ITenantDirectory tenants, CancellationToken ct) =>
        {
            var p = current.Required;
            var tenant = (await tenants.FindByIdAsync(p.TenantId, ct))!;
            var permissions = p.IsOwner ? ["*"] : p.Grants.Select(static g => g.Permission).Distinct(StringComparer.Ordinal).OrderBy(static k => k, StringComparer.Ordinal).ToList();
            return Results.Ok(new MeResponse(
                new UserSummary(p.UserId.Value, p.Email, p.DisplayName, p.Language, string.Empty, string.Empty, p.AuthMethods.Contains("mfa", StringComparison.Ordinal), p.IsPlatformOperator),
                AuthService.Summary(tenant), p.MembershipId.Value, p.IsOwner, permissions, p.Grants, p.FieldRules, p.DocumentTypeRules, p.AuthTime, p.AuthMethods));
        }).WithSummary("Who am I, with effective permissions");

        me.MapPatch("/", async (UpdateProfileRequest request, CurrentPrincipal current, AccountService accounts, CancellationToken ct) =>
            ApiProblems.Ok(await accounts.UpdateProfileAsync(current.Required.UserId.Value, request, ct)));

        me.MapPost("/password", async (ChangePasswordRequest request, HttpContext http, CurrentPrincipal current, AccountService accounts, ITenantDirectory tenants, CancellationToken ct) =>
        {
            var tenant = (await tenants.FindByIdAsync(current.Required.TenantId, ct))!;
            return ApiProblems.NoContent(await accounts.ChangePasswordAsync(current.Required.UserId.Value, SessionId(http) ?? Guid.Empty, request, tenant.Policy, ct));
        });

        me.MapPost("/logout", async (LogoutRequest? request, HttpContext http, CurrentPrincipal current, AuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(SessionId(http), current.Required.UserId.Value, request ?? new LogoutRequest(), ct);
            return Results.NoContent();
        });

        me.MapPost("/step-up", async (StepUpRequest request, HttpContext http, CurrentPrincipal current, AuthService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.StepUpAsync(SessionId(http) ?? Guid.Empty, current.Required.UserId.Value, request, Client(http), ct)))
            .WithSummary("Re-prove identity for sensitive actions");

        me.MapGet("/sessions", async (HttpContext http, CurrentPrincipal current, AuthService service, CancellationToken ct) =>
            Results.Ok(await service.ListSessionsAsync(current.Required.UserId.Value, SessionId(http) ?? Guid.Empty, ct)));

        me.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, CurrentPrincipal current, AuthService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.RevokeSessionAsync(current.Required.UserId.Value, sessionId, ct)));

        me.MapGet("/mfa", async (CurrentPrincipal current, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListMfaMethodsAsync(current.Required.UserId.Value, ct)));

        me.MapPost("/mfa/totp/enroll", async (CurrentPrincipal current, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.StartTotpAsync(current.Required.UserId.Value, ct)));

        me.MapPost("/mfa/totp/confirm", async (TotpConfirmRequest request, CurrentPrincipal current, AccountService accounts, CancellationToken ct) =>
            ApiProblems.Ok(await accounts.ConfirmTotpAsync(current.Required.UserId.Value, request, ct)));

        me.MapPost("/mfa/recovery-codes", async (CurrentPrincipal current, AccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.RegenerateRecoveryCodesAsync(current.Required.UserId.Value, ct))).RequireRecentAuth();

        me.MapDelete("/mfa/{methodId:guid}", async (Guid methodId, CurrentPrincipal current, AccountService accounts, ITenantDirectory tenants, CancellationToken ct) =>
        {
            var tenant = (await tenants.FindByIdAsync(current.Required.TenantId, ct))!;
            return ApiProblems.NoContent(await accounts.RemoveMfaMethodAsync(current.Required.UserId.Value, methodId, tenant.Policy.MfaRequired, ct));
        }).RequireRecentAuth();

        me.MapPost("/mfa/webauthn/register/options", async (CurrentPrincipal current, AuthService service, WebAuthnService webAuthn, CancellationToken ct) =>
        {
            var user = (await service.LoadUserAsync(current.Required.UserId.Value, ct))!;
            return Results.Ok(await webAuthn.RegistrationOptionsAsync(user, ct));
        });

        me.MapPost("/mfa/webauthn/register/verify", async (WebAuthnRegisterVerifyRequest request, CurrentPrincipal current, AuthService service, WebAuthnService webAuthn, CancellationToken ct) =>
        {
            var user = (await service.LoadUserAsync(current.Required.UserId.Value, ct))!;
            return ApiProblems.Ok(await webAuthn.CompleteRegistrationAsync(user, request, ct));
        });

        // ---------------------------------------------------------------- administration: members
        var users = api.MapGroup("/users").WithTags("Members").RequireAuthorization();
        users.MapGet("/", async (RoleService service, CancellationToken ct) => TypedResults.Ok(await service.ListMembersAsync(ct))).RequirePermission(IdentityPermissions.UserRead);
        users.MapGet("/{membershipId:guid}", async (Guid membershipId, RoleService service, CancellationToken ct) =>
            ApiProblems.Found(await service.MemberAsync(membershipId, ct), "member", membershipId))
            .RequirePermission(IdentityPermissions.UserRead);
        users.MapPost("/invite", async (InviteUserRequest request, CurrentPrincipal current, AccountService accounts, ITenantDirectory tenants, CancellationToken ct) =>
        {
            var p = current.Required;
            var tenant = (await tenants.FindByIdAsync(p.TenantId, ct))!;
            return ApiProblems.Created(await accounts.InviteAsync(p.TenantId.Value, p.UserId.Value, request, tenant.Name, ct), static m => $"/api/v1/users/{m.MembershipId}");
        }).RequirePermission(IdentityPermissions.UserInvite);
        users.MapPost("/{membershipId:guid}/disable", async (Guid membershipId, CurrentPrincipal current, RoleService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetMemberStatusAsync(membershipId, false, current.Required.MembershipId.Value, ct)))
            .RequirePermission(IdentityPermissions.UserManage).RequireRecentAuth();
        users.MapPost("/{membershipId:guid}/enable", async (Guid membershipId, CurrentPrincipal current, RoleService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetMemberStatusAsync(membershipId, true, current.Required.MembershipId.Value, ct)))
            .RequirePermission(IdentityPermissions.UserManage);
        users.MapPost("/{membershipId:guid}/assignments", async (Guid membershipId, AssignRoleRequest request, CurrentPrincipal current, RoleService service, CancellationToken ct) =>
            ApiProblems.Created(await service.AssignAsync(membershipId, request, current.Required.UserId.Value, ct), static a => $"/api/v1/assignments/{a.Id}"))
            .RequirePermission(IdentityPermissions.AssignmentManage);
        api.MapDelete("/assignments/{assignmentId:guid}", async (Guid assignmentId, RoleService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.UnassignAsync(assignmentId, ct)))
            .WithTags("Members").RequireAuthorization().RequirePermission(IdentityPermissions.AssignmentManage);

        // ---------------------------------------------------------------- administration: roles
        var roles = api.MapGroup("/roles").WithTags("Roles").RequireAuthorization();
        roles.MapGet("/", async (RoleService service, CancellationToken ct) => TypedResults.Ok(await service.ListRolesAsync(ct))).RequirePermission(IdentityPermissions.RoleRead);
        roles.MapGet("/{roleId:guid}", async (Guid roleId, RoleService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetRoleAsync(roleId, ct), "role", roleId))
            .RequirePermission(IdentityPermissions.RoleRead);
        roles.MapPost("/", async (SaveRoleRequest request, RoleService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateRoleAsync(request, ct), static r => $"/api/v1/roles/{r.Id}"))
            .RequirePermission(IdentityPermissions.RoleManage);
        roles.MapPut("/{roleId:guid}", async (Guid roleId, SaveRoleRequest request, RoleService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateRoleAsync(roleId, request, ct)))
            .RequirePermission(IdentityPermissions.RoleManage);
        roles.MapDelete("/{roleId:guid}", async (Guid roleId, RoleService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteRoleAsync(roleId, ct)))
            .RequirePermission(IdentityPermissions.RoleManage);

        // ---------------------------------------------------------------- administration: segregation of duties
        var sod = api.MapGroup("/sod").WithTags("Segregation of duties").RequireAuthorization();
        sod.MapGet("/rules", async (RoleService service, CancellationToken ct) => TypedResults.Ok(await service.ListSodRulesAsync(ct))).RequirePermission(IdentityPermissions.SodRead);
        sod.MapPost("/rules", async (SaveSodRuleRequest request, RoleService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveSodRuleAsync(null, request, ct), static r => $"/api/v1/sod/rules/{r.Id}")).RequirePermission(IdentityPermissions.SodManage);
        sod.MapPut("/rules/{ruleId:guid}", async (Guid ruleId, SaveSodRuleRequest request, RoleService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveSodRuleAsync(ruleId, request, ct))).RequirePermission(IdentityPermissions.SodManage);
        sod.MapPost("/exceptions", async (SodExceptionRequest request, CurrentPrincipal current, RoleService service, CancellationToken ct) =>
            ApiProblems.From(await service.AddSodExceptionAsync(request, current.Required.UserId.Value, ct), static id => Results.Created($"/api/v1/sod/exceptions/{id}", new { id })))
            .RequirePermission(IdentityPermissions.SodManage);
        sod.MapGet("/report", async (RoleService service, CancellationToken ct) => TypedResults.Ok(await service.SodReportAsync(ct))).RequirePermission(IdentityPermissions.SodRead);

        // ---------------------------------------------------------------- administration: API keys, SSO, policy, metadata
        var keys = api.MapGroup("/api-keys").WithTags("API keys").RequireAuthorization();
        keys.MapGet("/", async (ApiKeyService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct))).RequirePermission(IdentityPermissions.ApiKeyManage);
        keys.MapPost("/", async (CreateApiKeyRequest request, CurrentPrincipal current, ApiKeyService service, CancellationToken ct) =>
        {
            var p = current.Required;
            return ApiProblems.Created(await service.CreateAsync(p.TenantId.Value, p.MembershipId.Value, p.UserId.Value, request, ct), static k => $"/api/v1/api-keys/{k.Id}");
        }).RequirePermission(IdentityPermissions.ApiKeyManage).RequireRecentAuth();
        keys.MapDelete("/{keyId:guid}", async (Guid keyId, ApiKeyService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.RevokeAsync(keyId, ct))).RequirePermission(IdentityPermissions.ApiKeyManage);

        var sso = api.MapGroup("/sso-connections").WithTags("Single sign-on").RequireAuthorization();
        sso.MapGet("/", async (CurrentPrincipal current, SsoService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(current.Required.TenantId.Value, ct))).RequirePermission(IdentityPermissions.SsoManage);
        sso.MapPost("/", async (SaveSsoConnectionRequest request, CurrentPrincipal current, SsoService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveAsync(current.Required.TenantId.Value, null, request, ct), static c => $"/api/v1/sso-connections/{c.Id}"))
            .RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuth();
        sso.MapPut("/{connectionId:guid}", async (Guid connectionId, SaveSsoConnectionRequest request, CurrentPrincipal current, SsoService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveAsync(current.Required.TenantId.Value, connectionId, request, ct)))
            .RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuth();
        sso.MapDelete("/{connectionId:guid}", async (Guid connectionId, CurrentPrincipal current, SsoService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(current.Required.TenantId.Value, connectionId, ct)))
            .RequirePermission(IdentityPermissions.SsoManage).RequireRecentAuth();

        var tenant = api.MapGroup("/tenant").WithTags("Tenant").RequireAuthorization();
        tenant.MapGet("/policy", async (CurrentPrincipal current, ITenantDirectory tenants, CancellationToken ct) =>
            Results.Ok((await tenants.FindByIdAsync(current.Required.TenantId, ct))!.Policy)).RequirePermission(IdentityPermissions.TenantSettingsManage);
        tenant.MapPut("/policy", async (TenantSecurityPolicy policy, CurrentPrincipal current, ITenantProvisioner provisioner, ITenantDirectory tenants, CancellationToken ct) =>
        {
            var result = await provisioner.UpdatePolicyAsync(current.Required.TenantId, policy, ct);
            return ApiProblems.From(result, () => Results.Ok(policy));
        }).RequirePermission(IdentityPermissions.TenantSettingsManage).RequireRecentAuth();

        var meta = api.MapGroup("/meta").WithTags("Metadata").RequireAuthorization();
        meta.MapGet("/permissions", static () => Results.Ok(PermissionCatalog.All));
        meta.MapGet("/role-templates", static () => Results.Ok(RoleTemplates.All.Select(static t => new { t.Code, Name = t.Name.Values, t.Description, t.Grants })));

        return api;
    }

    private static ClientInfo Client(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        IPAddress? ip = forwarded is not null && IPAddress.TryParse(forwarded, out var parsed) ? parsed : http.Connection.RemoteIpAddress;
        return new ClientInfo(ip, http.Request.Headers.UserAgent.FirstOrDefault());
    }

    private static Guid? SessionId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst(QuickerClaims.Session)?.Value, out var id) ? id : null;
}

public sealed record TotpConfirmWithChallenge(string ChallengeToken, Guid MethodId, string Code, string Email, string Password, string? TenantSlug = null);

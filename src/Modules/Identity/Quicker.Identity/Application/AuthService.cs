using System.Net;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Identity.Application;

/// <summary>Request metadata captured for sessions and the audit log.</summary>
public sealed record ClientInfo(IPAddress? Ip, string? UserAgent);

/// <summary>
/// Password login with lockout, tenant selection, MFA challenge, session issuance with rotating refresh tokens,
/// reuse detection, logout and step-up (ADR-0014).
/// </summary>
public sealed class AuthService(
    IdentityDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ITenantDirectory tenants,
    PasswordHasher hasher,
    JwtIssuer jwt,
    TotpProvider totp,
    SecretProtector protector,
    WebAuthnService webAuthn,
    IAuditSink audit,
    IClock clock)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshTokenLifetimeCap = TimeSpan.FromDays(90);

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = Emails.Normalize(request.Email);
        var user = await db.Users
            .Include(static u => u.MfaMethods)
            .Include(static u => u.Memberships)
            .SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        var now = clock.UtcNow;
        if (user is null || user.PasswordHash is null)
        {
            // Burn the same time as a real verification so timing does not reveal account existence.
            hasher.Verify(request.Password, hasher.Hash("timing-equaliser-password"));
            return InvalidCredentials();
        }

        if (!user.IsActive)
        {
            return Error.Forbidden("auth.account_disabled", "This account is disabled.");
        }

        if (user.IsLocked(now))
        {
            unitOfWork.Current.CommitOnFailure = true;
            await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.LoginFailed, Reason: "locked"), cancellationToken);
            return Error.Locked("auth.locked", "Too many failed attempts. Try again later.").WithWhy(("lockedUntil", user.LockedUntil));
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            var threshold = TenantSecurityPolicy.Default.LockoutThreshold;
            if (user.FailedLoginCount >= threshold)
            {
                user.LockedUntil = now.AddMinutes(TenantSecurityPolicy.Default.LockoutMinutes);
                user.FailedLoginCount = 0;
            }

            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.LoginFailed, Reason: "bad_password"), cancellationToken);
            unitOfWork.Current.CommitOnFailure = true; // the counter and the audit event must survive the 401
            return InvalidCredentials();
        }

        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = hasher.Hash(request.Password);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        return await ContinueAfterPasswordAsync(user, request.TenantSlug, client, "pwd", cancellationToken);
    }

    public async Task<Result<LoginResponse>> SelectTenantAsync(SelectTenantRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claims = jwt.Validate(request.ChallengeToken, "tenant_select");
        if (claims is null || !Guid.TryParse(claims.FindFirst(QuickerClaims.User)?.Value, out var userId))
        {
            return Error.Validation("auth.challenge_invalid", "The challenge has expired; sign in again.");
        }

        var user = await LoadUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return InvalidCredentials();
        }

        return await ContinueAfterPasswordAsync(user, request.TenantSlug, client, "pwd", cancellationToken);
    }

    private async Task<Result<LoginResponse>> ContinueAfterPasswordAsync(User user, string? tenantSlug, ClientInfo client, string amr, CancellationToken cancellationToken)
    {
        var active = user.Memberships.Where(static m => m.IsActive).ToList();
        TenantMembership? membership = null;
        TenantInfo? tenant = null;

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            tenant = await tenants.FindBySlugAsync(tenantSlug, cancellationToken);
            membership = tenant is null ? null : active.SingleOrDefault(m => m.TenantId == tenant.Id.Value);
            if (membership is null)
            {
                return Error.Forbidden("auth.no_membership", "You are not a member of that workspace.");
            }
        }
        else if (active.Count == 1)
        {
            membership = active[0];
            tenant = await tenants.FindByIdAsync(new TenantId(membership.TenantId), cancellationToken);
        }
        else if (active.Count == 0)
        {
            return Error.Forbidden("auth.no_membership", "Your account is not a member of any workspace.");
        }
        else
        {
            var summaries = new List<TenantSummary>();
            foreach (var m in active)
            {
                var t = await tenants.FindByIdAsync(new TenantId(m.TenantId), cancellationToken);
                if (t is { Status: "active" })
                {
                    summaries.Add(new TenantSummary(t.Id.Value, t.Slug, t.Name, t.DefaultLanguage));
                }
            }

            return new LoginResponse("select_tenant", ChallengeToken: jwt.IssueChallengeToken(user.Id, null, "tenant_select", ChallengeLifetime), Tenants: summaries);
        }

        if (tenant is null || tenant.Status != "active")
        {
            return Error.Forbidden("auth.tenant_unavailable", "This workspace is not available.");
        }

        if (!tenant.Policy.AllowPasswordLogin && amr == "pwd")
        {
            return Error.Forbidden("auth.password_login_disabled", "Sign in with your organisation's identity provider.");
        }

        if (user.HasMfa)
        {
            var methods = user.MfaMethods.Where(static m => m.VerifiedAt is not null && m.Kind is "totp" or "webauthn").Select(static m => m.Kind).Distinct(StringComparer.Ordinal).ToList();
            return new LoginResponse("mfa_required", ChallengeToken: jwt.IssueChallengeToken(user.Id, tenant.Id.Value, "mfa", ChallengeLifetime), MfaMethods: methods);
        }

        if (tenant.Policy.MfaRequired)
        {
            return new LoginResponse("mfa_enrollment_required", ChallengeToken: jwt.IssueChallengeToken(user.Id, tenant.Id.Value, "mfa_enroll", TimeSpan.FromMinutes(15)));
        }

        var tokens = await IssueSessionAsync(user, membership!, tenant, client, amr, clock.UtcNow, cancellationToken);
        return new LoginResponse("ok", tokens);
    }

    public async Task<Result<LoginResponse>> VerifyMfaAsync(MfaVerifyRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claims = jwt.Validate(request.ChallengeToken, "mfa");
        if (claims is null || !Guid.TryParse(claims.FindFirst(QuickerClaims.User)?.Value, out var userId) || !Guid.TryParse(claims.FindFirst(QuickerClaims.Tenant)?.Value, out var tenantId))
        {
            return Error.Validation("auth.challenge_invalid", "The challenge has expired; sign in again.");
        }

        var user = await LoadUserAsync(userId, cancellationToken);
        var tenant = await tenants.FindByIdAsync(new TenantId(tenantId), cancellationToken);
        var membership = user?.Memberships.SingleOrDefault(m => m.TenantId == tenantId && m.IsActive);
        if (user is null || tenant is null || membership is null)
        {
            return InvalidCredentials();
        }

        var now = clock.UtcNow;
        string amr;
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            var method = await VerifyTotpAsync(user, request.Code, cancellationToken);
            if (method is null)
            {
                unitOfWork.Current.CommitOnFailure = true;
                await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.LoginFailed, Reason: "bad_totp"), cancellationToken);
                return Error.Unauthorized("auth.mfa_invalid", "The code is not valid.");
            }

            amr = "pwd mfa otp";
        }
        else if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
        {
            var hash = RecoveryCodes.Hash(request.RecoveryCode);
            var code = user.MfaMethods.FirstOrDefault(m => m.Kind == "recovery" && m.ConsumedAt is null && m.SecretEnc is not null && m.SecretEnc.AsSpan().SequenceEqual(hash));
            if (code is null)
            {
                unitOfWork.Current.CommitOnFailure = true;
                await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.LoginFailed, Reason: "bad_recovery_code"), cancellationToken);
                return Error.Unauthorized("auth.mfa_invalid", "The recovery code is not valid.");
            }

            code.ConsumedAt = now;
            amr = "pwd mfa recovery";
        }
        else if (request.WebAuthnOptionsId is { } optionsId && request.WebAuthnResponse is { } assertion)
        {
            var verified = await webAuthn.VerifyAssertionAsync(user, optionsId, assertion, cancellationToken);
            if (verified.IsFailure)
            {
                unitOfWork.Current.CommitOnFailure = true;
                await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.LoginFailed, Reason: "bad_webauthn"), cancellationToken);
                return verified.Error!;
            }

            amr = "pwd mfa hwk";
        }
        else
        {
            return Error.Validation("auth.mfa_proof_missing", "Provide a code, a recovery code or a WebAuthn assertion.");
        }

        var tokens = await IssueSessionAsync(user, membership, tenant, client, amr, now, cancellationToken);
        return new LoginResponse("ok", tokens);
    }

    public async Task<Result<WebAuthnAssertionOptionsResponse>> WebAuthnLoginOptionsAsync(string challengeToken, CancellationToken cancellationToken)
    {
        var claims = jwt.Validate(challengeToken, "mfa");
        if (claims is null || !Guid.TryParse(claims.FindFirst(QuickerClaims.User)?.Value, out var userId))
        {
            return Error.Validation("auth.challenge_invalid", "The challenge has expired; sign in again.");
        }

        var user = await LoadUserAsync(userId, cancellationToken);
        return user is null ? Error.Validation("auth.challenge_invalid", "The challenge has expired; sign in again.") : await webAuthn.AssertionOptionsAsync(user, cancellationToken);
    }

    public async Task<Result<TokenResponse>> RefreshAsync(RefreshRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = Tokens.Hash(request.RefreshToken);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.RefreshTokenHash == hash, cancellationToken);
        var now = clock.UtcNow;
        if (session is null)
        {
            return Error.Forbidden("auth.session_invalid", "The session is not valid.");
        }

        if (session.RevokedAt is not null)
        {
            // Reuse of a rotated token: someone replayed an old token. Revoke the whole family and keep that revocation.
            unitOfWork.Current.CommitOnFailure = true;
            await db.Sessions.Where(s => s.FamilyId == session.FamilyId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "reuse_detected"), cancellationToken);
            await audit.RecordAsync(new AuditEntry("session", session.Id, session.FamilyId.ToString(), "session_family_revoked", Reason: "refresh_token_reuse"), cancellationToken);
            return Error.Forbidden("auth.session_revoked", "The session was revoked because a refresh token was reused.");
        }

        if (session.ExpiresAt <= now)
        {
            return Error.Forbidden("auth.session_expired", "The session has expired; sign in again.");
        }

        var user = await LoadUserAsync(session.UserId, cancellationToken);
        var membership = user?.Memberships.SingleOrDefault(m => m.Id == session.MembershipId && m.IsActive);
        var tenant = membership is null ? null : await tenants.FindByIdAsync(new TenantId(membership.TenantId), cancellationToken);
        if (user is null || !user.IsActive || membership is null || tenant is null || tenant.Status != "active")
        {
            session.RevokedAt = now;
            session.RevokedReason = "principal_invalid";
            await db.SaveChangesAsync(cancellationToken);
            unitOfWork.Current.CommitOnFailure = true;
            return Error.Forbidden("auth.session_invalid", "The session is not valid.");
        }

        session.RevokedAt = now;
        session.RevokedReason = "rotated";
        var replacement = NewSession(user, membership, tenant, client, session.Amr, session.AuthTime, session.FamilyId, now, out var refreshToken);
        db.Sessions.Add(replacement);
        await db.SaveChangesAsync(cancellationToken);
        return BuildTokens(user, membership, tenant, replacement, refreshToken);
    }

    public async Task LogoutAsync(Guid? sessionId, Guid userId, LogoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        if (request.AllSessions)
        {
            await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "logout_all"), cancellationToken);
        }
        else if (sessionId is { } sid)
        {
            await db.Sessions.Where(s => s.Id == sid && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "logout"), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var hash = Tokens.Hash(request.RefreshToken);
            await db.Sessions.Where(s => s.RefreshTokenHash == hash && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "logout"), cancellationToken);
        }

        await audit.RecordAsync(new AuditEntry("user", userId, userId.ToString(), AuditActions.Logout), cancellationToken);
    }

    /// <summary>Re-proves identity and refreshes the session's auth_time; returns a new access token.</summary>
    public async Task<Result<TokenResponse>> StepUpAsync(Guid sessionId, Guid userId, StepUpRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await LoadUserAsync(userId, cancellationToken);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (user is null || session is null || !session.IsUsable(clock.UtcNow))
        {
            return Error.Forbidden("auth.session_invalid", "The session is not valid.");
        }

        var proved = false;
        if (!string.IsNullOrEmpty(request.Code))
        {
            proved = await VerifyTotpAsync(user, request.Code, cancellationToken) is not null;
        }
        else if (!string.IsNullOrEmpty(request.Password) && user.PasswordHash is not null)
        {
            proved = hasher.Verify(request.Password, user.PasswordHash) && !user.HasMfa;
        }

        if (!proved)
        {
            unitOfWork.Current.CommitOnFailure = true;
            await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, "step_up_failed"), cancellationToken);
            return Error.Validation("auth.step_up_failed", user.HasMfa ? "Enter a valid authenticator code." : "The password is not valid.");
        }

        var membership = user.Memberships.Single(m => m.Id == session.MembershipId);
        var tenant = (await tenants.FindByIdAsync(new TenantId(membership.TenantId), cancellationToken))!;
        session.AuthTime = clock.UtcNow;
        session.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, "step_up"), cancellationToken);

        var access = jwt.IssueAccessToken(user.Id, tenant.Id.Value, membership.Id, session.Id, tenant.PermissionsEpoch, session.AuthTime, session.Amr, user.Locale, user.IsPlatformOperator, TimeSpan.FromMinutes(tenant.Policy.AccessTokenMinutes));
        return new TokenResponse(access, string.Empty, tenant.Policy.AccessTokenMinutes * 60, Summary(tenant), Summary(user), membership.Id);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now).OrderByDescending(static s => s.LastUsedAt).ToListAsync(cancellationToken);
        return sessions.Select(s => new SessionSummary(s.Id, s.CreatedAt, s.LastUsedAt, s.ExpiresAt, s.Ip?.ToString(), s.UserAgent, s.Amr, s.Id == currentSessionId)).ToList();
    }

    public async Task<Result> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
        if (session is null)
        {
            return Error.NotFound("session", sessionId);
        }

        session.RevokedAt = clock.UtcNow;
        session.RevokedReason = "revoked_by_user";
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Issues a session for an already-authenticated user (OIDC callback, invitation acceptance).</summary>
    public async Task<TokenResponse> IssueSessionAsync(User user, TenantMembership membership, TenantInfo tenant, ClientInfo client, string amr, DateTimeOffset authTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(tenant);
        var session = NewSession(user, membership, tenant, client, amr, authTime, Guid.CreateVersion7(), authTime, out var refreshToken);
        db.Sessions.Add(session);
        user.LastLoginAt = authTime;
        await db.SaveChangesAsync(cancellationToken);

        // Switch the unit of work to the tenant so the login audit event lands in the tenant's chain.
        await unitOfWork.Current.SwitchTenantAsync(new TenantId(tenant.Id.Value), new UserId(user.Id), new MembershipId(membership.Id), cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.Login, Details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["amr"] = amr, ["ip"] = client.Ip?.ToString() }), cancellationToken);
        return BuildTokens(user, membership, tenant, session, refreshToken);
    }

    private static Session NewSession(User user, TenantMembership membership, TenantInfo tenant, ClientInfo client, string amr, DateTimeOffset authTime, Guid familyId, DateTimeOffset now, out string refreshToken)
    {
        refreshToken = Tokens.NewUrlSafe(32);
        var lifetime = TimeSpan.FromHours(tenant.Policy.SessionLifetimeHours);
        if (lifetime > RefreshTokenLifetimeCap)
        {
            lifetime = RefreshTokenLifetimeCap;
        }

        return new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            MembershipId = membership.Id,
            FamilyId = familyId,
            RefreshTokenHash = Tokens.Hash(refreshToken),
            Amr = amr,
            AuthTime = authTime,
            Ip = client.Ip,
            UserAgent = client.UserAgent is { Length: > 512 } ua ? ua[..512] : client.UserAgent,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.Add(lifetime),
        };
    }

    private TokenResponse BuildTokens(User user, TenantMembership membership, TenantInfo tenant, Session session, string refreshToken)
    {
        var minutes = tenant.Policy.AccessTokenMinutes;
        var access = jwt.IssueAccessToken(user.Id, tenant.Id.Value, membership.Id, session.Id, tenant.PermissionsEpoch, session.AuthTime, session.Amr, user.Locale, user.IsPlatformOperator, TimeSpan.FromMinutes(minutes));
        return new TokenResponse(access, refreshToken, minutes * 60, Summary(tenant), Summary(user), membership.Id);
    }

    private async Task<MfaMethod?> VerifyTotpAsync(User user, string code, CancellationToken cancellationToken)
    {
        foreach (var method in user.MfaMethods.Where(static m => m.Kind == "totp" && m.VerifiedAt is not null && m.SecretEnc is not null))
        {
            var secret = protector.Unprotect(method.SecretEnc!);
            if (totp.Verify(secret, code, method.SignCount, out var step))
            {
                method.SignCount = step; // last accepted time step, for replay protection
                method.LastUsedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return method;
            }
        }

        return null;
    }

    public Task<User?> LoadUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.Include(static u => u.MfaMethods).Include(static u => u.Memberships).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public static TenantSummary Summary(TenantInfo tenant) => new(tenant.Id.Value, tenant.Slug, tenant.Name, tenant.DefaultLanguage);

    public static UserSummary Summary(User user) => new(user.Id, user.Email, user.DisplayName, user.Locale, user.TimeZone, user.DigitStyle, user.HasMfa, user.IsPlatformOperator);

    private static Error InvalidCredentials() => Error.Unauthorized("auth.invalid_credentials", "The email or password is not correct.");
}

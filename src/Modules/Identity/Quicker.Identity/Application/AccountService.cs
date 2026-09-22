using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Identity.Application;

/// <summary>
/// Sign-up (tenant + owner), invitations, password reset, profile, password change and MFA enrolment (TOTP,
/// recovery codes; WebAuthn delegates to <see cref="WebAuthnService"/>).
/// </summary>
public sealed class AccountService(
    IdentityDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ITenantDirectory tenants,
    ITenantProvisioner provisioner,
    RoleService roles,
    PasswordHasher hasher,
    PasswordPolicy passwordPolicy,
    TotpProvider totp,
    SecretProtector protector,
    JwtIssuer jwt,
    AuthService auth,
    IEmailSender email,
    AuthOptions options,
    IAuditSink audit,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<TokenResponse>> SignupAsync(SignupRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerEmail = Emails.Normalize(request.OwnerEmail);
        if (await db.Users.AnyAsync(u => u.Email == ownerEmail, cancellationToken))
        {
            return Error.Conflict("user.email_taken", "An account with this email already exists. Sign in and create the workspace from your account.");
        }

        var passwordCheck = await passwordPolicy.ValidateAsync(request.Password, ownerEmail, TenantSecurityPolicy.Default, cancellationToken);
        if (passwordCheck.IsFailure)
        {
            return passwordCheck.Error!;
        }

        var provisioned = await provisioner.ProvisionAsync(new ProvisionTenantRequest(request.Slug, request.TenantName, request.Language), cancellationToken);
        if (provisioned.IsFailure)
        {
            return provisioned.Error!;
        }

        var tenant = provisioned.Value;
        var now = clock.UtcNow;
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = ownerEmail,
            DisplayName = request.OwnerName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            PasswordUpdatedAt = now,
            Locale = request.Language,
            EmailVerifiedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id.Value,
            UserId = user.Id,
            Status = "active",
            IsOwner = true,
            AcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            User = user,
        };
        db.Users.Add(user);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(cancellationToken);

        // From here on we act inside the new tenant: default roles, SoD rules, owner assignment, audit.
        await unitOfWork.Current.SwitchTenantAsync(tenant.Id, new UserId(user.Id), new MembershipId(membership.Id), user.Email, cancellationToken);
        var ownerRole = await roles.SeedDefaultsAsync(cancellationToken);
        var assigned = await roles.AssignAsync(membership.Id, new AssignRoleRequest(ownerRole.Id, AcknowledgeWarnings: true), user.Id, cancellationToken);
        if (assigned.IsFailure)
        {
            throw new InvalidOperationException($"Owner role assignment failed during sign-up: {assigned.Error!.Code}");
        }

        await provisioner.ActivateAsync(tenant.Id, cancellationToken);
        await audit.RecordAsync(new AuditEntry("tenant", tenant.Id.Value, tenant.Slug, AuditActions.Created, After: new { tenant.Slug, tenant.Name }), cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.Created, After: new { user.Email, user.DisplayName, Role = "owner" }), cancellationToken);

        var activeTenant = (await tenants.FindByIdAsync(tenant.Id, cancellationToken))!;
        return await auth.IssueSessionAsync(user, membership, activeTenant, client, "pwd", now, cancellationToken);
    }

    public async Task<Result<MemberSummary>> InviteAsync(Guid tenantId, Guid invitedBy, InviteUserRequest request, string tenantName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = Emails.Normalize(request.Email);
        if (!address.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("user.email_invalid", "Enter a valid email address.");
        }

        var now = clock.UtcNow;
        var user = await db.Users.Include(static u => u.Memberships).SingleOrDefaultAsync(u => u.Email == address, cancellationToken);
        if (user is null)
        {
            user = new User { Id = Guid.CreateVersion7(), Email = address, DisplayName = request.DisplayName?.Trim() ?? address.Split('@')[0], Locale = request.Language, CreatedAt = now, UpdatedAt = now };
            db.Users.Add(user);
        }

        var membership = user.Memberships.SingleOrDefault(m => m.TenantId == tenantId);
        if (membership is { Status: "active" })
        {
            return Error.Conflict("user.already_member", "This person is already a member.");
        }

        if (membership is null)
        {
            membership = new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = user.Id, Status = "invited", InvitedBy = invitedBy, InvitedAt = now, CreatedAt = now, UpdatedAt = now, User = user };
            db.Memberships.Add(membership);
        }
        else
        {
            membership.Status = "invited";
            membership.InvitedBy = invitedBy;
            membership.InvitedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var roleId in request.RoleIds.Distinct())
        {
            var assigned = await roles.AssignAsync(membership.Id, new AssignRoleRequest(roleId, AcknowledgeWarnings: true), invitedBy, cancellationToken);
            if (assigned.IsFailure)
            {
                return assigned.Error!;
            }
        }

        var raw = Tokens.NewUrlSafe(32);
        db.OneTimeTokens.Add(new OneTimeToken
        {
            Id = Guid.CreateVersion7(),
            Kind = "invitation",
            UserId = user.Id,
            TenantId = tenantId,
            TokenHash = Tokens.Hash(raw),
            Payload = JsonSerializer.Serialize(new { membershipId = membership.Id }, Json),
            ExpiresAt = now.AddDays(7),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        var link = $"{options.PublicOrigin}/invitation?token={raw}";
        await email.SendAsync(new EmailMessage(user.Email, $"You are invited to {tenantName} on Quicker",
            $"Open this link to accept the invitation (valid for 7 days): {link}"), cancellationToken);
        await audit.RecordAsync(new AuditEntry("membership", membership.Id, user.Email, "invited", After: new { user.Email, request.RoleIds }), cancellationToken);

        return await roles.MemberAsync(membership.Id, cancellationToken) ?? throw new InvalidOperationException("Member disappeared.");
    }

    public async Task<Result<TokenResponse>> AcceptInvitationAsync(AcceptInvitationRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await db.OneTimeTokens.SingleOrDefaultAsync(t => t.TokenHash == Tokens.Hash(request.Token) && t.Kind == "invitation", cancellationToken);
        var now = clock.UtcNow;
        if (token is null || !token.IsUsable(now) || token.UserId is null || token.TenantId is null)
        {
            return Error.Validation("invitation.invalid", "This invitation is not valid or has expired.");
        }

        var user = await auth.LoadUserAsync(token.UserId.Value, cancellationToken);
        var membership = user?.Memberships.SingleOrDefault(m => m.TenantId == token.TenantId.Value);
        var tenant = await tenants.FindByIdAsync(new TenantId(token.TenantId.Value), cancellationToken);
        if (user is null || membership is null || tenant is null)
        {
            return Error.Validation("invitation.invalid", "This invitation is not valid or has expired.");
        }

        if (user.PasswordHash is null)
        {
            var check = await passwordPolicy.ValidateAsync(request.Password, user.Email, tenant.Policy, cancellationToken);
            if (check.IsFailure)
            {
                return check.Error!;
            }

            user.PasswordHash = hasher.Hash(request.Password);
            user.PasswordUpdatedAt = now;
        }
        else if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            return Error.Validation("auth.invalid_credentials", "Enter your existing password to accept the invitation.");
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            user.DisplayName = request.DisplayName.Trim();
        }

        user.EmailVerifiedAt ??= now;
        membership.Status = "active";
        membership.AcceptedAt = now;
        token.ConsumedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return await auth.IssueSessionAsync(user, membership, tenant, client, "pwd", now, cancellationToken);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var address = Emails.Normalize(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == address, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return; // no account enumeration
        }

        var raw = Tokens.NewUrlSafe(32);
        var now = clock.UtcNow;
        db.OneTimeTokens.Add(new OneTimeToken { Id = Guid.CreateVersion7(), Kind = "password_reset", UserId = user.Id, TokenHash = Tokens.Hash(raw), ExpiresAt = now.AddHours(1), CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await email.SendAsync(new EmailMessage(user.Email, "Reset your Quicker password", $"Use this link within one hour: {options.PublicOrigin}/reset-password?token={raw}"), cancellationToken);
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await db.OneTimeTokens.SingleOrDefaultAsync(t => t.TokenHash == Tokens.Hash(request.Token) && t.Kind == "password_reset", cancellationToken);
        var now = clock.UtcNow;
        if (token is null || !token.IsUsable(now) || token.UserId is null)
        {
            return Error.Validation("password_reset.invalid", "This reset link is not valid or has expired.");
        }

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId.Value, cancellationToken);
        var check = await passwordPolicy.ValidateAsync(request.Password, user.Email, TenantSecurityPolicy.Default, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        user.PasswordHash = hasher.Hash(request.Password);
        user.PasswordUpdatedAt = now;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        token.ConsumedAt = now;
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "password_reset"), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.PasswordChanged, Reason: "reset"), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<UserSummary>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await db.Users.Include(static u => u.MfaMethods).SingleAsync(u => u.Id == userId, cancellationToken);
        var before = new { user.DisplayName, user.Locale, user.TimeZone, user.DigitStyle };
        if (request.DisplayName is { } name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Error.Validation("user.name_required", "Display name is required.");
            }

            user.DisplayName = name.Trim();
        }

        if (request.Locale is { } locale)
        {
            if (locale is not ("en" or "ar"))
            {
                return Error.Validation("user.locale_unsupported", "Locale must be 'en' or 'ar'.");
            }

            user.Locale = locale;
        }

        if (request.TimeZone is { } zone)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            {
                return Error.Validation("user.timezone_unknown", "Unknown time zone.");
            }

            user.TimeZone = zone;
        }

        if (request.DigitStyle is { } digits)
        {
            if (digits is not ("western" or "eastern_arabic"))
            {
                return Error.Validation("user.digit_style_unsupported", "Digit style must be 'western' or 'eastern_arabic'.");
            }

            user.DigitStyle = digits;
        }

        await db.SaveChangesAsync(cancellationToken); // captured as "updated" with the field diff
        return AuthService.Summary(user);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, Guid currentSessionId, ChangePasswordRequest request, TenantSecurityPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (user.PasswordHash is null || !hasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Error.Validation("auth.invalid_credentials", "The current password is not correct.");
        }

        var check = await passwordPolicy.ValidateAsync(request.NewPassword, user.Email, policy, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var now = clock.UtcNow;
        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.PasswordUpdatedAt = now;
        await db.Sessions.Where(s => s.UserId == userId && s.Id != currentSessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "password_changed"), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.PasswordChanged), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ MFA (TOTP and recovery codes)

    public async Task<TotpEnrollResponse> StartTotpAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(static u => u.MfaMethods).SingleAsync(u => u.Id == userId, cancellationToken);
        // Replace any unverified pending enrolment.
        db.MfaMethods.RemoveRange(user.MfaMethods.Where(static m => m.Kind == "totp" && m.VerifiedAt is null));
        var secret = totp.NewSecret();
        var method = new MfaMethod { Id = Guid.CreateVersion7(), UserId = userId, Kind = "totp", Name = "Authenticator app", SecretEnc = protector.Protect(secret), CreatedAt = clock.UtcNow };
        db.MfaMethods.Add(method);
        await db.SaveChangesAsync(cancellationToken);
        return new TotpEnrollResponse(method.Id, TotpProvider.Base32(secret), totp.ProvisioningUri(secret, user.Email));
    }

    public async Task<Result<RecoveryCodesResponse>> ConfirmTotpAsync(Guid userId, TotpConfirmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await db.Users.Include(static u => u.MfaMethods).SingleAsync(u => u.Id == userId, cancellationToken);
        var method = user.MfaMethods.SingleOrDefault(m => m.Id == request.MethodId && m.Kind == "totp" && m.VerifiedAt is null);
        if (method?.SecretEnc is null)
        {
            return Error.Validation("mfa.enrollment_not_found", "Start the enrolment again.");
        }

        if (!totp.Verify(protector.Unprotect(method.SecretEnc), request.Code, 0, out var step))
        {
            return Error.Validation("mfa.code_invalid", "The code is not valid; check the time on your device.");
        }

        var now = clock.UtcNow;
        method.VerifiedAt = now;
        method.SignCount = step;
        var codes = await ReplaceRecoveryCodesAsync(user, now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.MfaEnrolled, After: new { Kind = "totp" }), cancellationToken);
        return new RecoveryCodesResponse(codes);
    }

    public async Task<RecoveryCodesResponse> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(static u => u.MfaMethods).SingleAsync(u => u.Id == userId, cancellationToken);
        var codes = await ReplaceRecoveryCodesAsync(user, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, "recovery_codes_regenerated"), cancellationToken);
        return new RecoveryCodesResponse(codes);
    }

    public async Task<Result> RemoveMfaMethodAsync(Guid userId, Guid methodId, bool mfaRequiredByPolicy, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(static u => u.MfaMethods).SingleAsync(u => u.Id == userId, cancellationToken);
        var method = user.MfaMethods.SingleOrDefault(m => m.Id == methodId && m.Kind is "totp" or "webauthn");
        if (method is null)
        {
            return Error.NotFound("mfa_method", methodId);
        }

        var remaining = user.MfaMethods.Count(m => m.Id != methodId && m.Kind is "totp" or "webauthn" && m.VerifiedAt is not null);
        if (remaining == 0 && mfaRequiredByPolicy)
        {
            return Error.Forbidden("mfa.required_by_policy", "Your workspace requires MFA; add another method before removing this one.");
        }

        db.MfaMethods.Remove(method);
        if (remaining == 0)
        {
            db.MfaMethods.RemoveRange(user.MfaMethods.Where(static m => m.Kind == "recovery"));
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("user", user.Id, user.Email, AuditActions.MfaRemoved, Before: new { method.Kind, method.Name }), cancellationToken);
        return Result.Success();
    }

    public async Task<IReadOnlyList<MfaMethodSummary>> ListMfaMethodsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var methods = await db.MfaMethods.Where(m => m.UserId == userId && m.Kind != "recovery").OrderBy(static m => m.CreatedAt).ToListAsync(cancellationToken);
        return methods.Select(static m => new MfaMethodSummary(m.Id, m.Kind, m.Name, m.VerifiedAt, m.LastUsedAt)).ToList();
    }

    /// <summary>Completes enrolment for a user who must add MFA before their first session (policy-required MFA).</summary>
    public Result<Guid> UserFromEnrollmentChallenge(string challengeToken)
    {
        var claims = jwt.Validate(challengeToken, "mfa_enroll");
        return claims is not null && Guid.TryParse(claims.FindFirst(QuickerClaims.User)?.Value, out var userId)
            ? userId
            : Error.Validation("auth.challenge_invalid", "The challenge has expired; sign in again.");
    }

    private Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(User user, DateTimeOffset now)
    {
        db.MfaMethods.RemoveRange(user.MfaMethods.Where(static m => m.Kind == "recovery"));
        var codes = RecoveryCodes.Generate();
        foreach (var code in codes)
        {
            db.MfaMethods.Add(new MfaMethod { Id = Guid.CreateVersion7(), UserId = user.Id, Kind = "recovery", Name = "Recovery code", SecretEnc = RecoveryCodes.Hash(code), VerifiedAt = now, CreatedAt = now });
        }

        return Task.FromResult(codes);
    }
}

namespace Quicker.Identity.Domain;

/// <summary>control.users: one identity per person across tenants.</summary>
public sealed class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public DateTimeOffset? PasswordUpdatedAt { get; set; }

    public string Locale { get; set; } = "en";

    public string TimeZone { get; set; } = "Asia/Baghdad";

    public string DigitStyle { get; set; } = "western";

    public string Status { get; set; } = "active";

    public bool IsPlatformOperator { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<MfaMethod> MfaMethods { get; set; } = [];

    public List<TenantMembership> Memberships { get; set; } = [];

    public bool IsActive => Status == "active";

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public bool HasMfa => MfaMethods.Any(static m => m.Kind is "totp" or "webauthn" && m.VerifiedAt is not null);
}

/// <summary>control.mfa_methods: TOTP secrets, WebAuthn credentials and single-use recovery codes.</summary>
public sealed class MfaMethod
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Kind { get; set; } = "totp";

    public string Name { get; set; } = string.Empty;

    public byte[]? SecretEnc { get; set; }

    public byte[]? CredentialId { get; set; }

    public byte[]? PublicKey { get; set; }

    public long SignCount { get; set; }

    public Guid? Aaguid { get; set; }

    public string[]? Transports { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>control.tenant_memberships: a user's seat in a tenant.</summary>
public sealed class TenantMembership
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public string Status { get; set; } = "invited";

    public bool IsOwner { get; set; }

    public Guid? InvitedBy { get; set; }

    public DateTimeOffset? InvitedAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;

    public bool IsActive => Status == "active";
}

/// <summary>control.sessions: a refresh-token session in a rotation family.</summary>
public sealed class Session
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? MembershipId { get; set; }

    public Guid FamilyId { get; set; }

    public byte[] RefreshTokenHash { get; set; } = [];

    public string Amr { get; set; } = "pwd";

    public DateTimeOffset AuthTime { get; set; }

    public System.Net.IPAddress? Ip { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>control.one_time_tokens: invitations, resets, MFA challenges, OIDC transactions, step-up.</summary>
public sealed class OneTimeToken
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public Guid? TenantId { get; set; }

    public byte[] TokenHash { get; set; } = [];

    public string Payload { get; set; } = "{}";

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;
}

/// <summary>control.sso_connections: an OIDC identity provider for a tenant.</summary>
public sealed class SsoConnection
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Kind { get; set; } = "oidc";

    public string DisplayName { get; set; } = string.Empty;

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public byte[]? ClientSecretEnc { get; set; }

    public string Scopes { get; set; } = "openid profile email";

    public string[] EmailDomains { get; set; } = [];

    public bool JitProvisioning { get; set; } = true;

    public string? GroupClaim { get; set; }

    public string GroupRoleMap { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

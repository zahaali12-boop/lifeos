namespace Quicker.Identity.Application;

// Request and response shapes of the identity API. Records keep them immutable and OpenAPI-friendly.

public sealed record SignupRequest(string TenantName, string Slug, string OwnerEmail, string OwnerName, string Password, string Language = "en");

public sealed record LoginRequest(string Email, string Password, string? TenantSlug = null);

public sealed record SelectTenantRequest(string ChallengeToken, string TenantSlug);

public sealed record MfaVerifyRequest(string ChallengeToken, string? Code = null, string? RecoveryCode = null, Guid? WebAuthnOptionsId = null, System.Text.Json.JsonElement? WebAuthnResponse = null);

public sealed record MfaWebAuthnOptionsRequest(string ChallengeToken);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken = null, bool AllSessions = false);

public sealed record StepUpRequest(string? Password = null, string? Code = null);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string Password);

public sealed record AcceptInvitationRequest(string Token, string Password, string? DisplayName = null);

public sealed record SsoExchangeRequest(string Code);

public sealed record TenantSummary(Guid Id, string Slug, string Name, string DefaultLanguage);

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Locale, string TimeZone, string DigitStyle, bool HasMfa, bool IsPlatformOperator);

public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds, TenantSummary Tenant, UserSummary User, Guid MembershipId);

/// <summary>Login outcome: a token set, a tenant choice, an MFA challenge or an MFA enrollment requirement.</summary>
public sealed record LoginResponse(
    string Status,
    TokenResponse? Tokens = null,
    string? ChallengeToken = null,
    IReadOnlyList<TenantSummary>? Tenants = null,
    IReadOnlyList<string>? MfaMethods = null);

public sealed record MeResponse(
    UserSummary User,
    TenantSummary Tenant,
    Guid MembershipId,
    bool IsOwner,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Contracts.Grant> Grants,
    IReadOnlyList<Contracts.FieldRule> FieldRules,
    IReadOnlyList<Contracts.DocumentTypeRule> DocumentTypeRules,
    DateTimeOffset AuthTime,
    string AuthMethods);

public sealed record UpdateProfileRequest(string? DisplayName = null, string? Locale = null, string? TimeZone = null, string? DigitStyle = null);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record TotpEnrollResponse(Guid MethodId, string Secret, string ProvisioningUri);

public sealed record TotpConfirmRequest(Guid MethodId, string Code);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);

public sealed record MfaMethodSummary(Guid Id, string Kind, string Name, DateTimeOffset? VerifiedAt, DateTimeOffset? LastUsedAt);

public sealed record WebAuthnRegisterOptionsResponse(Guid OptionsId, System.Text.Json.JsonElement Options);

public sealed record WebAuthnRegisterVerifyRequest(Guid OptionsId, string Name, System.Text.Json.JsonElement Response);

public sealed record WebAuthnAssertionOptionsResponse(Guid OptionsId, System.Text.Json.JsonElement Options);

public sealed record SessionSummary(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset ExpiresAt, string? Ip, string? UserAgent, string Amr, bool IsCurrent);

public sealed record InviteUserRequest(string Email, string? DisplayName, IReadOnlyList<Guid> RoleIds, string Language = "en");

public sealed record MemberSummary(Guid MembershipId, Guid UserId, string Email, string DisplayName, string Status, bool IsOwner, bool HasMfa, DateTimeOffset? LastLoginAt, IReadOnlyList<AssignmentSummary> Assignments);

public sealed record RoleSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Description, bool IsSystem, string? TemplateCode, bool IsActive, IReadOnlyList<string> Grants, IReadOnlyList<Contracts.FieldRule> FieldRules, IReadOnlyList<Contracts.DocumentTypeRule> DocumentTypeRules);

public sealed record SaveRoleRequest(string Code, IReadOnlyDictionary<string, string> Name, string Description, IReadOnlyList<string> Grants, IReadOnlyList<Contracts.FieldRule>? FieldRules = null, IReadOnlyList<Contracts.DocumentTypeRule>? DocumentTypeRules = null, bool IsActive = true);

public sealed record ScopeInput(string ScopeType, Guid ScopeId);

public sealed record AssignRoleRequest(Guid RoleId, IReadOnlyList<ScopeInput>? Scopes = null, DateOnly? ValidFrom = null, DateOnly? ValidTo = null, bool AcknowledgeWarnings = false);

public sealed record AssignmentSummary(Guid Id, Guid RoleId, string RoleCode, IReadOnlyList<ScopeInput> Scopes, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record SodConflict(Guid RuleId, string PermissionA, string PermissionB, string Severity, IReadOnlyDictionary<string, string> Rationale, bool HasException);

public sealed record SodRuleSummary(Guid Id, string PermissionA, string PermissionB, string Severity, IReadOnlyDictionary<string, string> Rationale, bool IsSystem, bool IsActive);

public sealed record SaveSodRuleRequest(string PermissionA, string PermissionB, string Severity, IReadOnlyDictionary<string, string> Rationale, bool IsActive = true);

public sealed record SodExceptionRequest(Guid RuleId, Guid MembershipId, string Reason, DateOnly? ExpiresOn = null);

public sealed record SodReportRow(Guid MembershipId, string Email, string DisplayName, IReadOnlyList<SodConflict> Conflicts, bool IsSuperUser);

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string>? Scopes = null, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? IpAllowlist = null);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Key, string Prefix, DateTimeOffset? ExpiresAt);

public sealed record ApiKeySummary(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

public sealed record SaveSsoConnectionRequest(string Code, string DisplayName, string Authority, string ClientId, string? ClientSecret, string Scopes, IReadOnlyList<string> EmailDomains, bool JitProvisioning, string? GroupClaim, IReadOnlyDictionary<string, Guid>? GroupRoleMap, bool IsActive = true);

public sealed record SsoConnectionSummary(Guid Id, string Code, string DisplayName, string Authority, string ClientId, bool HasClientSecret, string Scopes, IReadOnlyList<string> EmailDomains, bool JitProvisioning, string? GroupClaim, IReadOnlyDictionary<string, Guid> GroupRoleMap, bool IsActive);

public sealed record SsoStartResponse(string RedirectUrl);

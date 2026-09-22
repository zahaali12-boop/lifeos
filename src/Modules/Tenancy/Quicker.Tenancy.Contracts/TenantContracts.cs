using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;

namespace Quicker.Tenancy.Contracts;

/// <summary>Security policy a tenant admin controls (stored in control.tenants.settings).</summary>
public sealed record TenantSecurityPolicy(
    bool MfaRequired = false,
    int SessionLifetimeHours = 24 * 14,
    int AccessTokenMinutes = 10,
    int PasswordMinLength = 12,
    int StepUpWindowMinutes = 5,
    int LockoutThreshold = 10,
    int LockoutMinutes = 15,
    IReadOnlyList<string>? IpAllowlist = null,
    bool AllowPasswordLogin = true)
{
    public static readonly TenantSecurityPolicy Default = new();
}

public sealed record TenantInfo(
    TenantId Id,
    string Slug,
    string Name,
    string Tier,
    string Region,
    string Status,
    string DefaultLanguage,
    long PermissionsEpoch,
    TenantSecurityPolicy Policy);

public interface ITenantDirectory
{
    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindByIdAsync(TenantId id, CancellationToken cancellationToken = default);

    /// <summary>Increments the tenant's permissions epoch so cached principals are recomputed on the next request.</summary>
    Task BumpPermissionsEpochAsync(TenantId id, CancellationToken cancellationToken = default);
}

public sealed record ProvisionTenantRequest(string Slug, string Name, string DefaultLanguage, string Region = "me", string Tier = "shared");

public interface ITenantProvisioner
{
    /// <summary>Creates the tenant row (status provisioning) inside the caller's unit of work; Identity then adds the owner.</summary>
    Task<Result<TenantInfo>> ProvisionAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default);

    Task ActivateAsync(TenantId id, CancellationToken cancellationToken = default);

    Task<Result> UpdatePolicyAsync(TenantId id, TenantSecurityPolicy policy, CancellationToken cancellationToken = default);
}

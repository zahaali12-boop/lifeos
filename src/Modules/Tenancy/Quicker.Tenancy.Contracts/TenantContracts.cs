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

    /// <summary>All tenants in the catalogue (control plane; for platform jobs that visit every tenant).</summary>
    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Increments the tenant's permissions epoch so cached principals are recomputed on the next request.</summary>
    Task BumpPermissionsEpochAsync(TenantId id, CancellationToken cancellationToken = default);
}

/// <summary>A new tenant. <paramref name="Id"/> is given only by seeders that need a stable identifier; sign-up leaves it empty.</summary>
public sealed record ProvisionTenantRequest(string Slug, string Name, string DefaultLanguage, string Region = "me", string Tier = "shared", Guid? Id = null);

/// <summary>
/// Module hook run once for every new tenant, inside the sign-up unit of work after the tenant session is switched,
/// so modules seed their defaults (system dimensions, rate types, units, calendars) without Identity knowing them.
/// </summary>
public interface ITenantSetupStep
{
    Task SetUpAsync(TenantId tenantId, string defaultLanguage, CancellationToken cancellationToken);
}

public interface ITenantProvisioner
{
    /// <summary>Creates the tenant row (status provisioning) inside the caller's unit of work; Identity then adds the owner.</summary>
    Task<Result<TenantInfo>> ProvisionAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default);

    Task ActivateAsync(TenantId id, CancellationToken cancellationToken = default);

    Task<Result> UpdatePolicyAsync(TenantId id, TenantSecurityPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>
/// A workspace's network allow-list: addresses ("203.0.113.7") and ranges in CIDR form ("10.0.0.0/8", "2001:db8::/32").
/// An empty list allows every address; a non-empty one allows only the addresses it covers, and an unknown address none.
/// </summary>
public static class IpAllowlists
{
    public const int MaxEntries = 100;

    public static bool TryParse(string? entry, out System.Net.IPNetwork network)
    {
        network = default;
        var text = entry?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Contains('/', StringComparison.Ordinal))
        {
            return System.Net.IPNetwork.TryParse(text, out network);
        }

        if (!System.Net.IPAddress.TryParse(text, out var address))
        {
            return false;
        }

        network = new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    /// <summary>The entries that are neither an address nor a CIDR range.</summary>
    public static IReadOnlyList<string> Invalid(IEnumerable<string>? entries) => (entries ?? []).Where(static e => !TryParse(e, out _)).ToList();

    public static bool Allows(IReadOnlyList<string>? entries, System.Net.IPAddress? address)
    {
        if (entries is null || entries.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return entries.Any(e => TryParse(e, out var network) && network.Contains(candidate));
    }
}

using System.Collections.Concurrent;

namespace Quicker.Identity.Contracts;

/// <summary>A permission the system understands: a stable key, its module and a human description.</summary>
public sealed record PermissionDefinition(string Key, string Module, string Description, bool IsSensitive = false);

/// <summary>
/// The code-defined permission catalogue (ADR-0014). Each module registers its keys at startup; roles reference keys
/// or wildcards (<c>sales.*</c>, <c>*</c>). Unknown keys are rejected when a role is saved.
/// </summary>
public static class PermissionCatalog
{
    private static readonly ConcurrentDictionary<string, PermissionDefinition> Definitions = new(StringComparer.Ordinal);

    public static void Register(params PermissionDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!IsValidKey(definition.Key))
            {
                throw new ArgumentException($"'{definition.Key}' is not a valid permission key (module.entity.action).", nameof(definitions));
            }

            Definitions[definition.Key] = definition;
        }
    }

    public static IReadOnlyCollection<PermissionDefinition> All => Definitions.Values.OrderBy(static d => d.Key, StringComparer.Ordinal).ToList();

    public static bool Exists(string key) => Definitions.ContainsKey(key);

    public static PermissionDefinition? Find(string key) => Definitions.TryGetValue(key, out var d) ? d : null;

    /// <summary>A grant is valid if it is an exact key or a wildcard whose prefix matches at least one registered key.</summary>
    public static bool IsValidGrant(string grant)
    {
        if (string.IsNullOrWhiteSpace(grant))
        {
            return false;
        }

        if (string.Equals(grant, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (grant.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = grant[..^1];
            return Definitions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        return Definitions.ContainsKey(grant);
    }

    /// <summary>True when <paramref name="grant"/> (exact or wildcard) covers <paramref name="permission"/>.</summary>
    public static bool Covers(string grant, string permission)
    {
        if (string.Equals(grant, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (grant.EndsWith(".*", StringComparison.Ordinal))
        {
            return permission.StartsWith(grant[..^1], StringComparison.Ordinal);
        }

        return string.Equals(grant, permission, StringComparison.Ordinal);
    }

    public static bool IsValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Split('.').Length >= 3 && key.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_');
}

/// <summary>Identity module permissions.</summary>
public static class IdentityPermissions
{
    public const string UserInvite = "identity.user.invite";
    public const string UserManage = "identity.user.manage";
    public const string UserRead = "identity.user.read";
    public const string RoleRead = "identity.role.read";
    public const string RoleManage = "identity.role.manage";
    public const string AssignmentManage = "identity.assignment.manage";
    public const string ApiKeyManage = "identity.apikey.manage";
    public const string SsoManage = "identity.sso.manage";
    public const string SodManage = "identity.sod.manage";
    public const string SodRead = "identity.sod.read";
    public const string AuditRead = "identity.audit.read";
    public const string TenantSettingsManage = "identity.tenant.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(UserInvite, "identity", "Invite users to the tenant"),
        new(UserManage, "identity", "Disable, re-enable and edit members", IsSensitive: true),
        new(UserRead, "identity", "List members"),
        new(RoleRead, "identity", "View roles and their permissions"),
        new(RoleManage, "identity", "Create and edit roles, field rules and document-type rules", IsSensitive: true),
        new(AssignmentManage, "identity", "Assign roles and scopes to members", IsSensitive: true),
        new(ApiKeyManage, "identity", "Create and revoke API keys", IsSensitive: true),
        new(SsoManage, "identity", "Configure single sign-on connections", IsSensitive: true),
        new(SodManage, "identity", "Maintain segregation-of-duties rules and exceptions", IsSensitive: true),
        new(SodRead, "identity", "View the segregation-of-duties report"),
        new(AuditRead, "identity", "Read the audit log"),
        new(TenantSettingsManage, "identity", "Change tenant security policy", IsSensitive: true),
    ];
}

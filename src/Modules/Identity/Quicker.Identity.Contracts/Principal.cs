using Quicker.Kernel.Ids;

namespace Quicker.Identity.Contracts;

/// <summary>Record scopes attached to a principal's grants; an empty set for a scope type means "all".</summary>
public sealed record RecordScopes(
    IReadOnlySet<Guid> CompanyIds,
    IReadOnlySet<Guid> BranchIds,
    IReadOnlySet<Guid> WarehouseIds)
{
    public static readonly RecordScopes All = new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public bool AllowsCompany(Guid companyId) => CompanyIds.Count == 0 || CompanyIds.Contains(companyId);

    public bool AllowsBranch(Guid branchId) => BranchIds.Count == 0 || BranchIds.Contains(branchId);

    public bool AllowsWarehouse(Guid warehouseId) => WarehouseIds.Count == 0 || WarehouseIds.Contains(warehouseId);
}

/// <summary>A permission grant with the scopes it applies to (union of all assignments that grant it).</summary>
public sealed record Grant(string Permission, RecordScopes Scopes);

public sealed record FieldRule(string EntityType, string Field, string Access);

public sealed record DocumentTypeRule(string DocumentType, string Action, bool Allowed);

/// <summary>
/// The authenticated actor for a request: identity, seat, effective permissions with scopes, field and document-type
/// rules, and the authentication strength (for step-up). Computed once per request from the token and the cached
/// permission set (invalidated by the tenant's permissions epoch).
/// </summary>
public sealed record Principal(
    TenantId TenantId,
    UserId UserId,
    MembershipId MembershipId,
    string Email,
    string DisplayName,
    string Language,
    bool IsOwner,
    bool IsPlatformOperator,
    string ActorType,
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<FieldRule> FieldRules,
    IReadOnlyList<DocumentTypeRule> DocumentTypeRules,
    DateTimeOffset AuthTime,
    string AuthMethods,
    IReadOnlyList<Guid>? RoleIds = null)
{
    /// <summary>The active roles the grants came from (empty for owners without assignments and for API keys narrowed by scope).</summary>
    public IReadOnlyList<Guid> Roles => RoleIds ?? [];

    public bool Has(string permission) =>
        IsOwner || Grants.Any(g => PermissionCatalog.Covers(g.Permission, permission));

    /// <summary>Scopes under which the permission is held; null when not held at all.</summary>
    public RecordScopes? ScopesFor(string permission)
    {
        if (IsOwner)
        {
            return RecordScopes.All;
        }

        var matching = Grants.Where(g => PermissionCatalog.Covers(g.Permission, permission)).ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        if (matching.Any(static g => g.Scopes.CompanyIds.Count == 0 && g.Scopes.BranchIds.Count == 0 && g.Scopes.WarehouseIds.Count == 0))
        {
            return RecordScopes.All;
        }

        return new RecordScopes(
            matching.SelectMany(static g => g.Scopes.CompanyIds).ToHashSet(),
            matching.SelectMany(static g => g.Scopes.BranchIds).ToHashSet(),
            matching.SelectMany(static g => g.Scopes.WarehouseIds).ToHashSet());
    }

    public string FieldAccess(string entityType, string field)
    {
        if (IsOwner)
        {
            return "editable";
        }

        var rules = FieldRules.Where(r => string.Equals(r.EntityType, entityType, StringComparison.Ordinal) && string.Equals(r.Field, field, StringComparison.Ordinal)).ToList();
        if (rules.Count == 0)
        {
            return "editable";
        }

        // The most permissive rule across the member's roles wins (a role that can edit is not restricted by another).
        if (rules.Any(static r => r.Access is "editable"))
        {
            return "editable";
        }

        return rules.Any(static r => r.Access is "read_only") ? "read_only" : "hidden";
    }

    public bool MayActOnDocumentType(string documentType, string action)
    {
        if (IsOwner)
        {
            return true;
        }

        var rules = DocumentTypeRules.Where(r => string.Equals(r.DocumentType, documentType, StringComparison.Ordinal) && string.Equals(r.Action, action, StringComparison.Ordinal)).ToList();
        return rules.Count == 0 || rules.Any(static r => r.Allowed);
    }
}

/// <summary>Resolves the current request's principal; null for anonymous endpoints.</summary>
public interface ICurrentPrincipal
{
    Principal? Principal { get; }

    Principal Required { get; }
}

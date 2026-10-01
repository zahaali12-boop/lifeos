using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Identity.Domain;

/// <summary>app.idn_roles: a bundle of permission grants, field rules and document-type rules.</summary>
public sealed class Role : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public bool IsSystem { get; set; }

    public string? TemplateCode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];

    public List<FieldRuleEntity> FieldRules { get; set; } = [];

    public List<DocumentTypeRuleEntity> DocumentTypeRules { get; set; } = [];
}

public sealed class RolePermission : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public string PermissionKey { get; set; } = string.Empty;
}

public sealed class RoleAssignment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid MembershipId { get; set; }

    public Guid RoleId { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public List<AssignmentScope> Scopes { get; set; } = [];

    public Role Role { get; set; } = null!;

    public bool IsValidOn(DateOnly date) => (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || ValidTo >= date);
}

public sealed class AssignmentScope : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid AssignmentId { get; set; }

    public string ScopeType { get; set; } = string.Empty;

    public Guid ScopeId { get; set; }
}

public sealed class FieldRuleEntity : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RoleId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public string Access { get; set; } = "editable";
}

public sealed class DocumentTypeRuleEntity : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RoleId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool Allowed { get; set; }
}

public sealed class SodRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string PermissionA { get; set; } = string.Empty;

    public string PermissionB { get; set; } = string.Empty;

    public string Severity { get; set; } = "warn";

    public LocalizedText Rationale { get; set; } = new();

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class SodException : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid SodRuleId { get; set; }

    public Guid MembershipId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid ApprovedBy { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? RevokeReason { get; set; }
}

public sealed class ApiKey : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public byte[] KeyHash { get; set; } = [];

    public string[] Scopes { get; set; } = [];

    public System.Net.IPAddress[] IpAllowlist { get; set; } = [];

    public Guid? MembershipId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

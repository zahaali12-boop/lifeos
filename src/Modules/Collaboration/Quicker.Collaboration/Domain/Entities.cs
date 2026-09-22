using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Collaboration.Domain;

/// <summary>One member's in-app notification.</summary>
public sealed class Notification : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid MembershipId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public LocalizedText Title { get; set; } = new();

    public LocalizedText Body { get; set; } = new();

    public string? Link { get; set; }

    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string Data { get; set; } = "{}";

    public Guid? ActorMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Channels a member wants for a kind; the row with kind <c>*</c> is the member's default.</summary>
public sealed class NotificationPreference : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid MembershipId { get; set; }

    public string Kind { get; set; } = "*";

    public bool InApp { get; set; } = true;

    public bool Email { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An email queued for the send job, with its outcome.</summary>
public sealed class EmailLog : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? NotificationId { get; set; }

    public Guid? MembershipId { get; set; }

    public string ToAddress { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string TextBody { get; set; } = string.Empty;

    public string Status { get; set; } = "queued";

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public Guid? JobId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A file attached to a record; the bytes live in the object store under <see cref="StorageKey"/>.</summary>
public sealed class Attachment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public Guid? UploadedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A comment on a record; mentions are membership ids; deletion is soft so threads keep their shape.</summary>
public sealed class Comment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid AuthorMembershipId { get; set; }

    public string Body { get; set; } = string.Empty;

    public Guid[] Mentions { get; set; } = [];

    public Guid? ParentId { get; set; }

    public DateTimeOffset? EditedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Activity : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid? ActorMembershipId { get; set; }

    public LocalizedText Summary { get; set; } = new();

    public string Data { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DocumentLinkRecord : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string FromType { get; set; } = string.Empty;

    public Guid FromId { get; set; }

    public string ToType { get; set; } = string.Empty;

    public Guid ToId { get; set; }

    public string Relation { get; set; } = string.Empty;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A grid configuration (filters, sort, columns, grouping) owned by a member, optionally shared with the tenant.</summary>
public sealed class SavedView : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid OwnerMembershipId { get; set; }

    public bool Shared { get; set; }

    public bool IsDefault { get; set; }

    public string Definition { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A tenant-defined field on a host entity: type, requiredness, options for selects, rules, and whether the host column is indexed on it.</summary>
public sealed class CustomFieldDefinition : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public LocalizedText Label { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    public string Type { get; set; } = "text";

    public bool Required { get; set; }

    public string Options { get; set; } = "[]";

    public string Rules { get; set; } = "{}";

    public bool Indexed { get; set; }

    public int Position { get; set; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

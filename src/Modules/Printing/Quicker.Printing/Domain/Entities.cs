using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Printing.Domain;

/// <summary>A print template of a document type and variant (DOMAIN_MODEL §21): its language mode and paper, the company or branch it is for, whether it is the default, and its current version.</summary>
public sealed class PrintTemplate : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public string Variant { get; set; } = "standard";

    public LocalizedText Name { get; set; } = new();

    /// <summary>en, ar or bilingual.</summary>
    public string LanguageMode { get; set; } = "bilingual";

    public string Paper { get; set; } = "A4";

    public Guid? CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Copied from the shipped template; an edit keeps the flag (the history shows version 1 as shipped).</summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public int CurrentVersion { get; set; } = 1;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PrintTemplateVersion : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid TemplateId { get; set; }

    public int Version { get; set; }

    public string Html { get; set; } = string.Empty;

    public string Css { get; set; } = string.Empty;

    public string SampleData { get; set; } = "{}";

    public string? Note { get; set; }

    public Guid? PublishedBy { get; set; }

    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>One PDF of one document as it stood: the rendered HTML, the fingerprint it is cached by, the file in the object store and, for a final document, the evidence copy under retention.</summary>
public sealed class PrintRender : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public Guid? DocumentId { get; set; }

    public string Parameters { get; set; } = "{}";

    public string Variant { get; set; } = "standard";

    public Guid TemplateId { get; set; }

    public Guid TemplateVersionId { get; set; }

    public int TemplateVersion { get; set; }

    public string LanguageMode { get; set; } = "bilingual";

    public string Title { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public bool IsFinal { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    /// <summary>queued, ready or failed.</summary>
    public string Status { get; set; } = "queued";

    public string? StorageKey { get; set; }

    public string? EvidenceKey { get; set; }

    public string? Sha256 { get; set; }

    public long? SizeBytes { get; set; }

    public string? Error { get; set; }

    public Guid? JobId { get; set; }

    public Guid? RequestedBy { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? RenderedAt { get; set; }
}

public sealed class PrintEmail : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RenderId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public Guid? DocumentId { get; set; }

    public string ToAddress { get; set; } = string.Empty;

    public string? ToName { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string? Message { get; set; }

    /// <summary>queued, sent or failed.</summary>
    public string Status { get; set; } = "queued";

    public string? Error { get; set; }

    public Guid? JobId { get; set; }

    public Guid? RequestedBy { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>
/// A link that opens one rendered PDF without signing in, for WhatsApp and the like (roadmap 5.9, ADR-0022): the
/// token is given once and only its hash is kept; the link expires, can be revoked, and counts its openings.
/// </summary>
public sealed class PrintShareLink : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RenderId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public Guid? DocumentId { get; set; }

    /// <summary>SHA-256 of the token, hex; the token itself is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>link or whatsapp.</summary>
    public string Channel { get; set; } = "link";

    /// <summary>The phone number (digits) the link was made for, when the channel has one.</summary>
    public string? Recipient { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public int OpenCount { get; set; }

    public DateTimeOffset? LastOpenedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

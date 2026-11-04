using System.Text.Json;

namespace Quicker.Printing.Application;

public sealed record PrintTemplateSummary(
    Guid Id,
    string Code,
    string DocumentType,
    string Variant,
    IReadOnlyDictionary<string, string> Name,
    string LanguageMode,
    string Paper,
    Guid? CompanyId,
    Guid? BranchId,
    bool IsDefault,
    bool IsSystem,
    bool IsActive,
    int CurrentVersion,
    DateTimeOffset UpdatedAt,
    string? Html = null,
    string? Css = null,
    JsonElement? SampleData = null);

public sealed record PrintTemplateVersionSummary(Guid Id, int Version, string? Note, Guid? PublishedBy, DateTimeOffset PublishedAt, bool IsCurrent);

/// <summary>An edit of a template: a new version of its HTML and CSS, and the settings that are not versioned.</summary>
public sealed record SavePrintTemplateRequest(
    IReadOnlyDictionary<string, string>? Name = null,
    string? LanguageMode = null,
    string? Paper = null,
    string? Html = null,
    string? Css = null,
    bool? IsDefault = null,
    bool? IsActive = null,
    Guid? CompanyId = null,
    Guid? BranchId = null,
    string? Note = null);

/// <summary>A copy of a template for a company or a branch, starting from the source's current version.</summary>
public sealed record CopyPrintTemplateRequest(string Code, IReadOnlyDictionary<string, string>? Name = null, Guid? CompanyId = null, Guid? BranchId = null, string? LanguageMode = null);

/// <summary>What to print: the document, the variant (tax invoice or pro forma), and which template when not the default.</summary>
public sealed record PrintRequest(
    string DocumentType,
    Guid? DocumentId = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string Variant = "standard",
    Guid? TemplateId = null);

public sealed record PrintEmailRequest(
    string DocumentType,
    Guid? DocumentId = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string Variant = "standard",
    Guid? TemplateId = null,
    string? To = null,
    string? Subject = null,
    string? Message = null);

public sealed record PrintRenderSummary(
    Guid Id,
    Guid CompanyId,
    string DocumentType,
    Guid? DocumentId,
    JsonElement Parameters,
    string Variant,
    Guid TemplateId,
    int TemplateVersion,
    string LanguageMode,
    string Title,
    string Number,
    string FileName,
    bool IsFinal,
    bool IsEvidence,
    string Status,
    long? SizeBytes,
    string? Sha256,
    string? Error,
    Guid? JobId,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RenderedAt,
    bool Reused);

public sealed record PrintEmailSummary(
    Guid Id,
    Guid RenderId,
    string DocumentType,
    Guid? DocumentId,
    string To,
    string? ToName,
    string Subject,
    string? Message,
    string Status,
    string? Error,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SentAt);

/// <summary>A link to a ready render for someone without an account: the channel it goes by (link or whatsapp), the phone it is for, and how long it lives (1 to 30 days).</summary>
public sealed record PrintShareRequest(Guid RenderId, string Channel = "link", string? Recipient = null, int ExpiresInDays = 7);

/// <summary>A share link; the URL is given once, on creation, since only the token's hash is kept.</summary>
public sealed record PrintShareSummary(
    Guid Id,
    Guid RenderId,
    string DocumentType,
    Guid? DocumentId,
    string Number,
    string Channel,
    string? Recipient,
    string? Url,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    int OpenCount,
    DateTimeOffset? LastOpenedAt,
    DateTimeOffset CreatedAt,
    bool IsLive);

/// <summary>A document rendered as HTML for the browser's own print preview (the same template the PDF uses).</summary>
public sealed record PrintPreview(string Html, string Title, string FileName, string LanguageMode, Guid TemplateId, int TemplateVersion, bool IsFinal);

/// <summary>The PDF of a render, streamed from the object store.</summary>
public sealed record PrintFile(Stream Content, string ContentType, string FileName, long? Length);

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Printing.Domain;
using Quicker.Printing.Persistence;
using Scriban;

namespace Quicker.Printing.Application;

/// <summary>
/// Print templates per tenant (DOMAIN_MODEL §21, ADR-0022): the shipped ones are copied in the first time a document
/// type is printed or listed, so an admin edits a copy (every edit is a new version, a rollback a new version with the
/// old content, so the history stays whole); a template is for the tenant, a company or a branch, and the most
/// specific active default wins when a document prints. The designer of M7 edits what this service keeps.
/// </summary>
public sealed class TemplateService(PrintingDbContext db, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public static readonly IReadOnlyList<string> Papers = ["A4", "Letter"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Copies the shipped templates of the document type into the tenant when it has none of them yet.</summary>
    public async Task EnsureShippedAsync(string documentType, CancellationToken cancellationToken)
    {
        var shipped = ShippedTemplates.For(documentType).ToList();
        if (shipped.Count == 0)
        {
            return;
        }

        var codes = shipped.Select(static s => s.Code).ToList();
        var present = await db.Templates.Where(t => codes.Contains(t.Code)).Select(static t => t.Code).ToListAsync(cancellationToken);
        var added = false;
        foreach (var template in shipped.Where(s => !present.Contains(s.Code, StringComparer.Ordinal)))
        {
            var entity = new PrintTemplate
            {
                Id = Guid.CreateVersion7(),
                Code = template.Code,
                DocumentType = template.DocumentType,
                Variant = template.Variant,
                Name = template.Name,
                LanguageMode = template.LanguageMode,
                Paper = "A4",
                IsDefault = true,
                IsSystem = true,
                CurrentVersion = 1,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
            };
            db.Templates.Add(entity);
            db.Versions.Add(new PrintTemplateVersion
            {
                Id = Guid.CreateVersion7(),
                TemplateId = entity.Id,
                Version = 1,
                Html = ShippedTemplates.Html(template),
                Css = string.Empty,
                SampleData = ShippedTemplates.Sample(template),
                Note = "Shipped with the product",
                PublishedAt = clock.UtcNow,
            });
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The template a document prints with: the one asked for, else the most specific active default (branch, company, tenant).</summary>
    public async Task<Result<(PrintTemplate Template, PrintTemplateVersion Version)>> ResolveAsync(string documentType, string variant, Guid companyId, Guid? branchId, Guid? templateId, CancellationToken cancellationToken)
    {
        await EnsureShippedAsync(documentType, cancellationToken);
        PrintTemplate? template;
        if (templateId is { } id)
        {
            template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (template is null)
            {
                return Error.NotFound("print_template", id);
            }

            if (!string.Equals(template.DocumentType, documentType, StringComparison.Ordinal) || !string.Equals(template.Variant, variant, StringComparison.Ordinal))
            {
                return Error.Validation("print.template_mismatch", "The template is for another document type or variant.").WithWhy(("template", template.Code), ("documentType", template.DocumentType), ("variant", template.Variant));
            }
        }
        else
        {
            var candidates = await db.Templates.AsNoTracking().Where(t => t.DocumentType == documentType && t.Variant == variant && t.IsActive).ToListAsync(cancellationToken);
            template = candidates.Where(t => branchId is not null && t.BranchId == branchId).OrderByDescending(static t => t.IsDefault).ThenBy(static t => t.Code, StringComparer.Ordinal).FirstOrDefault()
                ?? candidates.Where(t => t.CompanyId == companyId && t.BranchId is null).OrderByDescending(static t => t.IsDefault).ThenBy(static t => t.Code, StringComparer.Ordinal).FirstOrDefault()
                ?? candidates.Where(static t => t.CompanyId is null && t.BranchId is null).OrderByDescending(static t => t.IsDefault).ThenByDescending(static t => t.IsSystem).ThenBy(static t => t.Code, StringComparer.Ordinal).FirstOrDefault();
            if (template is null)
            {
                return Error.Validation("print.template_missing", "No active template exists for this document type and variant.").WithWhy(("documentType", documentType), ("variant", variant));
            }
        }

        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.TemplateId == template.Id && v.Version == template.CurrentVersion, cancellationToken);
        return (template, version);
    }

    public async Task<IReadOnlyList<PrintTemplateSummary>> ListAsync(string? documentType, CancellationToken cancellationToken)
    {
        foreach (var type in string.IsNullOrWhiteSpace(documentType) ? ShippedTemplates.All.Select(static s => s.DocumentType).Distinct(StringComparer.Ordinal) : [documentType.Trim()])
        {
            await EnsureShippedAsync(type, cancellationToken);
        }

        var query = db.Templates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(documentType))
        {
            var type = documentType.Trim();
            query = query.Where(t => t.DocumentType == type);
        }

        var templates = await query.OrderBy(static t => t.DocumentType).ThenBy(static t => t.Variant).ThenBy(static t => t.Code).ToListAsync(cancellationToken);
        return templates.Select(t => Map(t, null)).ToList();
    }

    public async Task<Result<PrintTemplateSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("print_template", id);
        }

        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.TemplateId == id && v.Version == template.CurrentVersion, cancellationToken);
        return Map(template, version);
    }

    public async Task<PrintTemplateVersion?> VersionAsync(Guid templateId, int? version, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return null;
        }

        var number = version ?? template.CurrentVersion;
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(v => v.TemplateId == templateId && v.Version == number, cancellationToken);
    }

    public async Task<IReadOnlyList<PrintTemplateVersionSummary>> VersionsAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return [];
        }

        var versions = await db.Versions.AsNoTracking().Where(v => v.TemplateId == id).OrderByDescending(static v => v.Version).ToListAsync(cancellationToken);
        return versions.Select(v => new PrintTemplateVersionSummary(v.Id, v.Version, v.Note, v.PublishedBy, v.PublishedAt, v.Version == template.CurrentVersion)).ToList();
    }

    /// <summary>Edits the template: new HTML or CSS becomes a new version; the name, language mode, paper, scope and default flag change in place.</summary>
    public async Task<Result<PrintTemplateSummary>> SaveAsync(Guid id, SavePrintTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = await db.Templates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("print_template", id);
        }

        var current = await db.Versions.AsNoTracking().SingleAsync(v => v.TemplateId == id && v.Version == template.CurrentVersion, cancellationToken);
        var settings = ApplySettings(template, request.Name, request.LanguageMode, request.Paper, request.CompanyId, request.BranchId, request.IsActive);
        if (settings.IsFailure)
        {
            return settings.Error!;
        }

        PrintTemplateVersion? published = null;
        var html = request.Html ?? current.Html;
        var css = request.Css ?? current.Css;
        if (!string.Equals(html, current.Html, StringComparison.Ordinal) || !string.Equals(css, current.Css, StringComparison.Ordinal))
        {
            var parsed = Template.Parse(html);
            if (parsed.HasErrors)
            {
                return Error.Validation("template.invalid", "The template does not parse.").WithWhy(("messages", parsed.Messages.Select(static m => m.ToString()).ToList()));
            }

            published = new PrintTemplateVersion
            {
                Id = Guid.CreateVersion7(),
                TemplateId = template.Id,
                Version = template.CurrentVersion + 1,
                Html = html,
                Css = css,
                SampleData = current.SampleData,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                PublishedBy = principal.Principal?.MembershipId.Value,
                PublishedAt = clock.UtcNow,
            };
            db.Versions.Add(published);
            template.CurrentVersion = published.Version;
        }

        if (request.IsDefault is { } isDefault)
        {
            await SetDefaultAsync(template, isDefault, cancellationToken);
        }

        template.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_template", template.Id, template.Code, AuditActions.Updated, After: new { template.CurrentVersion, template.LanguageMode, template.Paper, template.IsDefault, template.IsActive, newVersion = published?.Version }, CompanyId: template.CompanyId), cancellationToken);
        return Map(template, published ?? current);
    }

    /// <summary>A copy for a company or a branch (or another tenant-wide variant), starting from the source's current version.</summary>
    public async Task<Result<PrintTemplateSummary>> CopyAsync(Guid id, CopyPrintTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (source is null)
        {
            return Error.NotFound("print_template", id);
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is 0 or > 64 || !code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return Error.Validation("template.code_invalid", "A template code is letters, digits, dashes and underscores.").WithWhy(("code", request.Code));
        }

        if (await db.Templates.AnyAsync(t => t.Code == code, cancellationToken))
        {
            return Error.Conflict("template.code_taken", "A template with this code exists.").WithWhy(("code", code));
        }

        var current = await db.Versions.AsNoTracking().SingleAsync(v => v.TemplateId == id && v.Version == source.CurrentVersion, cancellationToken);
        var copy = new PrintTemplate
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            DocumentType = source.DocumentType,
            Variant = source.Variant,
            Name = request.Name is { Count: > 0 } ? new LocalizedText(request.Name) : source.Name,
            LanguageMode = source.LanguageMode,
            Paper = source.Paper,
            IsDefault = false,
            IsSystem = false,
            CurrentVersion = 1,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        var settings = ApplySettings(copy, null, request.LanguageMode, null, request.CompanyId, request.BranchId, null);
        if (settings.IsFailure)
        {
            return settings.Error!;
        }

        db.Templates.Add(copy);
        var version = new PrintTemplateVersion
        {
            Id = Guid.CreateVersion7(),
            TemplateId = copy.Id,
            Version = 1,
            Html = current.Html,
            Css = current.Css,
            SampleData = current.SampleData,
            Note = $"Copied from {source.Code} version {source.CurrentVersion}",
            PublishedBy = principal.Principal?.MembershipId.Value,
            PublishedAt = clock.UtcNow,
        };
        db.Versions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_template", copy.Id, copy.Code, AuditActions.Created, After: new { source = source.Code, copy.DocumentType, copy.Variant, copy.CompanyId, copy.BranchId }, CompanyId: copy.CompanyId), cancellationToken);
        return Map(copy, version);
    }

    /// <summary>Makes an earlier version current again, as a new version with that content (nothing is rewritten).</summary>
    public async Task<Result<PrintTemplateSummary>> RollbackAsync(Guid id, int version, CancellationToken cancellationToken)
    {
        var template = await db.Templates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("print_template", id);
        }

        var target = await db.Versions.AsNoTracking().SingleOrDefaultAsync(v => v.TemplateId == id && v.Version == version, cancellationToken);
        if (target is null)
        {
            return Error.NotFound("print_template_version", version);
        }

        if (version == template.CurrentVersion)
        {
            return Error.Conflict("template.already_current", "That version is the current one.").WithWhy(("version", version));
        }

        var restored = new PrintTemplateVersion
        {
            Id = Guid.CreateVersion7(),
            TemplateId = template.Id,
            Version = template.CurrentVersion + 1,
            Html = target.Html,
            Css = target.Css,
            SampleData = target.SampleData,
            Note = $"Rolled back to version {version}",
            PublishedBy = principal.Principal?.MembershipId.Value,
            PublishedAt = clock.UtcNow,
        };
        db.Versions.Add(restored);
        template.CurrentVersion = restored.Version;
        template.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_template", template.Id, template.Code, AuditActions.StateChanged, After: new { rolledBackTo = version, template.CurrentVersion }, CompanyId: template.CompanyId), cancellationToken);
        return Map(template, restored);
    }

    private static Result ApplySettings(PrintTemplate template, IReadOnlyDictionary<string, string>? name, string? languageMode, string? paper, Guid? companyId, Guid? branchId, bool? isActive)
    {
        if (name is { Count: > 0 })
        {
            template.Name = new LocalizedText(name);
        }

        if (!string.IsNullOrWhiteSpace(languageMode))
        {
            var mode = languageMode.Trim().ToLowerInvariant();
            if (!LanguageModes.All.Contains(mode, StringComparer.Ordinal))
            {
                return Error.Validation("template.language_mode_invalid", "The language mode is en, ar or bilingual.").WithWhy(("languageMode", languageMode));
            }

            template.LanguageMode = mode;
        }

        if (!string.IsNullOrWhiteSpace(paper))
        {
            var size = Papers.FirstOrDefault(p => string.Equals(p, paper.Trim(), StringComparison.OrdinalIgnoreCase));
            if (size is null)
            {
                return Error.Validation("template.paper_invalid", "The paper is A4 or Letter.").WithWhy(("paper", paper));
            }

            template.Paper = size;
        }

        if (companyId is not null || branchId is not null)
        {
            template.CompanyId = companyId;
            template.BranchId = branchId;
        }

        if (isActive is { } active)
        {
            template.IsActive = active;
        }

        return Result.Success();
    }

    private async Task SetDefaultAsync(PrintTemplate template, bool isDefault, CancellationToken cancellationToken)
    {
        template.IsDefault = isDefault;
        if (!isDefault)
        {
            return;
        }

        var peers = await db.Templates.Where(t => t.Id != template.Id && t.DocumentType == template.DocumentType && t.Variant == template.Variant && t.CompanyId == template.CompanyId && t.BranchId == template.BranchId && t.IsDefault).ToListAsync(cancellationToken);
        foreach (var peer in peers)
        {
            peer.IsDefault = false;
            peer.UpdatedAt = clock.UtcNow;
        }
    }

    private static PrintTemplateSummary Map(PrintTemplate t, PrintTemplateVersion? v) =>
        new(t.Id, t.Code, t.DocumentType, t.Variant, t.Name.Values, t.LanguageMode, t.Paper, t.CompanyId, t.BranchId, t.IsDefault, t.IsSystem, t.IsActive, t.CurrentVersion, t.UpdatedAt,
            v?.Html, v?.Css, v is null ? null : JsonSerializer.Deserialize<JsonElement>(v.SampleData, Json));
}

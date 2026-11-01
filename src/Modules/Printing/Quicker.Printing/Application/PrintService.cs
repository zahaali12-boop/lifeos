using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;
using Quicker.Printing.Contracts;
using Quicker.Printing.Domain;
using Quicker.Printing.Persistence;
using Quicker.Storage;
using Scriban.Runtime;

#pragma warning disable MA0002 // Scriban script objects are case-sensitive ordinal maps by design.

namespace Quicker.Printing.Application;

/// <summary>
/// The print pipeline (roadmap 5.9, ADR-0022). A document's provider builds its print model; the template resolved for
/// the company renders it to HTML with the company's, branch's and partner's blocks, the labels of the language mode
/// and, for an e-invoice, the QR code; that HTML is the browser's print preview and the exact input of the PDF, so a
/// render is cached by the fingerprint of its HTML (same template version and same document: same file). Rendering
/// runs in the worker through headless Chromium; a final document's render is also kept as evidence under retention;
/// an email sends the PDF to the partner's address (or another) and both are logged on the document.
/// </summary>
public sealed class PrintService(
    PrintingDbContext db,
    IEnumerable<IPrintModelProvider> providers,
    TemplateService templates,
    IPdfRenderer renderer,
    ICompanyDirectory companies,
    IPartnerDirectory partners,
    IObjectStorage storage,
    PrintOptions options,
    IJobQueue jobs,
    IEmailSender email,
    IActivityLog activities,
    IAuditSink audit,
    ICurrentPrincipal principal,
    IUnitOfWorkAccessor unitOfWork,
    IClock clock)
{
    public const string RenderJobType = "print.render";
    public const string EmailJobType = "print.email";
    public const int RenderMaxAttempts = 3;
    public const int EmailMaxAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, IPrintModelProvider> _providers = providers.ToDictionary(static p => p.DocumentType, StringComparer.Ordinal);

    public IReadOnlyList<PrintDocumentTypeSummary> DocumentTypes() =>
        _providers.Values.OrderBy(static p => p.DocumentType, StringComparer.Ordinal)
            .Select(static p => new PrintDocumentTypeSummary(p.DocumentType, p.Variants.Select(static v => new PrintVariantSummary(v.Code, v.Name.Values)).ToList())).ToList();

    /// <summary>The document as HTML, for the browser's own print preview; nothing is stored.</summary>
    public async Task<Result<PrintPreview>> PreviewAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var composed = await ComposeAsync(request, forPdf: false, cancellationToken);
        return composed.IsFailure ? composed.Error! : new PrintPreview(composed.Value.Html, PrintFormat.Resolve(composed.Value.Model.Title, composed.Value.Template.LanguageMode), composed.Value.FileName, composed.Value.Template.LanguageMode, composed.Value.Template.Id, composed.Value.Version.Version, composed.Value.Model.IsFinal);
    }

    /// <summary>A template rendered with its sample data (the designer's preview and the layout regression test).</summary>
    public async Task<Result<PrintPreview>> PreviewTemplateAsync(Guid templateId, int? version, bool forPdf, CancellationToken cancellationToken)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        var content = template is null ? null : await templates.VersionAsync(templateId, version, cancellationToken);
        if (template is null || content is null)
        {
            return Error.NotFound("print_template", templateId);
        }

        var mode = template.LanguageMode;
        var sample = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(content.SampleData) ? "{}" : content.SampleData, Json);
        var globals = new ScriptObject();
        if (sample.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sample.EnumerateObject())
            {
                globals[property.Name] = ScribanModel.ToScript(property.Value, mode);
            }
        }

        AddCommon(globals, mode, template.Variant, template.Paper, content.Css, forPdf);
        if (globals.TryGetValue("document", out var document) && document is ScriptObject doc && doc.TryGetValue("qr_payload", out var payload) && payload is string qr && qr.Length > 0)
        {
            globals["qr_svg"] = QrSvg(qr);
        }

        var html = ScribanModel.Render(content.Html, globals);
        return html.IsFailure ? html.Error! : new PrintPreview(Stamp(html.Value), PrintFormat.Resolve(template.Name, mode), template.Code.ToLowerInvariant() + ".pdf", mode, template.Id, content.Version, false);
    }

    /// <summary>Composes and renders the PDF now, in the caller's unit of work (a scheduled report attaches it), and returns its bytes; a render with the same HTML is reused.</summary>
    public async Task<Result<PrintedPdf>> PrintNowAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var composed = await ComposeAsync(request, forPdf: true, cancellationToken);
        if (composed.IsFailure)
        {
            return composed.Error!;
        }

        var (render, _) = await RequestRenderAsync(composed.Value, cancellationToken);
        var ready = await RenderNowAsync(render.Id, cancellationToken);
        if (ready.IsFailure)
        {
            return ready.Error!;
        }

        var stored = await db.Renders.AsNoTracking().SingleAsync(r => r.Id == render.Id, cancellationToken);
        await using var file = await storage.GetAsync(stored.StorageKey!, cancellationToken: cancellationToken) ?? throw new InvalidOperationException($"Render {render.Id} has no file in the store.");
        using var buffer = new MemoryStream();
        await file.Content.CopyToAsync(buffer, cancellationToken);
        return new PrintedPdf(buffer.ToArray(), stored.FileName, stored.Title);
    }

    /// <summary>Asks for the PDF: a render with the same HTML already made is reused; otherwise a queued render the worker fills.</summary>
    public async Task<Result<PrintRenderSummary>> RequestAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var composed = await ComposeAsync(request, forPdf: true, cancellationToken);
        if (composed.IsFailure)
        {
            return composed.Error!;
        }

        var render = await RequestRenderAsync(composed.Value, cancellationToken);
        return Map(render.Render, render.Reused);
    }

    public async Task<IReadOnlyList<PrintRenderSummary>> ListRendersAsync(string documentType, Guid? documentId, CancellationToken cancellationToken)
    {
        var query = db.Renders.AsNoTracking().Where(r => r.DocumentType == documentType);
        query = documentId is { } id ? query.Where(r => r.DocumentId == id) : query;
        var renders = await query.OrderByDescending(static r => r.RequestedAt).Take(200).ToListAsync(cancellationToken);
        return renders.Select(static r => Map(r, false)).ToList();
    }

    public async Task<Result<PrintRenderSummary>> GetRenderAsync(Guid id, CancellationToken cancellationToken)
    {
        var render = await db.Renders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return render is null ? Error.NotFound("print_render", id) : Map(render, false);
    }

    /// <summary>The PDF itself, streamed from the object store once the render is ready.</summary>
    public async Task<Result<PrintFile>> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        var render = await db.Renders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (render is null)
        {
            return Error.NotFound("print_render", id);
        }

        if (render.Status != "ready" || render.StorageKey is null)
        {
            return Error.Conflict("print.render_not_ready", render.Status == "failed" ? "The render failed." : "The render is not ready yet.").WithWhy(("status", render.Status), ("error", render.Error));
        }

        var stored = await storage.GetAsync(render.StorageKey, cancellationToken: cancellationToken);
        if (stored is null)
        {
            return Error.Conflict("print.file_missing", "The rendered file is no longer in the store.").WithWhy(("key", render.StorageKey));
        }

        return new PrintFile(stored.Content, "application/pdf", render.FileName, stored.Info.Length);
    }

    /// <summary>Runs Chromium on the render's HTML, stores the PDF (and the evidence copy of a final document) and logs it on the document. Idempotent once ready.</summary>
    public async Task<Result<PrintRenderSummary>> RenderNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var render = await db.Renders.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (render is null)
        {
            return Error.NotFound("print_render", id);
        }

        if (render.Status == "ready")
        {
            return Map(render, true);
        }

        var bytes = await renderer.RenderPdfAsync(render.Html, cancellationToken);
        var tenant = unitOfWork.Current.Context.TenantId.Value.ToString("N");
        var folder = render.DocumentId is { } documentId ? documentId.ToString("N") : "reports";
        var key = $"print/{tenant}/{render.DocumentType}/{folder}/{render.Id:N}.pdf";
        using (var content = new MemoryStream(bytes, writable: false))
        {
            await storage.PutAsync(key, content, "application/pdf", cancellationToken: cancellationToken);
        }

        string? evidenceKey = null;
        if (render.IsFinal && render.DocumentId is not null)
        {
            evidenceKey = $"{ObjectKeys.ImmutablePrefix}print/{tenant}/{render.DocumentType}/{folder}/{render.Id:N}.pdf";
            using var evidence = new MemoryStream(bytes, writable: false);
            await storage.PutAsync(evidenceKey, evidence, "application/pdf", new ObjectRetention(clock.UtcNow.AddYears(options.EvidenceRetentionYears)), cancellationToken);
        }

        render.Status = "ready";
        render.StorageKey = key;
        render.EvidenceKey = evidenceKey;
        render.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        render.SizeBytes = bytes.LongLength;
        render.Error = null;
        render.RenderedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (render.DocumentId is { } document)
        {
            await activities.RecordAsync(new ActivityEntry(render.DocumentType, document, PrintActivityKinds.Rendered,
                LocalizedText.Bilingual($"{render.Title} {render.Number} rendered to PDF", $"أُخرج {render.Title} {render.Number} بصيغة PDF"),
                new { renderId = render.Id, render.TemplateVersion, evidence = evidenceKey is not null, render.SizeBytes }), cancellationToken);
        }

        return Map(render, false);
    }

    /// <summary>Queues an email of the document's PDF to the partner's address or the one given; the worker renders first when it must.</summary>
    public async Task<Result<PrintEmailSummary>> EmailAsync(PrintEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var composed = await ComposeAsync(new PrintRequest(request.DocumentType, request.DocumentId, request.Parameters, request.Variant, request.TemplateId), forPdf: true, cancellationToken);
        if (composed.IsFailure)
        {
            return composed.Error!;
        }

        var to = string.IsNullOrWhiteSpace(request.To) ? composed.Value.Model.PartnerEmail ?? composed.Value.Partner?.Email : request.To.Trim();
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("print.no_email", "The partner has no email address; give one to send to.").WithWhy(("partner", composed.Value.Partner?.Code));
        }

        var render = await RequestRenderAsync(composed.Value, cancellationToken);
        var title = PrintFormat.Resolve(composed.Value.Model.Title, composed.Value.Template.LanguageMode);
        var mail = new PrintEmail
        {
            Id = Guid.CreateVersion7(),
            CompanyId = composed.Value.Model.CompanyId,
            RenderId = render.Render.Id,
            DocumentType = request.DocumentType,
            DocumentId = request.DocumentId,
            ToAddress = to,
            ToName = composed.Value.Partner is { } partner ? PrintFormat.Resolve(partner.LegalName, composed.Value.Template.LanguageMode) : null,
            Subject = string.IsNullOrWhiteSpace(request.Subject) ? $"{title} {composed.Value.Model.Number}" : request.Subject.Trim(),
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            RequestedBy = principal.Principal?.MembershipId.Value,
            RequestedAt = clock.UtcNow,
        };
        db.Emails.Add(mail);
        await db.SaveChangesAsync(cancellationToken);
        mail.JobId = await jobs.EnqueueAsync(new JobRequest(EmailJobType, new PrintEmailPayload(mail.Id), MaxAttempts: EmailMaxAttempts), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_email", mail.Id, mail.Subject, AuditActions.Created, After: new { mail.DocumentType, mail.DocumentId, to = mail.ToAddress, renderId = mail.RenderId }, CompanyId: mail.CompanyId), cancellationToken);
        return Map(mail);
    }

    public async Task<IReadOnlyList<PrintEmailSummary>> ListEmailsAsync(string documentType, Guid? documentId, CancellationToken cancellationToken)
    {
        var query = db.Emails.AsNoTracking().Where(e => e.DocumentType == documentType);
        query = documentId is { } id ? query.Where(e => e.DocumentId == id) : query;
        var emails = await query.OrderByDescending(static e => e.RequestedAt).Take(200).ToListAsync(cancellationToken);
        return emails.Select(Map).ToList();
    }

    /// <summary>Sends one queued email: renders the PDF first when the worker has not yet, attaches it and logs the send on the document.</summary>
    public async Task<Result<PrintEmailSummary>> SendNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var mail = await db.Emails.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (mail is null)
        {
            return Error.NotFound("print_email", id);
        }

        if (mail.Status == "sent")
        {
            return Map(mail);
        }

        var rendered = await RenderNowAsync(mail.RenderId, cancellationToken);
        if (rendered.IsFailure)
        {
            return rendered.Error!;
        }

        var render = await db.Renders.AsNoTracking().SingleAsync(r => r.Id == mail.RenderId, cancellationToken);
        await using var stored = await storage.GetAsync(render.StorageKey!, cancellationToken: cancellationToken) ?? throw new InvalidOperationException($"Render {render.Id} has no file in the store.");
        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, cancellationToken);
        var text = mail.Message is null
            ? $"Please find attached {render.Title} {render.Number}.\n\nيرجى الاطلاع على {render.Title} {render.Number} المرفق."
            : mail.Message;
        var html = $"<p dir=\"auto\">{System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal)}</p>";
        await email.SendAsync(new EmailMessage(mail.ToAddress, mail.Subject, text, html, mail.ToName,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Quicker-Print-Render"] = render.Id.ToString() },
            [new EmailAttachment(render.FileName, "application/pdf", buffer.ToArray())]), cancellationToken);
        mail.Status = "sent";
        mail.Error = null;
        mail.SentAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (mail.DocumentId is { } document)
        {
            await activities.RecordAsync(new ActivityEntry(mail.DocumentType, document, PrintActivityKinds.Emailed,
                LocalizedText.Bilingual($"{render.Title} {render.Number} emailed to {mail.ToAddress}", $"أُرسل {render.Title} {render.Number} بالبريد إلى {mail.ToAddress}"),
                new { emailId = mail.Id, renderId = render.Id, to = mail.ToAddress }), cancellationToken);
        }

        return Map(mail);
    }

    // ------------------------------------------------------------------ internals

    private sealed record Composed(PrintModel Model, PrintTemplate Template, PrintTemplateVersion Version, string Html, string Fingerprint, string FileName, PartnerInfo? Partner, PrintRequest Request);

    private async Task<Result<Composed>> ComposeAsync(PrintRequest request, bool forPdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_providers.TryGetValue(request.DocumentType ?? string.Empty, out var provider))
        {
            return Error.Validation("print.document_type_unknown", "Nothing prints under this document type.").WithWhy(("documentType", request.DocumentType), ("known", _providers.Keys.Order(StringComparer.Ordinal).ToList()));
        }

        var variant = string.IsNullOrWhiteSpace(request.Variant) ? PrintVariants.Standard : request.Variant.Trim().ToLowerInvariant();
        if (!provider.Variants.Any(v => string.Equals(v.Code, variant, StringComparison.Ordinal)))
        {
            return Error.Validation("print.variant_unknown", "The document type has no such variant.").WithWhy(("variant", request.Variant), ("variants", provider.Variants.Select(static v => v.Code).ToList()));
        }

        var model = await provider.BuildAsync(new PrintSubject(provider.DocumentType, request.DocumentId, request.Parameters), variant, cancellationToken);
        if (model.IsFailure)
        {
            return model.Error!;
        }

        var resolved = await templates.ResolveAsync(provider.DocumentType, variant, model.Value.CompanyId, model.Value.BranchId, request.TemplateId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var (template, version) = resolved.Value;
        var mode = template.LanguageMode;
        var company = await companies.FindAsync(new CompanyId(model.Value.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", model.Value.CompanyId);
        }

        var branch = model.Value.BranchId is { } branchId ? await companies.FindBranchAsync(new BranchId(branchId), cancellationToken) : null;
        var partner = model.Value.PartnerId is { } partnerId ? await partners.FindAsync(partnerId, cancellationToken) : null;
        var addresses = partner is null ? [] : await partners.AddressesAsync(partner.Id, cancellationToken);

        var globals = new ScriptObject
        {
            ["title"] = PrintFormat.Resolve(model.Value.Title, mode),
            ["number"] = model.Value.Number,
            ["status"] = model.Value.Status,
            ["is_final"] = model.Value.IsFinal,
            ["document_date"] = PrintFormat.Date(model.Value.DocumentDate),
            ["currency"] = model.Value.Currency,
            ["company"] = CompanyBlock(company, mode),
            ["branch"] = branch is null ? null : new ScriptObject { ["code"] = branch.Code, ["name"] = PrintFormat.Resolve(branch.Name, mode) },
            ["partner"] = partner is null ? null : PartnerBlock(partner, addresses, mode),
            ["document"] = ScribanModel.Dictionary(model.Value.Data, mode),
        };
        if (model.Value.Data.TryGetValue("qr_payload", out var payload) && payload is string qr && qr.Length > 0)
        {
            globals["qr_svg"] = QrSvg(qr);
        }

        AddCommon(globals, mode, variant, template.Paper, version.Css, forPdf);
        var html = ScribanModel.Render(version.Html, globals);
        if (html.IsFailure)
        {
            return html.Error!.WithWhy(("template", template.Code), ("version", version.Version));
        }

        // The fingerprint is of the document and the template, not of the moment: the generation time goes in after
        // hashing, so the same document under the same template version reuses its PDF whenever it was asked for.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html.Value)));
        var fileName = model.Value.FileName ?? SafeFileName(model.Value.Number) + ".pdf";
        return new Composed(model.Value, template, version, Stamp(html.Value), fingerprint, fileName, partner, request with { Variant = variant });
    }

    /// <summary>Stands in for the generation time while the HTML is hashed; a template prints it as <c>{{ generated_at }}</c>.</summary>
    private const string GeneratedAtMarker = "\u2063generated-at\u2063";

    private string Stamp(string html) => html.Replace(GeneratedAtMarker, PrintFormat.DateTime(clock.UtcNow), StringComparison.Ordinal);

    private void AddCommon(ScriptObject globals, string mode, string variant, string paper, string css, bool forPdf)
    {
        globals["mode"] = mode;
        globals["is_rtl"] = string.Equals(mode, LanguageModes.Arabic, StringComparison.Ordinal);
        globals["variant"] = variant;
        globals["paper"] = paper;
        globals["generated_at"] = GeneratedAtMarker;
        globals["font_dir"] = forPdf ? renderer.FontDirectoryUrl : string.Empty;
        globals["css"] = ShippedTemplates.BaseCss + "\n" + css;
        globals["L"] = PrintLabels.For(mode);
    }

    private async Task<(PrintRender Render, bool Reused)> RequestRenderAsync(Composed composed, CancellationToken cancellationToken)
    {
        var existing = await db.Renders.Where(r => r.Fingerprint == composed.Fingerprint && r.Status != "failed").OrderByDescending(static r => r.RequestedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return (existing, true);
        }

        var render = new PrintRender
        {
            Id = Guid.CreateVersion7(),
            CompanyId = composed.Model.CompanyId,
            DocumentType = composed.Request.DocumentType,
            DocumentId = composed.Request.DocumentId,
            Parameters = JsonSerializer.Serialize(composed.Request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal), Json),
            Variant = composed.Request.Variant,
            TemplateId = composed.Template.Id,
            TemplateVersionId = composed.Version.Id,
            TemplateVersion = composed.Version.Version,
            LanguageMode = composed.Template.LanguageMode,
            Title = PrintFormat.Resolve(composed.Model.Title, composed.Template.LanguageMode),
            Number = composed.Model.Number,
            FileName = composed.FileName,
            IsFinal = composed.Model.IsFinal,
            Fingerprint = composed.Fingerprint,
            Html = composed.Html,
            Status = "queued",
            RequestedBy = principal.Principal?.MembershipId.Value,
            RequestedAt = clock.UtcNow,
        };
        db.Renders.Add(render);
        await db.SaveChangesAsync(cancellationToken);
        render.JobId = await jobs.EnqueueAsync(new JobRequest(RenderJobType, new PrintRenderPayload(render.Id), MaxAttempts: RenderMaxAttempts), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_render", render.Id, render.Number, AuditActions.Created, After: new { render.DocumentType, render.DocumentId, render.Variant, template = composed.Template.Code, render.TemplateVersion, render.IsFinal }, CompanyId: render.CompanyId), cancellationToken);
        return (render, false);
    }

    private static ScriptObject CompanyBlock(CompanyInfo company, string mode)
    {
        var address = company.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var registrations = company.RegistrationNumbers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new ScriptObject
        {
            ["code"] = company.Code,
            ["name"] = PrintFormat.Resolve(company.LegalName, mode),
            ["name_en"] = company.LegalName.Resolve("en"),
            ["name_ar"] = company.LegalName.Resolve("ar"),
            ["country"] = company.Country,
            ["currency"] = company.FunctionalCurrency.Code,
            ["address"] = ScribanModel.Strings(address),
            ["address_text"] = AddressText(address),
            ["registration_numbers"] = ScribanModel.Strings(registrations),
            ["tax_number"] = registrations.TryGetValue("vat", out var vat) ? vat : registrations.TryGetValue("tax", out var tax) ? tax : registrations.Values.FirstOrDefault(),
        };
    }

    private static ScriptObject PartnerBlock(PartnerInfo partner, IReadOnlyList<PartnerAddressInfo> addresses, string mode)
    {
        var address = addresses.FirstOrDefault(static a => a.Role == "billing") ?? addresses.FirstOrDefault(static a => a.IsDefault) ?? (addresses.Count > 0 ? addresses[0] : null);
        var lines = address?.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new ScriptObject
        {
            ["code"] = partner.Code,
            ["name"] = PrintFormat.Resolve(partner.LegalName, mode),
            ["name_en"] = partner.LegalName.Resolve("en"),
            ["name_ar"] = partner.LegalName.Resolve("ar"),
            ["email"] = partner.Email,
            ["language"] = partner.DefaultLanguage,
            ["country"] = address?.Country,
            ["region"] = address?.Region,
            ["address"] = ScribanModel.Strings(lines),
            ["address_text"] = AddressText(lines, address?.Region, address?.Country),
        };
    }

    private static readonly string[] AddressOrder = ["line1", "street", "line2", "district", "city", "postal_code", "postalCode", "state"];

    private static string AddressText(IReadOnlyDictionary<string, string> lines, params string?[] tail)
    {
        var ordered = AddressOrder
            .Where(lines.ContainsKey).Select(k => lines[k])
            .Concat(lines.Where(static kv => !AddressOrder.Contains(kv.Key, StringComparer.Ordinal)).Select(static kv => kv.Value))
            .Concat(tail.Where(static t => !string.IsNullOrWhiteSpace(t))!)
            .Where(static v => !string.IsNullOrWhiteSpace(v));
        return string.Join(", ", ordered);
    }

    private static string QrSvg(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true);
    }

    private static string SafeFileName(string number)
    {
        var safe = new string(number.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return safe.Length == 0 ? "document" : safe;
    }

    private static PrintRenderSummary Map(PrintRender r, bool reused) =>
        new(r.Id, r.CompanyId, r.DocumentType, r.DocumentId, JsonSerializer.Deserialize<JsonElement>(r.Parameters, Json), r.Variant, r.TemplateId, r.TemplateVersion, r.LanguageMode, r.Title, r.Number, r.FileName,
            r.IsFinal, r.EvidenceKey is not null, r.Status, r.SizeBytes, r.Sha256, r.Error, r.JobId, r.RequestedAt, r.RenderedAt, reused);

    private static PrintEmailSummary Map(PrintEmail e) => new(e.Id, e.RenderId, e.DocumentType, e.DocumentId, e.ToAddress, e.ToName, e.Subject, e.Message, e.Status, e.Error, e.RequestedAt, e.SentAt);
}

public sealed record PrintVariantSummary(string Code, IReadOnlyDictionary<string, string> Name);

public sealed record PrintDocumentTypeSummary(string DocumentType, IReadOnlyList<PrintVariantSummary> Variants);

/// <summary>What the pipeline writes on a document's timeline.</summary>
public static class PrintActivityKinds
{
    public const string Rendered = "print.rendered";
    public const string Emailed = "print.emailed";
    public const string Shared = "print.shared";
}

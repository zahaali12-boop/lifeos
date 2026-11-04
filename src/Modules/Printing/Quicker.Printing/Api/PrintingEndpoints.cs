using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Printing.Application;
using Quicker.Web;

namespace Quicker.Printing.Api;

/// <summary>Printing under /api/v1/print: templates and their versions, HTML previews, PDF renders and emails (roadmap 5.9, ADR-0022).</summary>
public static class PrintingEndpoints
{
    public static RouteGroupBuilder MapPrintingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var print = api.MapGroup("/print").WithTags("Printing").RequireAuthorization();

        print.MapGet("/document-types", (PrintService service) => TypedResults.Ok(service.DocumentTypes()))
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("The document types that print, each with its variants (the tax invoice and the pro forma of an invoice, say)");

        var templates = print.MapGroup("/templates");
        templates.MapGet("/", async (string? documentType, TemplateService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(documentType, ct)))
            .RequirePermission(PrintingPermissions.TemplateRead)
            .WithSummary("The tenant's print templates (the shipped ones copied in on first use), by document type");
        templates.MapGet("/{templateId:guid}", async (Guid templateId, TemplateService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(templateId, ct)))
            .RequirePermission(PrintingPermissions.TemplateRead)
            .WithSummary("The template with its current version's HTML, CSS and sample data");
        templates.MapPut("/{templateId:guid}", async (Guid templateId, SavePrintTemplateRequest request, TemplateService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(templateId, request, ct)))
            .RequirePermission(PrintingPermissions.TemplateManage)
            .WithSummary("Edits the template: new HTML or CSS becomes a new version; name, language mode, paper, scope, default and active change in place");
        templates.MapPost("/{templateId:guid}/copy", async (Guid templateId, CopyPrintTemplateRequest request, TemplateService service, CancellationToken ct) => ApiProblems.Created(await service.CopyAsync(templateId, request, ct), static t => $"/api/v1/print/templates/{t.Id}"))
            .RequirePermission(PrintingPermissions.TemplateManage)
            .WithSummary("A copy of the template for a company or a branch, from its current version");
        templates.MapGet("/{templateId:guid}/versions", async (Guid templateId, TemplateService service, CancellationToken ct) => TypedResults.Ok(await service.VersionsAsync(templateId, ct)))
            .RequirePermission(PrintingPermissions.TemplateRead)
            .WithSummary("The template's versions, newest first");
        templates.MapPost("/{templateId:guid}/rollback/{version:int}", async (Guid templateId, int version, TemplateService service, CancellationToken ct) => ApiProblems.Ok(await service.RollbackAsync(templateId, version, ct)))
            .RequirePermission(PrintingPermissions.TemplateManage)
            .WithSummary("Makes an earlier version current again, as a new version with its content");
        templates.MapGet("/{templateId:guid}/preview", async (Guid templateId, int? version, PrintService service, CancellationToken ct) =>
            ApiProblems.From(await service.PreviewTemplateAsync(templateId, version, false, ct), static p => Results.Content(p.Html, "text/html; charset=utf-8")))
            .Produces<string>(StatusCodes.Status200OK, "text/html")
            .RequirePermission(PrintingPermissions.TemplateRead)
            .WithSummary("The template rendered with its sample data, as HTML");

        print.MapGet("/preview", async (string documentType, Guid? documentId, string? variant, Guid? templateId, Guid? companyId, Guid? partnerId, DateOnly? from, DateOnly? to, PrintService service, CancellationToken ct) =>
            ApiProblems.From(await service.PreviewAsync(new PrintRequest(documentType, documentId, Parameters(companyId, partnerId, from, to), variant ?? "standard", templateId), ct), static p => Results.Content(p.Html, "text/html; charset=utf-8")))
            .Produces<string>(StatusCodes.Status200OK, "text/html")
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("The document rendered as HTML for the browser's print preview: the same template the PDF uses; a draft invoice is refused as a tax invoice (print it pro forma)");

        var renders = print.MapGroup("/renders");
        renders.MapGet("/", async (string documentType, Guid? documentId, PrintService service, CancellationToken ct) => TypedResults.Ok(await service.ListRendersAsync(documentType, documentId, ct)))
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("The renders of a document, newest first, with their status and whether an evidence copy is kept");
        renders.MapPost("/", async (PrintRequest request, PrintService service, CancellationToken ct) => ApiProblems.Created(await service.RequestAsync(request, ct), static r => $"/api/v1/print/renders/{r.Id}"))
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("Asks for the PDF: an identical render already made is reused, otherwise the worker renders it (poll the render until ready, then fetch its file)");
        renders.MapGet("/{renderId:guid}", async (Guid renderId, PrintService service, CancellationToken ct) => ApiProblems.Ok(await service.GetRenderAsync(renderId, ct)))
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("The render's status, size, hash and error");
        renders.MapGet("/{renderId:guid}/file", async (Guid renderId, PrintService service, CancellationToken ct) =>
            ApiProblems.From(await service.OpenAsync(renderId, ct), static f => Results.Stream(f.Content, f.ContentType, f.FileName)))
            .Produces<Stream>(StatusCodes.Status200OK, "application/pdf")
            .RequirePermission(PrintingPermissions.Render)
            .WithSummary("The PDF of a ready render");

        var emails = print.MapGroup("/emails");
        emails.MapGet("/", async (string documentType, Guid? documentId, PrintService service, CancellationToken ct) => TypedResults.Ok(await service.ListEmailsAsync(documentType, documentId, ct)))
            .RequirePermission(PrintingPermissions.Email)
            .WithSummary("The emails sent for a document, newest first");
        emails.MapPost("/", async (PrintEmailRequest request, PrintService service, CancellationToken ct) => ApiProblems.Created(await service.EmailAsync(request, ct), static e => $"/api/v1/print/emails/{e.Id}"))
            .RequirePermission(PrintingPermissions.Email)
            .WithSummary("Emails the document's PDF to the partner's address or the one given; the worker renders first when it must, and the send is logged on the document");

        var shares = print.MapGroup("/shares");
        shares.MapGet("/", async (string documentType, Guid? documentId, ShareService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(documentType, documentId, ct)))
            .RequirePermission(PrintingPermissions.Share)
            .WithSummary("The share links made for a document, newest first, with their openings; the URLs themselves are shown once, when made");
        shares.MapPost("/", async (PrintShareRequest request, HttpContext http, ShareService service, CancellationToken ct) =>
            ApiProblems.Created(await service.ShareAsync(request, $"{http.Request.Scheme}://{http.Request.Host}", ct), static s => $"/api/v1/print/shares/{s.Id}"))
            .RequirePermission(PrintingPermissions.Share)
            .WithSummary("A link anyone can open without signing in, to a ready render, for WhatsApp or a plain link: it expires (7 days unless said otherwise, 30 at most) and the share is logged on the document");
        shares.MapPost("/{shareId:guid}/revoke", async (Guid shareId, ShareService service, CancellationToken ct) => ApiProblems.Ok(await service.RevokeAsync(shareId, ct)))
            .RequirePermission(PrintingPermissions.Share)
            .WithSummary("Stops a link working from now on");

        // The public side of share links: no sign-in; the token names the tenant and the hash of it the link.
        api.MapGroup("/public/prints").WithTags("Public links")
            .MapGet("/{token}", async (string token, HttpContext http, ShareService service, CancellationToken ct) =>
                ApiProblems.From(await service.OpenAsync(token, ct), f =>
                {
                    http.Response.Headers.ContentDisposition = $"inline; filename=\"{f.FileName}\"";
                    http.Response.Headers.CacheControl = "private, no-store";
                    return Results.Stream(f.Content, f.ContentType);
                }))
            .Produces<Stream>(StatusCodes.Status200OK, "application/pdf")
            .WithSummary("The PDF behind a share link, shown in the browser; expired, revoked and unknown links are refused");

        return api;
    }

    private static Dictionary<string, string>? Parameters(Guid? companyId, Guid? partnerId, DateOnly? from, DateOnly? to)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (companyId is { } c)
        {
            parameters["companyId"] = c.ToString();
        }

        if (partnerId is { } p)
        {
            parameters["partnerId"] = p.ToString();
        }

        if (from is { } f)
        {
            parameters["from"] = f.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (to is { } t)
        {
            parameters["to"] = t.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        return parameters.Count == 0 ? null : parameters;
    }
}

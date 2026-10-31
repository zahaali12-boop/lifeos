using Quicker.Identity.Contracts;

namespace Quicker.Printing;

/// <summary>Permission keys of the print pipeline (roadmap 5.9). Printing a document also needs the document's own
/// read permission, which its provider checks; the keys here cover the act of rendering and emailing and the templates.</summary>
public static class PrintingPermissions
{
    public const string TemplateRead = "print.template.read";
    public const string TemplateManage = "print.template.manage";
    public const string Render = "print.document.render";
    public const string Email = "print.document.email";
    public const string Share = "print.document.share";

    public static readonly PermissionDefinition[] All =
    [
        new(TemplateRead, "print", "Read print templates and their versions"),
        new(TemplateManage, "print", "Edit print templates (every edit is a new version), roll back and choose defaults"),
        new(Render, "print", "Render documents to PDF and read the renders"),
        new(Email, "print", "Email rendered documents"),
        new(Share, "print", "Share rendered documents by link (WhatsApp) and revoke the links"),
    ];
}

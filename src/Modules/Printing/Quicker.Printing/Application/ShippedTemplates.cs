using System.Reflection;
using Quicker.Kernel.Text;

namespace Quicker.Printing.Application;

/// <summary>A template the product ships: copied into a tenant the first time its document type prints, then the tenant's own to edit.</summary>
public sealed record ShippedTemplate(string DocumentType, string Variant, string Code, LocalizedText Name, string LanguageMode, string File, string SampleFile);

/// <summary>The shipped templates, their base stylesheet, sample data and the fonts they embed (DejaVu Sans, Bitstream Vera licence, Arabic and Latin in one face).</summary>
public static class ShippedTemplates
{
    public static readonly IReadOnlyList<ShippedTemplate> All =
    [
        new("sales_invoice", "standard", "SALES-INVOICE", LocalizedText.Bilingual("Tax invoice", "فاتورة ضريبية"), LanguageModes.Bilingual, "sales_invoice.html", "sample.sales_invoice.json"),
        new("sales_invoice", "proforma", "SALES-INVOICE-PROFORMA", LocalizedText.Bilingual("Pro forma invoice", "فاتورة أولية"), LanguageModes.Bilingual, "sales_invoice.html", "sample.sales_invoice.json"),
        new("sales_order", "standard", "SALES-ORDER", LocalizedText.Bilingual("Order confirmation", "تأكيد الطلب"), LanguageModes.Bilingual, "sales_order.html", "sample.sales_order.json"),
        new("customer_statement", "standard", "CUSTOMER-STATEMENT", LocalizedText.Bilingual("Statement of account", "كشف حساب"), LanguageModes.Bilingual, "customer_statement.html", "sample.customer_statement.json"),
        new("pos_order", "standard", "POS-RECEIPT", LocalizedText.Bilingual("Till receipt", "إيصال نقطة البيع"), LanguageModes.Bilingual, "pos_receipt.html", "sample.pos_receipt.json"),
    ];

    public static readonly IReadOnlyList<string> FontFiles = ["DejaVuSans.ttf", "DejaVuSans-Bold.ttf"];

    private static readonly Lazy<string> BaseStylesheet = new(static () => Load("templates.base.css"));

    public static string BaseCss => BaseStylesheet.Value;

    public static IEnumerable<ShippedTemplate> For(string documentType) => All.Where(t => string.Equals(t.DocumentType, documentType, StringComparison.Ordinal));

    public static string Html(ShippedTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Load("templates." + template.File);
    }

    public static string Sample(ShippedTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Load("templates." + template.SampleFile);
    }

    public static byte[] Font(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("fonts." + file) ?? throw new InvalidOperationException($"Font {file} is not embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Resource {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

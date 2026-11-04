using System.Reflection;
using Quicker.Kernel.Text;

namespace Quicker.Printing.Application;

/// <summary>A template the product ships: copied into a tenant the first time its document type prints, then the tenant's own to edit.</summary>
public sealed record ShippedTemplate(string DocumentType, string Variant, string Code, LocalizedText Name, string LanguageMode, string File, string SampleFile);

/// <summary>
/// The shipped templates, their base stylesheet, sample data and the fonts they embed: DejaVu Sans (Bitstream Vera
/// licence; Arabic and Latin in one face) for the standard layouts, and for the branded ones (T12b) Montserrat for the
/// Latin and Cairo for the Arabic, both variable and under the SIL Open Font License.
/// </summary>
public static class ShippedTemplates
{
    public static readonly IReadOnlyList<ShippedTemplate> All =
    [
        new("sales_invoice", "standard", "SALES-INVOICE", LocalizedText.Bilingual("Tax invoice", "فاتورة ضريبية"), LanguageModes.Bilingual, "sales_invoice.html", "sample.sales_invoice.json"),
        new("sales_invoice", "proforma", "SALES-INVOICE-PROFORMA", LocalizedText.Bilingual("Pro forma invoice", "فاتورة أولية"), LanguageModes.Bilingual, "sales_invoice.html", "sample.sales_invoice.json"),
        new("sales_invoice", "brand", "SALES-INVOICE-BRAND", LocalizedText.Bilingual("Branded invoice", "فاتورة بهوية الشركة"), LanguageModes.Bilingual, "sales_invoice_brand.html", "sample.sales_invoice.json"),
        new("sales_order", "standard", "SALES-ORDER", LocalizedText.Bilingual("Order confirmation", "تأكيد الطلب"), LanguageModes.Bilingual, "sales_order.html", "sample.sales_order.json"),
        new("customer_statement", "standard", "CUSTOMER-STATEMENT", LocalizedText.Bilingual("Statement of account", "كشف حساب"), LanguageModes.Bilingual, "customer_statement.html", "sample.customer_statement.json"),
        new("customer_statement", "brand", "CUSTOMER-STATEMENT-BRAND", LocalizedText.Bilingual("Branded statement", "كشف حساب بهوية الشركة"), LanguageModes.Bilingual, "customer_statement_brand.html", "sample.customer_statement.json"),
        new("bank_receipt", "standard", "RECEIPT", LocalizedText.Bilingual("Payment receipt", "سند قبض"), LanguageModes.Bilingual, "bank_receipt.html", "sample.bank_receipt.json"),
        new("bank_receipt", "brand", "RECEIPT-BRAND", LocalizedText.Bilingual("Branded receipt", "سند قبض بهوية الشركة"), LanguageModes.Bilingual, "bank_receipt_brand.html", "sample.bank_receipt.json"),
        new("pos_order", "standard", "POS-RECEIPT", LocalizedText.Bilingual("Till receipt", "إيصال نقطة البيع"), LanguageModes.Bilingual, "pos_receipt.html", "sample.pos_receipt.json"),
        new("payslip", "standard", "PAYSLIP", LocalizedText.Bilingual("Payslip", "قسيمة راتب"), LanguageModes.Bilingual, "payslip.html", "sample.payslip.json"),
        new("report", "standard", "REPORT", LocalizedText.Bilingual("Report", "تقرير"), LanguageModes.Bilingual, "report.html", "sample.report.json"),
        new("financial_statement", "standard", "FINANCIAL-STATEMENT", LocalizedText.Bilingual("Financial statement", "قائمة مالية"), LanguageModes.Bilingual, "financial_statement.html", "sample.financial_statement.json"),
    ];

    public static readonly IReadOnlyList<string> FontFiles = ["DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "Montserrat-Variable.ttf", "Cairo-Variable.ttf"];

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

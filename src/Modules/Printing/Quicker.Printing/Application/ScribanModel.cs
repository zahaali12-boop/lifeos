using System.Collections;
using System.Globalization;
using System.Text.Json;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

#pragma warning disable MA0002 // Scriban script objects are case-sensitive ordinal maps by design.

namespace Quicker.Printing.Application;

/// <summary>The three ways a template reads: English, Arabic, or both side by side (ADR-0022).</summary>
public static class LanguageModes
{
    public const string English = "en";
    public const string Arabic = "ar";
    public const string Bilingual = "bilingual";
    public static readonly IReadOnlyList<string> All = [English, Arabic, Bilingual];
}

/// <summary>Text and numbers as the shipped templates print them: one spelling per language mode, invariant digits (A-164).</summary>
public static class PrintFormat
{
    public static string Resolve(LocalizedText text, string mode)
    {
        ArgumentNullException.ThrowIfNull(text);
        var en = text.Resolve("en");
        var ar = text.Resolve("ar");
        return mode switch
        {
            LanguageModes.English => en,
            LanguageModes.Arabic => ar,
            _ => string.Equals(en, ar, StringComparison.Ordinal) || string.IsNullOrEmpty(ar) ? en : string.IsNullOrEmpty(en) ? ar : $"{en} / {ar}",
        };
    }

    public static string Resolve(IReadOnlyDictionary<string, string> values, string mode) => Resolve(new LocalizedText(values), mode);

    public static string Money(decimal amount, int minorUnits) => amount.ToString("N" + Math.Clamp(minorUnits, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static string Quantity(decimal quantity) => quantity.ToString("#,##0.###", CultureInfo.InvariantCulture);

    public static string Percent(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Rate(decimal rate) => rate.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string DateTime(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>The words the shipped templates print, in both languages; a template reads them as <c>L.total</c> in its language mode.</summary>
public static class PrintLabels
{
    public static readonly IReadOnlyDictionary<string, (string En, string Ar)> Table = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["tax_invoice"] = ("Tax invoice", "فاتورة ضريبية"),
        ["invoice"] = ("Invoice", "فاتورة"),
        ["proforma_invoice"] = ("Pro forma invoice", "فاتورة أولية"),
        ["credit_note"] = ("Credit note", "إشعار دائن"),
        ["sales_order"] = ("Sales order", "أمر بيع"),
        ["order_confirmation"] = ("Order confirmation", "تأكيد الطلب"),
        ["statement"] = ("Statement of account", "كشف حساب"),
        ["number"] = ("Number", "الرقم"),
        ["date"] = ("Date", "التاريخ"),
        ["document_date"] = ("Document date", "تاريخ المستند"),
        ["posting_date"] = ("Posting date", "تاريخ الترحيل"),
        ["due_date"] = ("Due date", "تاريخ الاستحقاق"),
        ["order_date"] = ("Order date", "تاريخ الطلب"),
        ["customer"] = ("Customer", "العميل"),
        ["bill_to"] = ("Bill to", "فاتورة إلى"),
        ["seller"] = ("Seller", "البائع"),
        ["tax_number"] = ("Tax registration number", "الرقم الضريبي"),
        ["registration"] = ("Registration", "رقم التسجيل"),
        ["address"] = ("Address", "العنوان"),
        ["email"] = ("Email", "البريد الإلكتروني"),
        ["your_reference"] = ("Your reference", "مرجعكم"),
        ["credits_invoice"] = ("Credits invoice", "يُعدِّل الفاتورة"),
        ["payment_terms"] = ("Payment terms", "شروط الدفع"),
        ["currency"] = ("Currency", "العملة"),
        ["exchange_rate"] = ("Exchange rate", "سعر الصرف"),
        ["status"] = ("Status", "الحالة"),
        ["line"] = ("#", "#"),
        ["item"] = ("Item", "الصنف"),
        ["description"] = ("Description", "الوصف"),
        ["quantity"] = ("Quantity", "الكمية"),
        ["unit"] = ("Unit", "الوحدة"),
        ["unit_price"] = ("Unit price", "سعر الوحدة"),
        ["discount"] = ("Discount", "الخصم"),
        ["net"] = ("Net", "الصافي"),
        ["tax"] = ("Tax", "الضريبة"),
        ["tax_rate"] = ("Rate", "النسبة"),
        ["taxable_amount"] = ("Taxable amount", "المبلغ الخاضع"),
        ["gross"] = ("Total", "الإجمالي"),
        ["total_net"] = ("Total before tax", "الإجمالي قبل الضريبة"),
        ["total_discount"] = ("Total discount", "إجمالي الخصم"),
        ["total_tax"] = ("Total tax", "إجمالي الضريبة"),
        ["total_gross"] = ("Total due", "الإجمالي المستحق"),
        ["total"] = ("Total", "الإجمالي"),
        ["amount_in_words"] = ("Amount in words", "المبلغ كتابةً"),
        ["tax_summary"] = ("Tax summary", "ملخص الضريبة"),
        ["instalments"] = ("Payment schedule", "جدول السداد"),
        ["instalment"] = ("Instalment", "القسط"),
        ["amount"] = ("Amount", "المبلغ"),
        ["remaining"] = ("Open", "المتبقي"),
        ["notes"] = ("Notes", "ملاحظات"),
        ["page"] = ("Page", "صفحة"),
        ["of"] = ("of", "من"),
        ["generated"] = ("Printed", "طُبع في"),
        ["draft_watermark"] = ("DRAFT — not a tax invoice", "مسودة — ليست فاتورة ضريبية"),
        ["proforma_watermark"] = ("PRO FORMA — not a tax invoice", "فاتورة أولية — ليست فاتورة ضريبية"),
        ["cancelled_watermark"] = ("CANCELLED", "ملغاة"),
        ["reverse_charge"] = ("Reverse charge: tax to be accounted for by the customer.", "احتساب عكسي: يتحمل العميل احتساب الضريبة."),
        ["prices_include_tax"] = ("Prices include tax.", "الأسعار شاملة الضريبة."),
        ["period"] = ("Period", "الفترة"),
        ["from"] = ("From", "من"),
        ["to"] = ("To", "إلى"),
        ["opening_balance"] = ("Opening balance", "الرصيد الافتتاحي"),
        ["closing_balance"] = ("Closing balance", "الرصيد الختامي"),
        ["document"] = ("Document", "المستند"),
        ["debit"] = ("Debit", "مدين"),
        ["credit"] = ("Credit", "دائن"),
        ["balance"] = ("Balance", "الرصيد"),
        ["reversal"] = ("Reversal", "عكس"),
        ["shipped"] = ("Shipped", "المشحون"),
        ["drop_ship"] = ("Drop-ship", "شحن مباشر"),
        ["thank_you"] = ("Thank you for your business.", "شكرًا لتعاملكم معنا."),
        ["qr_hint"] = ("Scan to verify", "امسح للتحقق"),
        ["register"] = ("Register", "الصندوق"),
        ["cashier"] = ("Cashier", "أمين الصندوق"),
        ["refunds"] = ("Refund of", "استرجاع عن"),
        ["rounding"] = ("Rounding", "التقريب"),
        ["change"] = ("Change", "الباقي"),
        ["pay_period"] = ("Pay period", "فترة الراتب"),
        ["pay_date"] = ("Pay date", "تاريخ الدفع"),
        ["employee"] = ("Employee", "الموظف"),
        ["employee_number"] = ("Employee number", "رقم الموظف"),
        ["job_title"] = ("Job title", "المسمى الوظيفي"),
        ["department"] = ("Department", "القسم"),
        ["hire_date"] = ("Hire date", "تاريخ التعيين"),
        ["days_paid"] = ("Days paid", "الأيام المدفوعة"),
        ["unpaid_days"] = ("Unpaid leave days", "أيام الإجازة بدون راتب"),
        ["basic_salary"] = ("Basic salary", "الراتب الأساسي"),
        ["earnings"] = ("Earnings", "الاستحقاقات"),
        ["deductions"] = ("Deductions", "الاستقطاعات"),
        ["total_deductions"] = ("Total deductions", "إجمالي الاستقطاعات"),
        ["basis"] = ("Basis", "الأساس"),
        ["rate"] = ("Rate", "النسبة"),
        ["net_pay"] = ("Net pay", "صافي الراتب"),
        ["employer_contributions"] = ("Employer contributions", "مساهمات صاحب العمل"),
        ["employer_contributions_note"] = ("Paid by the employer on top of the gross; not deducted from the pay.", "يدفعها صاحب العمل فوق الإجمالي؛ لا تُخصم من الراتب."),
        ["payment_method"] = ("Payment", "طريقة الدفع"),
        ["paid_on"] = ("Paid on", "تاريخ الصرف"),
    };

    public static ScriptObject For(string mode)
    {
        var labels = new ScriptObject();
        foreach (var (key, (en, ar)) in Table)
        {
            labels[key] = PrintFormat.Resolve(LocalizedText.Bilingual(en, ar), mode);
        }

        return labels;
    }
}

/// <summary>Turns a print model into what Scriban reads, and renders a template with it; errors in a template are reported as validation errors, never thrown at the user.</summary>
public static class ScribanModel
{
    public static object? ToScript(object? value, string mode) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        int or long or short or byte or decimal => value,
        DateOnly d => PrintFormat.Date(d),
        DateTimeOffset at => PrintFormat.DateTime(at),
        DateTime at => PrintFormat.DateTime(new DateTimeOffset(at, TimeSpan.Zero)),
        Guid id => id.ToString(),
        LocalizedText text => PrintFormat.Resolve(text, mode),
        JsonElement json => FromJson(json, mode),
        IReadOnlyDictionary<string, string> strings => Strings(strings),
        IReadOnlyDictionary<string, object?> dictionary => Dictionary(dictionary, mode),
        IDictionary generic => GenericDictionary(generic, mode),
        IEnumerable enumerable => Array(enumerable, mode),
        _ => value.ToString(),
    };

    public static ScriptObject Dictionary(IReadOnlyDictionary<string, object?> dictionary, string mode)
    {
        var result = new ScriptObject();
        foreach (var (key, item) in dictionary)
        {
            result[key] = ToScript(item, mode);
        }

        return result;
    }

    public static ScriptObject Strings(IReadOnlyDictionary<string, string> strings)
    {
        var result = new ScriptObject();
        foreach (var (key, item) in strings)
        {
            result[key] = item;
        }

        return result;
    }

    /// <summary>Renders the template; a template that does not parse is a validation error naming its messages.</summary>
    public static Result<string> Render(string html, ScriptObject globals)
    {
        var template = Template.Parse(html);
        if (template.HasErrors)
        {
            return Error.Validation("template.invalid", "The template does not parse.").WithWhy(("messages", template.Messages.Select(static m => m.ToString()).ToList()));
        }

        var context = new TemplateContext { MemberRenamer = static member => member.Name, LoopLimit = 20_000, EnableRelaxedMemberAccess = true };
        context.PushGlobal(globals);
        try
        {
            return template.Render(context);
        }
        catch (ScriptRuntimeException ex)
        {
            return Error.Validation("template.runtime_error", "The template failed while rendering.").WithWhy(("message", ex.Message));
        }
    }

    private static ScriptObject GenericDictionary(IDictionary generic, string mode)
    {
        var result = new ScriptObject();
        foreach (DictionaryEntry entry in generic)
        {
            result[entry.Key.ToString() ?? string.Empty] = ToScript(entry.Value, mode);
        }

        return result;
    }

    private static ScriptArray Array(IEnumerable enumerable, string mode)
    {
        var result = new ScriptArray();
        foreach (var item in enumerable)
        {
            result.Add(ToScript(item, mode));
        }

        return result;
    }

    private static object? FromJson(JsonElement json, string mode) => json.ValueKind switch
    {
        JsonValueKind.Object => JsonObject(json, mode),
        JsonValueKind.Array => JsonArray(json, mode),
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.TryGetDecimal(out var number) ? number : json.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static ScriptObject JsonObject(JsonElement json, string mode)
    {
        var result = new ScriptObject();
        foreach (var property in json.EnumerateObject())
        {
            result[property.Name] = FromJson(property.Value, mode);
        }

        return result;
    }

    private static ScriptArray JsonArray(JsonElement json, string mode)
    {
        var result = new ScriptArray();
        foreach (var item in json.EnumerateArray())
        {
            result.Add(FromJson(item, mode));
        }

        return result;
    }
}

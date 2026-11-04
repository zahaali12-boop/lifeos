using Quicker.Kernel.Results;
using Quicker.Web.Imports;

namespace Quicker.Partners.Application;

/// <summary>
/// The import format of customers and suppliers (setup wizard, A-173): one row per partner with the roles as
/// yes/no columns and, when a company is named, the company account's terms. Columns: code, name_en, name_ar,
/// trade_name_en, trade_name_ar, kind, customer, supplier, email, phone, website, language, currency, payment_terms,
/// credit_limit, customer_group, supplier_group, notes, is_active. An Excel workbook with the same columns reads
/// the same way.
/// </summary>
public static class PartnerCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "code", "name_en", "name_ar", "trade_name_en", "trade_name_ar", "kind", "customer", "supplier", "email", "phone", "website", "language",
        "currency", "payment_terms", "credit_limit", "customer_group", "supplier_group", "notes", "is_active",
    ];

    public static Result<IReadOnlyList<PartnerImportRow>> Read(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var rows = new List<PartnerImportRow>(sheet.Rows.Count);
        foreach (var row in sheet.Rows)
        {
            var legalName = Names(row.Get("name_en"), row.Get("name_ar"));
            var tradeName = Names(row.Get("trade_name_en"), row.Get("trade_name_ar"));
            var creditLimit = row.Decimal("credit_limit");
            if (creditLimit.IsFailure)
            {
                return creditLimit.Error!;
            }

            var partner = new SavePartnerRequest(row.Get("code") ?? string.Empty, legalName, tradeName.Count == 0 ? null : tradeName, row.Get("kind") ?? "organization",
                row.Bool("supplier"), row.Bool("customer"), false, null, row.Get("language") ?? "en", row.Get("website"), row.Get("email"), row.Get("phone"), null, row.Get("notes"), null, row.Bool("is_active", true));
            rows.Add(new PartnerImportRow(partner, row.Get("currency"), row.Get("payment_terms"), creditLimit.Value, row.Get("customer_group"), row.Get("supplier_group")));
        }

        return rows;
    }

    private static Dictionary<string, string> Names(string? en, string? ar)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (en is not null)
        {
            names["en"] = en;
        }

        if (ar is not null)
        {
            names["ar"] = ar;
        }

        return names;
    }
}

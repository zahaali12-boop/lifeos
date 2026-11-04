using System.Globalization;
using System.Text;
using Quicker.Web.Imports;

namespace Quicker.Items.Application;

/// <summary>
/// The exchange format of the item master: one row per item with its units and barcodes packed into two columns
/// (<c>uoms</c>: "CTN:24/1;DZ:12/1", <c>barcodes</c>: "PCS=6291041500213|CTN=16291041500210|CTN=...:CODE128"), so a
/// spreadsheet round-trips. Booleans are true/false, empty means null, quotes and commas follow RFC 4180; an Excel
/// workbook with the same columns reads the same way.
/// </summary>
public static class ItemCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "code", "name_en", "name_ar", "description_en", "description_ar", "type", "category", "brand", "base_uom", "sales_uom", "purchase_uom", "uoms", "barcodes",
        "tracking", "expiry_required", "shelf_life_days", "fefo", "list_price", "list_price_currency", "weight_kg", "volume_m3", "hs_code", "country_of_origin", "is_active",
    ];

    public static string Write(IEnumerable<ItemSummary> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns)).Append('\n');
        foreach (var i in items)
        {
            var uoms = string.Join(';', (i.Uoms ?? []).Where(static u => !u.IsBase).Select(static u => $"{u.UomCode}:{N(u.Numerator)}/{N(u.Denominator)}"));
            var barcodes = string.Join('|', (i.Uoms ?? []).SelectMany(static u => u.Barcodes.Select(b => $"{u.UomCode}={b.Barcode}{(b.Symbology == "EAN13" ? string.Empty : ":" + b.Symbology)}")));
            sb.Append(string.Join(',', new[]
            {
                Q(i.Code), Q(i.Name.GetValueOrDefault("en")), Q(i.Name.GetValueOrDefault("ar")), Q(i.Description.GetValueOrDefault("en")), Q(i.Description.GetValueOrDefault("ar")), Q(i.Type), Q(i.CategoryCode), Q(i.BrandCode),
                Q(i.BaseUom), Q(i.SalesUom), Q(i.PurchaseUom), Q(uoms), Q(barcodes), Q(i.Tracking), B(i.ExpiryRequired), Q(i.ShelfLifeDays?.ToString(CultureInfo.InvariantCulture)), B(i.Fefo),
                Q(i.ListPrice is { } p ? N(p) : null), Q(i.ListPriceCurrency), Q(i.WeightKg is { } w ? N(w) : null), Q(i.VolumeM3 is { } v ? N(v) : null), Q(i.HsCode), Q(i.CountryOfOrigin), B(i.IsActive),
            })).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses the CSV into save requests; the first line names the columns (any order, unknown columns ignored).</summary>
    public static IReadOnlyList<SaveItemRequest> Read(string csv) => Read(Sheet.FromCsv(csv));

    /// <summary>The rows of a sheet (CSV or Excel) as save requests, blank rows skipped.</summary>
    public static IReadOnlyList<SaveItemRequest> Read(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var result = new List<SaveItemRequest>(sheet.Rows.Count);
        foreach (var row in sheet.Rows)
        {
            var name = new Dictionary<string, string>(StringComparer.Ordinal);
            if (row.Get("name_en") is { } en)
            {
                name["en"] = en;
            }

            if (row.Get("name_ar") is { } ar)
            {
                name["ar"] = ar;
            }

            var description = new Dictionary<string, string>(StringComparer.Ordinal);
            if (row.Get("description_en") is { } den)
            {
                description["en"] = den;
            }

            if (row.Get("description_ar") is { } dar)
            {
                description["ar"] = dar;
            }

            var uoms = new List<SaveItemUomRequest>();
            foreach (var part in (row.Get("uoms") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                var code = colon < 0 ? part : part[..colon];
                var factor = colon < 0 ? "1/1" : part[(colon + 1)..];
                var slash = factor.IndexOf('/', StringComparison.Ordinal);
                var numerator = D(slash < 0 ? factor : factor[..slash]) ?? 0m;
                var denominator = slash < 0 ? 1m : D(factor[(slash + 1)..]) ?? 0m;
                uoms.Add(new SaveItemUomRequest(code, null, numerator, denominator));
            }

            var barcodes = new List<SaveBarcodeRequest>();
            foreach (var part in (row.Get("barcodes") ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                var uom = eq < 0 ? null : part[..eq];
                var rest = eq < 0 ? part : part[(eq + 1)..];
                var colon = rest.IndexOf(':', StringComparison.Ordinal);
                var barcode = colon < 0 ? rest : rest[..colon];
                var symbology = colon < 0 ? "EAN13" : rest[(colon + 1)..];
                barcodes.Add(new SaveBarcodeRequest(barcode, uom, null, symbology));
            }

            result.Add(new SaveItemRequest(
                row.Get("code") ?? string.Empty, name, row.Get("type") ?? "stock", row.Get("base_uom"), null, description.Count == 0 ? null : description, row.Get("category"), null, row.Get("brand"), null,
                row.Get("sales_uom"), row.Get("purchase_uom"), row.Get("tracking") ?? "none", row.Bool("expiry_required"), I(row.Get("shelf_life_days")), row.Bool("fefo"), null, null, null,
                D(row.Get("list_price")), row.Get("list_price_currency"), D(row.Get("weight_kg")), D(row.Get("volume_m3")), row.Get("hs_code"), row.Get("country_of_origin"), row.Bool("is_active", true), null,
                uoms.Count == 0 ? null : uoms, barcodes.Count == 0 ? null : barcodes));
        }

        return result;
    }

    private static string N(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static decimal? D(string? value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? I(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static string B(bool value) => value ? "true" : "false";

    private static string Q(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

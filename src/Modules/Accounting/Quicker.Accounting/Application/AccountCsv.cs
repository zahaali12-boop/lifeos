using System.Text;

namespace Quicker.Accounting.Application;

/// <summary>
/// The exchange format of a chart: one row per account, columns fixed and documented in the header line so a
/// spreadsheet round-trips. Booleans are true/false, empty means null, quotes and commas follow RFC 4180.
/// </summary>
public static class AccountCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "code", "parent_code", "name_en", "name_ar", "type", "subtype", "category", "is_header", "is_control", "subledger_type",
        "currency", "allow_manual_posting", "revalue_fx", "cash_flow_category", "default_role", "is_active",
    ];

    public static string Write(IEnumerable<AccountSummary> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns)).Append('\n');
        foreach (var a in accounts)
        {
            sb.Append(string.Join(',', new[]
            {
                Q(a.Code), Q(a.ParentCode), Q(a.Name.GetValueOrDefault("en")), Q(a.Name.GetValueOrDefault("ar")), Q(a.Type), Q(a.Subtype), Q(a.CategoryCode),
                B(a.IsHeader), B(a.IsControl), Q(a.SubledgerType), Q(a.CurrencyRestriction), B(a.AllowManualPosting), B(a.RevalueFx), Q(a.CashFlowCategory), Q(a.DefaultRole), B(a.IsActive),
            })).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses the CSV into save requests; the first line names the columns (any order, unknown columns ignored).</summary>
    public static IReadOnlyList<SaveAccountRequest> Read(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = ParseRows(csv);
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0].Select(static h => h.Trim().ToLowerInvariant()).ToList();
        var result = new List<SaveAccountRequest>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            string? Get(string column)
            {
                var index = header.IndexOf(column);
                return index >= 0 && index < row.Count && row[index].Length > 0 ? row[index] : null;
            }

            var name = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Get("name_en") is { } en)
            {
                name["en"] = en;
            }

            if (Get("name_ar") is { } ar)
            {
                name["ar"] = ar;
            }

            result.Add(new SaveAccountRequest(
                Get("code") ?? string.Empty,
                name,
                Get("type") ?? string.Empty,
                ParentCode: Get("parent_code"),
                Subtype: Get("subtype") ?? string.Empty,
                CategoryCode: Get("category"),
                IsHeader: Bool(Get("is_header"), false),
                IsControl: Bool(Get("is_control"), false),
                SubledgerType: Get("subledger_type"),
                CurrencyRestriction: Get("currency"),
                AllowManualPosting: Bool(Get("allow_manual_posting"), true),
                RevalueFx: Bool(Get("revalue_fx"), false),
                CashFlowCategory: Get("cash_flow_category"),
                DefaultRole: Get("default_role"),
                IsActive: Bool(Get("is_active"), true)));
        }

        return result;
    }

    private static bool Bool(string? value, bool fallback) => value is null ? fallback : value.Trim().ToUpperInvariant() is "TRUE" or "1" or "YES" or "Y";

    private static string B(bool value) => value ? "true" : "false";

    private static string Q(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

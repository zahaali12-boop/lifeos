using System.Globalization;
using Quicker.Kernel.Text;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Domain;
using Quicker.Web.Exports;

namespace Quicker.Reporting.Application.Schedules;

/// <summary>The CSV and XLSX files of a query result or a statement, written with the same writer the grids export through, headers in the reader's language.</summary>
public static class ReportFiles
{
    public static ExportFile FromQuery(QueryResult result, string format, string language, string baseName)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rtl = language.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        var columns = result.Fields.Select(f => new ExportColumn(Label(f.Label, language), CellType(f))).ToList();
        var rows = result.Rows.Select(row => (IReadOnlyList<object?>)result.Fields.Select(f => Cell(row.GetValueOrDefault(f.Key), f)).ToList());
        return TabularExport.Build(format, baseName, "Report", columns, rows, rtl);
    }

    public static ExportFile FromStatement(StatementResult statement, string format, string language, string baseName)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var rtl = language.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        var lineHeader = rtl ? "البند" : "Line";
        var columns = new List<ExportColumn> { new(rtl ? "الرمز" : "Code"), new(lineHeader) };
        columns.AddRange(statement.Columns.Select(c => new ExportColumn(Label(c.Label, language) + (c.From is { } f && c.To is { } t ? $" ({Iso(f)} – {Iso(t)})" : string.Empty), ExportCellType.Number)));
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var row in statement.Rows)
        {
            if (row.RowType == RowTypes.Blank)
            {
                continue;
            }

            var cells = new List<object?> { row.Code, new string(' ', row.Indent * 2) + Label(row.Label, language) };
            cells.AddRange(row.RowType == RowTypes.Header ? statement.Columns.Select(static _ => (object?)null) : row.Values.Select(static v => (object?)v));
            rows.Add(cells);
            foreach (var account in row.Accounts ?? [])
            {
                var line = new List<object?> { account.Code, new string(' ', (row.Indent + 1) * 2) + Label(account.Name, language) };
                line.AddRange(account.Values.Select(static v => (object?)v));
                rows.Add(line);
            }
        }

        return TabularExport.Build(format, baseName, "Statement", columns, rows, rtl);
    }

    /// <summary>How many lines a statement carries a figure on: the rows and account lines with any value, so "only if rows" can judge an empty period.</summary>
    public static int FigureCount(StatementResult statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.Rows.Count(static r => r.RowType != RowTypes.Blank && r.RowType != RowTypes.Header && r.Values.Any(static v => v is not null && v != 0m));
    }

    public static string Label(IReadOnlyDictionary<string, string> label, string language) =>
        new LocalizedText(label).Resolve(language.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en");

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ExportCellType CellType(ResultField field) => field.Type switch
    {
        FieldTypes.Date => ExportCellType.Date,
        FieldTypes.Number or FieldTypes.Money or FieldTypes.Quantity or FieldTypes.Percent => ExportCellType.Number,
        _ => ExportCellType.Text,
    };

    private static object? Cell(object? value, ResultField field)
    {
        if (value is null)
        {
            return null;
        }

        if (field.Type == FieldTypes.Boolean)
        {
            return value is bool b ? (b ? "true" : "false") : value.ToString();
        }

        return value;
    }
}

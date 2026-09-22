using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Quicker.Web.Exports;

/// <summary>How the cells of an export column are typed: text, a real number cell, or a date cell.</summary>
public enum ExportCellType
{
    Text,
    Number,
    Date,
}

/// <summary>A column of a tabular export: its header and cell type.</summary>
public sealed record ExportColumn(string Header, ExportCellType Type = ExportCellType.Text);

/// <summary>A finished export: bytes, media type and the file name for the download.</summary>
public sealed record ExportFile(byte[] Bytes, string ContentType, string FileName);

/// <summary>
/// Tabular exports without a spreadsheet library: CSV (RFC 4180, UTF-8 with a byte order mark so spreadsheets open
/// Arabic text correctly) and XLSX (a minimal SpreadsheetML package with real number and date cells, bold headers
/// and a right-to-left sheet when asked). Numbers are written invariant; dates as ISO text in CSV and as date cells
/// in XLSX.
/// </summary>
public static class TabularExport
{
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] Formats = ["csv", "xlsx"];
    private static readonly DateOnly SerialEpoch = new(1899, 12, 30);

    public static bool IsKnownFormat(string? format) => format is not null && Array.Exists(Formats, f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));

    /// <summary>The file for a format ("csv" or "xlsx"); the caller validated the format with <see cref="IsKnownFormat"/>.</summary>
    public static ExportFile Build(string format, string baseName, string sheetName, IReadOnlyList<ExportColumn> columns, IEnumerable<IReadOnlyList<object?>> rows, bool rightToLeft = false)
    {
        ArgumentNullException.ThrowIfNull(format);
        return string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)
            ? new ExportFile(Xlsx(sheetName, columns, rows, rightToLeft), XlsxContentType, baseName + ".xlsx")
            : new ExportFile(Csv(columns, rows), CsvContentType, baseName + ".csv");
    }

    public static byte[] Csv(IReadOnlyList<ExportColumn> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', columns.Select(static c => CsvCell(c.Header)))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(CsvCell))).Append("\r\n");
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    public static byte[] Xlsx(string sheetName, IReadOnlyList<ExportColumn> columns, IEnumerable<IReadOnlyList<object?>> rows, bool rightToLeft = false)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes);
            Add(zip, "_rels/.rels", PackageRels);
            Add(zip, "xl/workbook.xml", Workbook(SafeSheetName(sheetName)));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
            Add(zip, "xl/styles.xml", Styles);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(columns, rows, rightToLeft));
        }

        return stream.ToArray();
    }

    private static string CsvCell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            decimal d => Number(d),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            Guid g => g.ToString("D"),
            _ => value.ToString() ?? string.Empty,
        };
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private static string Sheet(IReadOnlyList<ExportColumn> columns, IEnumerable<IReadOnlyList<object?>> rows, bool rightToLeft)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(rightToLeft ? " rightToLeft=\"1\"" : string.Empty).Append("/></sheetViews>");
        sb.Append("<cols>");
        for (var c = 0; c < columns.Count; c++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{(columns[c].Type == ExportCellType.Text ? 28 : 16)}\" customWidth=\"1\"/>");
        }

        sb.Append("</cols><sheetData>");
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < columns.Count; c++)
        {
            sb.Append("<c r=\"").Append(Reference(c, 1)).Append("\" s=\"3\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(columns[c].Header)).Append("</t></is></c>");
        }

        sb.Append("</row>");
        var r = 1;
        foreach (var row in rows)
        {
            r++;
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\">");
            for (var c = 0; c < columns.Count && c < row.Count; c++)
            {
                var value = row[c];
                if (value is null)
                {
                    continue;
                }

                var reference = Reference(c, r);
                switch (columns[c].Type)
                {
                    case ExportCellType.Number when value is decimal or int or long:
                        sb.Append("<c r=\"").Append(reference).Append("\" s=\"1\"><v>").Append(Number(Convert.ToDecimal(value, CultureInfo.InvariantCulture))).Append("</v></c>");
                        break;
                    case ExportCellType.Date when value is DateOnly date:
                        sb.Append("<c r=\"").Append(reference).Append("\" s=\"2\"><v>").Append((date.DayNumber - SerialEpoch.DayNumber).ToString(CultureInfo.InvariantCulture)).Append("</v></c>");
                        break;
                    default:
                        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(CsvText(value))).Append("</t></is></c>");
                        break;
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    /// <summary>Invariant, without the trailing zeros a numeric(24, 6) sum carries: 3000000.000000 → 3000000.</summary>
    private static string Number(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static string CsvText(object value) => value switch
    {
        decimal d => Number(d),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Guid g => g.ToString("D"),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>A1-style reference: column index 0 → A, 25 → Z, 26 → AA.</summary>
    private static string Reference(int column, int row)
    {
        var letters = new StringBuilder();
        var c = column;
        do
        {
            letters.Insert(0, (char)('A' + (c % 26)));
            c = (c / 26) - 1;
        }
        while (c >= 0);
        return letters.Append(row.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    private static string SafeSheetName(string name)
    {
        var cleaned = new string((name ?? "Sheet1").Where(static ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Sheet1";
        }

        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Workbook(string sheetName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\""
        + SecurityElement.Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
        + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
        + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";

    private const string PackageRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
        + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";

    // Cell styles: 0 default, 1 number (#,##0.00), 2 date (built-in 14), 3 bold header.
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
        + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
        + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
        + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
}

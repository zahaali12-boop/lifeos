using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Quicker.Web.Imports;

/// <summary>
/// Reads the first sheet of an Excel workbook (SpreadsheetML in a zip) into rows of text without a spreadsheet
/// library, the mirror of <see cref="Exports.TabularExport"/>: shared and inline strings as they are, booleans as
/// true/false, numbers invariant and without the scientific notation Excel stores large values in, and a cell whose
/// style is a date format as an ISO date. Empty cells are empty strings, so every row has a value under every column.
/// </summary>
public static class Xlsx
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly DateOnly SerialEpoch = new(1899, 12, 30);
    private static readonly HashSet<int> DateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58];
    private const int MaxCells = 2_000_000;

    public static IReadOnlyList<IReadOnlyList<string>> Rows(Stream workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        using var zip = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true);
        var sheetPath = FirstSheetPath(zip);
        var strings = SharedStrings(zip);
        var dateStyles = DateStyles(zip);
        var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException($"The workbook has no sheet at {sheetPath}.");
        var rows = new List<IReadOnlyList<string>>();
        var cells = 0;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        var width = 0;
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                reader.Read();
                continue;
            }

            // ReadFrom consumes the row element and leaves the reader on whatever follows it, so the loop must not step again.
            var row = new List<string>();
            var rowElement = (XElement)XNode.ReadFrom(reader);
            foreach (var cell in rowElement.Elements(Main + "c"))
            {
                if (++cells > MaxCells)
                {
                    throw new InvalidDataException("The sheet has more cells than an import can take.");
                }

                var column = ColumnIndex((string?)cell.Attribute("r"));
                while (row.Count < column)
                {
                    row.Add(string.Empty);
                }

                row.Add(CellText(cell, strings, dateStyles));
            }

            // A row of nothing but empty cells is a blank line in a spreadsheet: skipped, like a blank line of a CSV.
            if (row.TrueForAll(string.IsNullOrEmpty))
            {
                continue;
            }

            width = Math.Max(width, row.Count);
            rows.Add(row);
        }

        return rows.Select(r => r.Count == width ? r : (IReadOnlyList<string>)[.. r, .. Enumerable.Repeat(string.Empty, width - r.Count)]).ToList();
    }

    private static string CellText(XElement cell, IReadOnlyList<string> strings, HashSet<int> dateStyles)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Main + "t").Select(static t => t.Value));
        }

        var value = cell.Element(Main + "v")?.Value ?? string.Empty;
        switch (type)
        {
            case "s":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < strings.Count ? strings[index] : string.Empty;
            case "b":
                return value == "1" ? "true" : "false";
            case "str":
            case "e":
                return value;
            default:
                if (value.Length == 0)
                {
                    return string.Empty;
                }

                var style = (int?)cell.Attribute("s") ?? 0;
                if (dateStyles.Contains(style) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial >= 0m)
                {
                    // The serial's whole part is the day; the fraction is the time of day, which a date import does not keep.
                    return SerialEpoch.AddDays((int)serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : value;
        }
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var first = workbook?.Descendants(Main + "sheet").FirstOrDefault();
        var relId = (string?)first?.Attribute(Rel + "id");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var target = rels?.Descendants(Package + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target")?.Value;
        if (target is null)
        {
            return "xl/worksheets/sheet1.xml";
        }

        return target.StartsWith('/') ? target[1..] : "xl/" + target;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var document = Load(zip, "xl/sharedStrings.xml");
        return document is null ? [] : document.Root!.Elements(Main + "si").Select(static si => string.Concat(si.Descendants(Main + "t").Select(static t => t.Value))).ToList();
    }

    /// <summary>The cell style indexes whose number format is a date: Excel's built-in date formats, or a custom one made of day, month and year codes.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var styles = Load(zip, "xl/styles.xml");
        var result = new HashSet<int>();
        if (styles is null)
        {
            return result;
        }

        var custom = styles.Descendants(Main + "numFmt")
            .Where(static f => IsDateFormat((string?)f.Attribute("formatCode")))
            .Select(static f => (int?)f.Attribute("numFmtId") ?? -1)
            .ToHashSet();
        var index = 0;
        foreach (var xf in styles.Descendants(Main + "cellXfs").Elements(Main + "xf"))
        {
            var format = (int?)xf.Attribute("numFmtId") ?? 0;
            if (DateFormats.Contains(format) || custom.Contains(format))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    private static bool IsDateFormat(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        // Strip quoted literals and [colour] or [locale] sections, then look for day, month and year codes with no number signs.
        var bare = new StringBuilder();
        var inQuote = false;
        var inBracket = false;
        foreach (var ch in code)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && ch == '[')
            {
                inBracket = true;
            }
            else if (!inQuote && ch == ']')
            {
                inBracket = false;
            }
            else if (!inQuote && !inBracket)
            {
                bare.Append(char.ToLowerInvariant(ch));
            }
        }

        var text = bare.ToString();
        return text.Contains('y', StringComparison.Ordinal) && text.Contains('d', StringComparison.Ordinal) && !text.Contains('0', StringComparison.Ordinal) && !text.Contains('#', StringComparison.Ordinal);
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    /// <summary>The zero-based column of an A1 reference ("C7" → 2); a cell without one goes after the previous.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        var column = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch))
            {
                break;
            }

            column = (column * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return Math.Max(0, column - 1);
    }
}

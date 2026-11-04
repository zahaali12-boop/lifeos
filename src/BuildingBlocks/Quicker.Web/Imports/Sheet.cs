using System.Globalization;
using Microsoft.AspNetCore.Http;
using Quicker.Kernel.Results;

namespace Quicker.Web.Imports;

/// <summary>
/// A table read from an upload: the first row names the columns (any order, any case; unknown columns ignored), the
/// rest are records that read their cells by column name. CSV (text/csv) and Excel (.xlsx) uploads read the same
/// way, so every import takes either; a JSON body is left to the endpoint.
/// </summary>
public sealed class Sheet
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private Sheet(IReadOnlyList<string> header, IReadOnlyList<SheetRow> rows)
    {
        Header = header;
        Rows = rows;
    }

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<SheetRow> Rows { get; }

    public static bool IsCsv(string? contentType) => contentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsXlsx(string? contentType) => contentType?.StartsWith(XlsxContentType, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>True when the request carries a spreadsheet (CSV or Excel) rather than JSON.</summary>
    public static bool IsSpreadsheet(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsCsv(request.ContentType) || IsXlsx(request.ContentType);
    }

    public static Sheet FromCsv(string csv) => FromRows(Csv.Rows(csv));

    public static Sheet FromRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return new Sheet([], []);
        }

        var header = rows[0].Select(static h => h.Trim().ToLowerInvariant()).ToList();
        var records = new List<SheetRow>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            records.Add(new SheetRow(i, header, row));
        }

        return new Sheet(header, records);
    }

    /// <summary>The request's spreadsheet as a sheet; a failure names what was wrong with the file.</summary>
    public static async Task<Result<Sheet>> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (IsXlsx(request.ContentType))
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                return FromRows(Xlsx.Rows(buffer));
            }

            using var reader = new StreamReader(request.Body);
            return FromCsv(await reader.ReadToEndAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
        {
            return Error.Validation("import.file_invalid", "The file could not be read as a spreadsheet (.xlsx) or CSV.").WithWhy(("detail", ex.Message));
        }
    }
}

/// <summary>One record of a sheet: cells by column name, empty cells as null, with the row's number in the file for error messages.</summary>
public sealed class SheetRow(int number, IReadOnlyList<string> header, IReadOnlyList<string> cells)
{
    /// <summary>One-based row number in the file as a spreadsheet shows it (the header is row 1).</summary>
    public int Number { get; } = number + 1;

    public string? Get(string column)
    {
        var index = IndexOf(header, column);
        if (index < 0 || index >= cells.Count)
        {
            return null;
        }

        var value = cells[index].Trim();
        return value.Length > 0 ? value : null;
    }

    public string Require(string column) => Get(column) ?? string.Empty;

    /// <summary>A number in invariant or grouped form ("1,250.50"); null when empty, a failure when not a number.</summary>
    public Result<decimal?> Decimal(string column)
    {
        var text = Get(column);
        if (text is null)
        {
            return (decimal?)null;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : Error.Validation("import.number_invalid", $"Row {Number}: {column} must be a number.").WithWhy(("row", Number), ("column", column), ("value", text));
    }

    public Result<DateOnly?> Date(string column)
    {
        var text = Get(column);
        if (text is null)
        {
            return (DateOnly?)null;
        }

        return DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : Error.Validation("import.date_invalid", $"Row {Number}: {column} must be a date (yyyy-mm-dd).").WithWhy(("row", Number), ("column", column), ("value", text));
    }

    public bool Bool(string column, bool whenEmpty = false)
    {
        var text = Get(column);
        return text is null ? whenEmpty : text.ToLowerInvariant() is "true" or "1" or "yes" or "y" or "نعم";
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

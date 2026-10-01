using System.Globalization;
using System.Text;
using System.Text.Json;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Web;
using Quicker.Web.Exports;

namespace Quicker.Api;

/// <summary>A column of a table export: its header and how its cells are typed (text, number or date).</summary>
public sealed record TableExportColumn(string Header, string? Type = null);

/// <summary>
/// A table the web client already shows (a list's visible columns and rows, in its order), to be written as CSV or XLSX.
/// <c>documentType</c> names the kind of record listed, so a role that may not export that kind is refused.
/// </summary>
public sealed record TableExportRequest(string Format, string? Name, string? DocumentType, bool RightToLeft, IReadOnlyList<TableExportColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows);

/// <summary>Exports of what a list shows, formatted by the server so every list's CSV and Excel files look the same.</summary>
public static class ExportEndpoints
{
    public const int MaxRows = 50_000;
    public const int MaxColumns = 100;
    private const int MaxCellLength = 32_767;

    public static RouteGroupBuilder MapExportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/exports/table", async (TableExportRequest request, CurrentPrincipal current, IAuditSink audit, IClock clock, CancellationToken ct) =>
        {
            var principal = current.Required;
            if (!TabularExport.IsKnownFormat(request.Format))
            {
                return ApiProblems.From(Error.Validation("export.format_invalid", "The format is csv or xlsx."));
            }

            if (request.Columns is not { Count: > 0 } columns || columns.Count > MaxColumns || columns.Any(static c => string.IsNullOrWhiteSpace(c.Header) || c.Header.Length > 200 || c.Type is not (null or "text" or "number" or "date")))
            {
                return ApiProblems.From(Error.Validation("export.columns_invalid", $"Between 1 and {MaxColumns} columns, each with a header of up to 200 characters and a type of text, number or date."));
            }

            var rows = request.Rows ?? [];
            if (rows.Count > MaxRows)
            {
                return ApiProblems.From(Error.Validation("export.too_large", $"At most {MaxRows} rows can be exported at once; narrow the list first.").WithWhy(("max", MaxRows), ("rows", rows.Count)));
            }

            if (rows.Any(r => r is null || r.Count != columns.Count))
            {
                return ApiProblems.From(Error.Validation("export.row_invalid", "Every row has one value per column."));
            }

            var documentType = string.IsNullOrWhiteSpace(request.DocumentType) ? null : request.DocumentType.Trim();
            if (documentType is not null && !principal.MayActOnDocumentType(documentType, "export"))
            {
                return ApiProblems.From(Error.Forbidden("export.not_allowed", "Your role may not export these records.").WithWhy(("documentType", documentType)));
            }

            var types = columns.Select(static c => c.Type switch { "number" => ExportCellType.Number, "date" => ExportCellType.Date, _ => ExportCellType.Text }).ToList();
            var cells = rows.Select(r => (IReadOnlyList<object?>)r.Select((value, i) => Cell(value, types[i])).ToList()).ToList();
            var name = FileName(request.Name);
            var file = TabularExport.Build(request.Format, $"{name}-{clock.UtcNow:yyyyMMdd-HHmm}", name, columns.Select((c, i) => new ExportColumn(Clean(c.Header.Trim()), types[i])).ToList(), cells, request.RightToLeft);

            // Who took which records out, in which format: exports are part of the audit trail.
            await audit.RecordAsync(new AuditEntry(documentType ?? "list", Guid.CreateVersion7(), name, AuditActions.Exported,
                Details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["format"] = request.Format.ToLowerInvariant(), ["rows"] = rows.Count, ["columns"] = columns.Select(static c => c.Header).ToList() }), ct);
            return Results.File(file.Bytes, file.ContentType, file.FileName);
        })
        .RequireAuthorization()
        .Produces<Stream>(StatusCodes.Status200OK, "text/csv", TabularExport.XlsxContentType)
        .WithTags("Exports")
        .WithSummary("Writes the rows a list shows as CSV (UTF-8) or XLSX; checks the role may export the document type and records the export in the audit trail");
        return api;
    }

    private static object? Cell(JsonElement value, ExportCellType type) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : Clean(value.GetRawText()),
        JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
        JsonValueKind.String => StringCell(value.GetString() ?? string.Empty, type),
        _ => Clean(value.GetRawText()),
    };

    private static object StringCell(string text, ExportCellType type)
    {
        if (type == ExportCellType.Number && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        if (type == ExportCellType.Date && text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return Clean(text);
    }

    /// <summary>Control characters are not allowed in a spreadsheet's XML, and very long text is cut to what a cell holds.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxCellLength));
        foreach (var c in text)
        {
            if (sb.Length == MaxCellLength)
            {
                break;
            }

            if (c >= ' ' || c is '\t' or '\n' or '\r')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string FileName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Trim().Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return cleaned.Length == 0 ? "export" : cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}

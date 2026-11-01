using System.Globalization;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Printing.Contracts;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Domain;

namespace Quicker.Reporting.Application.Schedules;

/// <summary>A saved report as a print: run for the company and the dates in the parameters, laid out as a table for the <c>report</c> template; never evidence.</summary>
public sealed class ReportPrintModel(ReportService reports, IClock clock) : IPrintModelProvider
{
    public const string DocumentType = "report";

    string IPrintModelProvider.DocumentType => DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } = [new(PrintVariants.Standard, LocalizedText.Bilingual("Report", "تقرير"))];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var parameters = subject.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var reportId = subject.DocumentId ?? (parameters.TryGetValue("reportId", out var idText) && Guid.TryParse(idText, out var parsed) ? parsed : null);
        if (reportId is not { } id)
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A report is printed by its id (reportId).");
        }

        if (!parameters.TryGetValue("companyId", out var companyText) || !Guid.TryParse(companyText, out var companyId))
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A report is printed for a company (companyId).");
        }

        var report = await reports.GetAsync(id, cancellationToken);
        if (report.IsFailure)
        {
            return report.Error!;
        }

        var run = await reports.RunAsync(id, new RunReportRequest(companyId, PrintParameters.Date(parameters, "from"), PrintParameters.Date(parameters, "to")), cancellationToken);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var result = run.Value;
        var model = Catalogue.Find(result.Model);
        var columns = result.Fields.Select(f => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = new LocalizedText(f.Label),
            ["numeric"] = f.Type is FieldTypes.Number or FieldTypes.Money or FieldTypes.Quantity or FieldTypes.Percent,
        }).ToList();
        var rows = result.Rows.Select(row => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cells"] = result.Fields.Select(f => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = PrintParameters.Text(row.GetValueOrDefault(f.Key), f.Type),
                ["numeric"] = f.Type is FieldTypes.Number or FieldTypes.Money or FieldTypes.Quantity or FieldTypes.Percent,
            }).ToList(),
        }).ToList();
        var to = result.To ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new LocalizedText(report.Value.Name),
            ["model"] = model?.Name ?? LocalizedText.Bilingual(result.Model, result.Model),
            ["source_label"] = LocalizedText.Bilingual("Source", "المصدر"),
            ["rows_label"] = LocalizedText.Bilingual("rows", "صفوف"),
            ["truncated_label"] = LocalizedText.Bilingual("the first rows only", "الصفوف الأولى فقط"),
            ["period_from"] = result.From,
            ["period_to"] = result.To,
            ["filters"] = new List<Dictionary<string, object?>>(),
            ["columns"] = columns,
            ["rows"] = rows,
            ["row_count"] = result.RowCount,
            ["truncated"] = result.Truncated,
        };
        var number = $"{(result.From is { } f ? PrintParameters.Iso(f) : string.Empty)} {PrintParameters.Iso(to)}".Trim();
        return new PrintModel(companyId, null, new LocalizedText(report.Value.Name), number, "ready", false, to, result.Currency, data, FileName: $"{Slug(report.Value.Name)}.pdf");
    }

    private static string Slug(IReadOnlyDictionary<string, string> name)
    {
        var text = new LocalizedText(name).Resolve("en", "ar");
        var safe = new string(text.Select(static c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return safe.Length == 0 ? "report" : safe;
    }
}

/// <summary>A financial statement as a print: the layout (a tenant's by id or a shipped one by code) computed for the company and the dates, laid out for the <c>financial_statement</c> template; never evidence.</summary>
public sealed class FinancialStatementPrintModel(StatementService statements) : IPrintModelProvider
{
    public const string DocumentType = "financial_statement";

    string IPrintModelProvider.DocumentType => DocumentType;

    public IReadOnlyList<PrintVariant> Variants { get; } = [new(PrintVariants.Standard, LocalizedText.Bilingual("Financial statement", "قائمة مالية"))];

    public async Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var parameters = subject.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!parameters.TryGetValue("companyId", out var companyText) || !Guid.TryParse(companyText, out var companyId))
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A statement is printed for a company (companyId).");
        }

        var layoutId = subject.DocumentId ?? (parameters.TryGetValue("layoutId", out var idText) && Guid.TryParse(idText, out var parsed) ? parsed : null);
        var layoutCode = parameters.GetValueOrDefault("layoutCode");
        if (layoutId is null && string.IsNullOrWhiteSpace(layoutCode))
        {
            return Error.Validation(PrintErrors.ParameterRequired, "A statement is printed from a layout (layoutId or layoutCode).");
        }

        bool? includeClosing = parameters.TryGetValue("includeClosing", out var closingText) && bool.TryParse(closingText, out var closing) ? closing : null;
        var run = await statements.RunAsync(new StatementRequest(companyId, layoutId, layoutCode, PrintParameters.Date(parameters, "from"), PrintParameters.Date(parameters, "to"), null, includeClosing), cancellationToken);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var s = run.Value;
        var columns = s.Columns.Select(c => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = new LocalizedText(c.Label),
            ["dates"] = c.From is { } f && c.To is { } t ? $"{PrintParameters.Iso(f)} → {PrintParameters.Iso(t)}" : null,
        }).ToList();
        var rows = s.Rows.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = r.RowType,
            ["label"] = new LocalizedText(r.Label),
            ["indent"] = r.Indent,
            ["bold"] = r.Bold,
            ["values"] = r.RowType == RowTypes.Header ? s.Columns.Select(static _ => string.Empty).ToList() : r.Values.Select((v, i) => PrintParameters.Figure(v, s.Columns[i].IsPercent)).ToList(),
            ["accounts"] = (r.Accounts ?? []).Select(a => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = a.Code,
                ["name"] = new LocalizedText(a.Name),
                ["values"] = a.Values.Select((v, i) => PrintParameters.Figure(v, s.Columns[i].IsPercent)).ToList(),
            }).ToList(),
        }).ToList();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = new LocalizedText(s.Name),
            ["line_label"] = LocalizedText.Bilingual("Line", "البند"),
            ["period_from"] = s.From,
            ["period_to"] = s.To,
            ["closing_note"] = s.IncludeClosing ? LocalizedText.Bilingual("Closing entries included", "قيود الإقفال مضمّنة") : LocalizedText.Bilingual("Closing entries left out", "قيود الإقفال مستبعدة"),
            ["scope_note"] = LocalizedText.Bilingual($"Amounts in {s.Currency}", $"المبالغ بعملة {s.Currency}"),
            ["columns"] = columns,
            ["rows"] = rows,
        };
        return new PrintModel(companyId, null, new LocalizedText(s.Name), $"{s.LayoutCode} {PrintParameters.Iso(s.To)}", "ready", false, s.To, s.Currency, data, FileName: $"{s.LayoutCode.ToLowerInvariant()}_{PrintParameters.Iso(s.To)}.pdf");
    }
}

internal static class PrintParameters
{
    public static DateOnly? Date(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Figure(decimal? value, bool percent) => value is null ? string.Empty : percent ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + " %" : value.Value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static string Text(object? value, string type) => value switch
    {
        null => string.Empty,
        decimal d when type is FieldTypes.Percent => d.ToString("0.#", CultureInfo.InvariantCulture) + " %",
        decimal d when type is FieldTypes.Quantity => d.ToString("#,##0.###", CultureInfo.InvariantCulture),
        decimal d when type is FieldTypes.Number => d.ToString("#,##0.##", CultureInfo.InvariantCulture),
        decimal d => d.ToString("#,##0.00", CultureInfo.InvariantCulture),
        int or long => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture),
        DateOnly date => Iso(date),
        DateTime at => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "✓" : string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}

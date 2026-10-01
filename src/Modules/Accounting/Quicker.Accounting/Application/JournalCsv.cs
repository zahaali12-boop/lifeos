using System.Globalization;
using Quicker.Kernel.Results;

namespace Quicker.Accounting.Application;

/// <summary>
/// Journal import from a spreadsheet: one row per line, rows grouped into journals by <c>journal_ref</c> in file
/// order. Columns: journal_ref, posting_date, currency, account_code, debit, credit, description, reference,
/// document_date, kind, subledger_type, subledger_ref.
/// </summary>
public static class JournalCsv
{
    public static Result<IReadOnlyList<SaveJournalRequest>> Read(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var rows = AccountCsv.Rows(csv);
        if (rows.Count < 2)
        {
            return Error.Validation("import.empty", "The file has a header and no rows.");
        }

        var header = rows[0].Select(static h => h.Trim().ToLowerInvariant()).ToList();
        var journals = new List<(string Ref, List<JournalLineRequest> Lines, SaveJournalRequest Head)>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue;
            }

            string? Get(string column)
            {
                var position = header.IndexOf(column);
                return position >= 0 && position < row.Count && row[position].Length > 0 ? row[position].Trim() : null;
            }

            var reference = Get("journal_ref") ?? string.Empty;
            if (!DateOnly.TryParse(Get("posting_date"), CultureInfo.InvariantCulture, out var postingDate))
            {
                return Error.Validation("import.posting_date", $"Row {i}: posting_date must be an ISO date.").WithWhy(("row", i));
            }

            if (!decimal.TryParse(Get("debit") ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture, out var debit) || !decimal.TryParse(Get("credit") ?? "0", NumberStyles.Number, CultureInfo.InvariantCulture, out var credit))
            {
                return Error.Validation("import.amount", $"Row {i}: debit and credit must be numbers.").WithWhy(("row", i));
            }

            var line = new JournalLineRequest(debit, credit, Get("account_code"), null, null, null, Get("subledger_type"), Guid.TryParse(Get("subledger_ref"), out var subledgerRef) ? subledgerRef : null, null,
                Get("description") is { } text ? new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = text } : null);
            var index = journals.FindIndex(j => string.Equals(j.Ref, reference, StringComparison.Ordinal));
            if (index < 0)
            {
                var head = new SaveJournalRequest(postingDate, Get("currency") ?? string.Empty, [], Get("kind") ?? "manual",
                    DateOnly.TryParse(Get("document_date"), CultureInfo.InvariantCulture, out var documentDate) ? documentDate : null,
                    Get("description") is { } d ? new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = d } : null, Get("reference") ?? (reference.Length > 0 ? reference : null));
                journals.Add((reference, [line], head));
            }
            else
            {
                journals[index].Lines.Add(line);
            }
        }

        return journals.Select(static j => j.Head with { Lines = j.Lines }).ToList();
    }
}

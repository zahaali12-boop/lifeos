using System.Globalization;
using Quicker.Tax.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>Copies what the tax engine decided for each line (keyed by line number) onto the document's lines.</summary>
internal static class TaxLines
{
    public static void Apply<TLine>(TaxedDocumentResult taxed, IEnumerable<TLine> lines, Func<TLine, int> lineNo, Action<TLine, TaxedLine, TaxLineDetermination> apply)
    {
        var byKey = taxed.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        var why = taxed.Determinations.ToDictionary(static d => d.Key, StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var key = lineNo(line).ToString(CultureInfo.InvariantCulture);
            apply(line, byKey[key], why[key]);
        }
    }
}

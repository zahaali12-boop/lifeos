using Quicker.Kernel.Amounts;
using Quicker.Tax.Contracts;

namespace Quicker.Tax.Engine;

/// <summary>
/// The tax on a document's lines (ADR-0018): on prices that exclude tax, tax = net × rate; on prices that include it,
/// tax = gross × rate ÷ (100 + rate) and net = gross − tax. The regime rounds per line or per document; per document,
/// each code's tax is the rounded tax of all its lines together, spread back over the lines so they add up to it, the
/// residue on the line with the largest tax (then the first key). Reverse-charge lines carry the tax the buyer
/// self-assesses but the seller does not charge, so it adds nothing to the gross. A pure function of its input.
/// </summary>
public static class TaxCalculator
{
    public static TaxedDocument Calculate(IReadOnlyList<TaxLineInput> lines, bool pricesIncludeTax, string roundingLevel, Currency currency, RoundingPolicy rounding)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rounding);
        var minor = currency.MinorUnits;
        var exact = lines.Select(line => (Line: line, Raw: RawTax(line, pricesIncludeTax))).ToList();
        var taxes = exact.ToDictionary(static e => e.Line.Key, e => rounding.Round(e.Raw, minor), StringComparer.Ordinal);

        if (roundingLevel == TaxRoundingLevels.Document)
        {
            foreach (var byCode in exact.Where(static e => e.Line.Code is not null).GroupBy(static e => e.Line.Code!.Id))
            {
                var total = rounding.Round(byCode.Sum(static e => e.Raw), minor);
                var residue = total - byCode.Sum(e => taxes[e.Line.Key]);
                if (residue != 0m)
                {
                    var carrier = byCode.OrderByDescending(static e => Math.Abs(e.Raw)).ThenBy(static e => e.Line.Key, StringComparer.Ordinal).First();
                    taxes[carrier.Line.Key] += residue;
                }
            }
        }

        var taxed = exact.Select(e =>
        {
            var line = e.Line;
            var tax = taxes[line.Key];
            var amount = rounding.Round(line.Amount, minor);
            var net = pricesIncludeTax && line.Code is { IsReverseCharge: false } ? amount - tax : amount;
            var charged = line.Code is { IsReverseCharge: true } ? 0m : tax;
            return new TaxedLine(line.Key, line.Code?.Id, line.Code?.Code, line.Code?.RatePct ?? 0m, net, tax, net + charged, line.Code?.IsReverseCharge ?? false, line.Code?.IsRecoverable ?? true);
        }).ToList();

        var byCodeSummary = taxed
            .Where(static l => l.TaxCodeId is not null)
            .GroupBy(static l => (l.TaxCodeId!.Value, l.TaxCode!, l.RatePct, l.IsReverseCharge))
            .Select(static g => new TaxSummary(g.Key.Value, g.Key.Item2, g.Key.RatePct, g.Sum(static l => l.Net), g.Sum(static l => l.Tax), g.Key.IsReverseCharge))
            .OrderBy(static s => s.TaxCode, StringComparer.Ordinal)
            .ToList();
        return new TaxedDocument(
            taxed,
            byCodeSummary,
            taxed.Sum(static l => l.Net),
            taxed.Where(static l => !l.IsReverseCharge).Sum(static l => l.Tax),
            taxed.Sum(static l => l.Gross),
            roundingLevel,
            pricesIncludeTax);
    }

    /// <summary>The unrounded tax of a line; reverse charge is always on the net (the supplier's invoice carries no tax).</summary>
    private static decimal RawTax(TaxLineInput line, bool pricesIncludeTax)
    {
        if (line.Code is not { } code || code.RatePct == 0m)
        {
            return 0m;
        }

        return pricesIncludeTax && !code.IsReverseCharge
            ? line.Amount * code.RatePct / (100m + code.RatePct)
            : line.Amount * code.RatePct / 100m;
    }
}

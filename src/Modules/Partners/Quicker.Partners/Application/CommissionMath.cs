using Quicker.Kernel.Amounts;
using Quicker.Partners.Contracts;

namespace Quicker.Partners.Application;

/// <summary>
/// Marginal tier arithmetic of commission plans. A plan's bands for one scope start at their thresholds; the basis a
/// rep has earned so far in the tier period decides where a new sale lands, and each part of the sale earns the rate
/// of the band it falls in. Below the first threshold nothing is earned (unless that band starts at zero, which then
/// also covers credit notes that take the period below zero). The earnings for a sale are therefore
/// C(periodToDate + basis) − C(periodToDate) for the cumulative function C, so splitting a sale into parts never
/// changes what it earns before rounding, and a credit note gives back exactly what the sale earned.
/// </summary>
public static class CommissionMath
{
    /// <summary>The bands a sale crosses, with the raw (unrounded) commission of each, in band order.</summary>
    public static IReadOnlyList<(decimal FromAmount, decimal? ToAmount, decimal RatePct, decimal Basis, decimal Commission)> Crossed(IReadOnlyList<(decimal FromAmount, decimal RatePct)> tiers, decimal periodToDate, decimal basis)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var result = new List<(decimal, decimal?, decimal, decimal, decimal)>();
        if (basis == 0m || tiers.Count == 0)
        {
            return result;
        }

        var ordered = tiers.OrderBy(static t => t.FromAmount).ToList();
        var low = Math.Min(periodToDate, periodToDate + basis);
        var high = Math.Max(periodToDate, periodToDate + basis);
        var sign = basis < 0m ? -1m : 1m;
        for (var i = 0; i < ordered.Count; i++)
        {
            // The first band reaches down without end when it starts at zero; otherwise nothing below it earns.
            var from = i == 0 && ordered[0].FromAmount == 0m ? decimal.MinValue : ordered[i].FromAmount;
            decimal? to = i + 1 < ordered.Count ? ordered[i + 1].FromAmount : null;
            var overlapLow = Math.Max(low, from);
            var overlapHigh = to is { } t ? Math.Min(high, t) : high;
            if (overlapHigh <= overlapLow)
            {
                continue;
            }

            var part = (overlapHigh - overlapLow) * sign;
            result.Add((ordered[i].FromAmount, to, ordered[i].RatePct, part, part * ordered[i].RatePct / 100m));
        }

        return result;
    }

    /// <summary>C(x): what a period's basis of <paramref name="amount"/> earns in total, unrounded.</summary>
    public static decimal Cumulative(IReadOnlyList<(decimal FromAmount, decimal RatePct)> tiers, decimal amount) =>
        Crossed(tiers, 0m, amount).Sum(static b => b.Commission);

    /// <summary>
    /// The bands rounded in the plan's currency so that they sum exactly to the rounded total (largest remainder).
    /// </summary>
    public static (IReadOnlyList<CommissionBand> Bands, decimal Total) Round(IReadOnlyList<(decimal FromAmount, decimal? ToAmount, decimal RatePct, decimal Basis, decimal Commission)> crossed, Currency currency, RoundingPolicy rounding)
    {
        ArgumentNullException.ThrowIfNull(crossed);
        ArgumentNullException.ThrowIfNull(rounding);
        var raw = crossed.Sum(static b => b.Commission);
        var total = rounding.Round(new Money(raw, currency));
        if (crossed.Count == 0)
        {
            return ([], total.Amount);
        }

        var weights = crossed.Select(static b => Math.Abs(b.Commission)).ToList();
        IReadOnlyList<decimal> parts = weights.All(static w => w == 0m)
            ? weights.Select(static _ => 0m).ToList()
            : rounding.Allocate(total.Abs(), weights).Select(m => total.IsNegative ? -m.Amount : m.Amount).ToList();
        var bands = crossed.Select((b, i) => new CommissionBand(b.FromAmount, b.ToAmount, b.RatePct, b.Basis, parts[i])).ToList();
        return (bands, total.Amount);
    }
}

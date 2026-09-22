using Quicker.Kernel.Amounts;
using Quicker.Organization.Contracts;

namespace Quicker.Migrator.Demo;

public sealed record DemoRate(string RateType, string From, string To, DateOnly ValidFrom, decimal Rate);

/// <summary>
/// A year of exchange rates, identical on every run for the same last day: the official USD/IQD rate is the
/// Central Bank's fixed 1310, the market rate is a bounded random walk around 1500 drawn from a fixed seed,
/// AED is pegged to USD, EUR/USD drifts around 1.08. Month-end closing and monthly average rates derive from
/// the daily spot series, as an accountant would enter them.
/// </summary>
public static class DemoRates
{
    public const decimal OfficialUsdIqd = 1310m;
    public const decimal UsdAedPeg = 3.6725m;

    private static readonly RoundingPolicy Rounding = RoundingPolicy.Default;

    public static IReadOnlyList<DemoRate> Build(DateOnly lastDay)
    {
        var firstDay = lastDay.AddDays(-(DemoData.RateDays - 1));
        var usdIqd = Walk("usd-iqd", start: 1500m, unit: 0.25m, span: 24, min: 1450m, max: 1560m, decimals: 2);
        var eurUsd = Walk("eur-usd", start: 1.08m, unit: 0.0005m, span: 20, min: 1.02m, max: 1.14m, decimals: 4);
        var rates = new List<DemoRate>(DemoData.RateDays * 4 + 80)
        {
            new(RateTypes.Spot, "USD", "AED", firstDay, UsdAedPeg),
            new(DemoData.OfficialRateType, "USD", "IQD", firstDay, OfficialUsdIqd),
            new(DemoData.OfficialRateType, "USD", "AED", firstDay, UsdAedPeg),
        };

        var monthly = new Dictionary<(int Year, int Month), (decimal UsdIqd, decimal EurUsd, int Days, DateOnly First)>();
        for (var i = 0; i < DemoData.RateDays; i++)
        {
            var day = firstDay.AddDays(i);
            var aedIqd = Rounding.Round(usdIqd[i] / UsdAedPeg, 4);
            rates.Add(new(RateTypes.Spot, "USD", "IQD", day, usdIqd[i]));
            rates.Add(new(DemoData.MarketRateType, "USD", "IQD", day, usdIqd[i]));
            rates.Add(new(RateTypes.Spot, "EUR", "USD", day, eurUsd[i]));
            rates.Add(new(RateTypes.Spot, "AED", "IQD", day, aedIqd));
            if (day.AddDays(1).Month != day.Month)
            {
                rates.Add(new(RateTypes.Closing, "USD", "IQD", day, usdIqd[i]));
                rates.Add(new(RateTypes.Closing, "EUR", "USD", day, eurUsd[i]));
                rates.Add(new(RateTypes.Closing, "AED", "IQD", day, aedIqd));
            }

            var key = (day.Year, day.Month);
            monthly[key] = monthly.TryGetValue(key, out var sum)
                ? (sum.UsdIqd + usdIqd[i], sum.EurUsd + eurUsd[i], sum.Days + 1, sum.First)
                : (usdIqd[i], eurUsd[i], 1, day);
        }

        foreach (var (_, month) in monthly.OrderBy(static m => m.Key))
        {
            rates.Add(new(RateTypes.Average, "USD", "IQD", month.First, Rounding.Round(month.UsdIqd / month.Days, 2)));
            rates.Add(new(RateTypes.Average, "EUR", "USD", month.First, Rounding.Round(month.EurUsd / month.Days, 4)));
        }

        return rates;
    }

    private static decimal[] Walk(string series, decimal start, decimal unit, int span, decimal min, decimal max, int decimals)
    {
        var values = new decimal[DemoData.RateDays];
        var value = start;
        for (var i = 0; i < values.Length; i++)
        {
            var step = (DemoIds.Draw(series, i, 2 * span + 1) - span) * unit;
            value = Math.Clamp(value + step, min, max);
            values[i] = Rounding.Round(value, decimals);
        }

        return values;
    }
}

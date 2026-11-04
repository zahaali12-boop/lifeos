using Quicker.FixedAssets.Contracts;
using Quicker.Kernel.Amounts;

namespace Quicker.FixedAssets.Application;

/// <summary>
/// What to plan: the amount still to depreciate, the weighted months it spans, the method and the first month's weight.
/// Weighted months count a half month as a half (mid-month convention) and a partial first month by its days (daily),
/// so a plan that continues after a change starts from exactly what is left.
/// </summary>
public sealed record DepreciationPlanRequest(
    decimal Depreciable,
    decimal LifeMonths,
    string Method,
    DateOnly Start,
    decimal FirstWeight,
    int MinorUnits,
    decimal NetBookValueAtStart,
    decimal SalvageValue,
    decimal? AnnualRatePct = null,
    decimal? TotalUnits = null,
    IReadOnlyDictionary<DateOnly, decimal>? UnitsByMonth = null);

/// <summary>One month of the plan: its calendar bounds, its weight (1 for a full month) and the amount, rounded to the minor unit.</summary>
public sealed record PlannedPeriod(DateOnly Start, DateOnly End, decimal Weight, decimal Amount);

/// <summary>
/// The depreciation engine (roadmap 6.6, A-175): pure arithmetic over decimals. Every method plans month by month;
/// each month's amount is the rounded cumulative amount less what came before, so the plan sums to the depreciable
/// amount to the cent whatever the life and rate, and the last month absorbs any remainder.
/// </summary>
public static class Depreciation
{
    /// <summary>The weight of the first month of life under a convention: how much of that month counts.</summary>
    public static decimal FirstMonthWeight(string convention, DateOnly inService) => convention switch
    {
        DepreciationConventions.MidMonth => 0.5m,
        DepreciationConventions.Daily => (decimal)(DateTime.DaysInMonth(inService.Year, inService.Month) - inService.Day + 1) / DateTime.DaysInMonth(inService.Year, inService.Month),
        _ => 1m,
    };

    /// <summary>The first month depreciation is planned for: the month of service, or the next under the next-month convention.</summary>
    public static DateOnly FirstMonth(string convention, DateOnly inService)
    {
        var month = new DateOnly(inService.Year, inService.Month, 1);
        return convention == DepreciationConventions.NextMonth ? month.AddMonths(1) : month;
    }

    /// <summary>How much of the month an asset leaves in counts for depreciation (disposal, transfer out).</summary>
    public static decimal LeavingMonthWeight(string convention, DateOnly leaving) => convention switch
    {
        DepreciationConventions.FullMonth => 0m,
        DepreciationConventions.MidMonth => 0.5m,
        DepreciationConventions.Daily => (decimal)leaving.Day / DateTime.DaysInMonth(leaving.Year, leaving.Month),
        _ => 1m,
    };

    /// <summary>The annual rate a declining method uses: the rate given, else the straight-line rate times the method's factor.</summary>
    public static decimal AnnualRate(string method, decimal lifeMonths, decimal? annualRatePct)
    {
        if (annualRatePct is { } given && given > 0m)
        {
            return given / 100m;
        }

        var straight = lifeMonths <= 0m ? 0m : 12m / lifeMonths;
        return method == DepreciationMethods.DoubleDeclining ? 2m * straight : 1.5m * straight;
    }

    public static IReadOnlyList<PlannedPeriod> Plan(DepreciationPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method == DepreciationMethods.None || request.Depreciable <= 0m)
        {
            return [];
        }

        if (request.Method == DepreciationMethods.UnitsOfProduction)
        {
            return PlanByUnits(request);
        }

        if (request.LifeMonths <= 0m)
        {
            return [];
        }

        var months = Months(request.Start, request.LifeMonths, request.FirstWeight);
        return request.Method switch
        {
            DepreciationMethods.DecliningBalance or DepreciationMethods.DoubleDeclining => PlanDeclining(request, months),
            DepreciationMethods.SumOfYears => PlanByCumulative(request, months, static (p, life) => p * (2m * life - p + 1m) / (life * (life + 1m))),
            _ => PlanByCumulative(request, months, static (p, life) => p / life),
        };
    }

    /// <summary>The months a plan spans with their weights: the first as given, full months, and a last partial one when the weights would otherwise fall short of the life.</summary>
    private static List<(DateOnly Start, DateOnly End, decimal Weight)> Months(DateOnly start, decimal lifeMonths, decimal firstWeight)
    {
        var result = new List<(DateOnly, DateOnly, decimal)>();
        var month = new DateOnly(start.Year, start.Month, 1);
        var consumed = 0m;
        var first = Math.Clamp(firstWeight, 0.000001m, 1m);
        while (consumed < lifeMonths)
        {
            var weight = result.Count == 0 ? Math.Min(first, lifeMonths) : Math.Min(1m, lifeMonths - consumed);
            result.Add((month, month.AddMonths(1).AddDays(-1), weight));
            consumed += weight;
            month = month.AddMonths(1);
        }

        return result;
    }

    /// <summary>Straight line and sum of the years: the share depreciated after p weighted months of a life of L is a function of p; each month gets the rounded cumulative less the previous.</summary>
    private static List<PlannedPeriod> PlanByCumulative(DepreciationPlanRequest request, List<(DateOnly Start, DateOnly End, decimal Weight)> months, Func<decimal, decimal, decimal> shareAt)
    {
        var plan = new List<PlannedPeriod>(months.Count);
        var position = 0m;
        var cumulativeRounded = 0m;
        for (var i = 0; i < months.Count; i++)
        {
            position += months[i].Weight;
            var cumulative = i == months.Count - 1 ? request.Depreciable : RoundingPolicy.Default.Round(request.Depreciable * shareAt(position, request.LifeMonths), request.MinorUnits);
            var amount = cumulative - cumulativeRounded;
            cumulativeRounded = cumulative;
            plan.Add(new PlannedPeriod(months[i].Start, months[i].End, months[i].Weight, amount));
        }

        return plan;
    }

    /// <summary>
    /// Declining balance on the net book value, never below the salvage value, the last month taking the remainder. Double
    /// declining switches to straight line over what is left once that gives more (the usual DDB); a declining balance at
    /// a chosen rate keeps its rate to the end, as the diminishing-balance method does.
    /// </summary>
    private static List<PlannedPeriod> PlanDeclining(DepreciationPlanRequest request, List<(DateOnly Start, DateOnly End, decimal Weight)> months)
    {
        var rate = AnnualRate(request.Method, request.LifeMonths, request.AnnualRatePct);
        var switches = request.Method == DepreciationMethods.DoubleDeclining;
        var plan = new List<PlannedPeriod>(months.Count);
        var remaining = request.Depreciable;
        var netBookValue = request.NetBookValueAtStart;
        var weightLeft = months.Sum(static m => m.Weight);
        for (var i = 0; i < months.Count; i++)
        {
            var weight = months[i].Weight;
            decimal amount;
            if (i == months.Count - 1)
            {
                amount = remaining;
            }
            else
            {
                var declining = (netBookValue - request.SalvageValue <= 0m ? 0m : netBookValue) * rate / 12m * weight;
                var straight = weightLeft <= 0m ? remaining : remaining * weight / weightLeft;
                amount = RoundingPolicy.Default.Round(Math.Min(remaining, switches ? Math.Max(declining, straight) : declining), request.MinorUnits);
            }

            remaining -= amount;
            netBookValue -= amount;
            weightLeft -= weight;
            plan.Add(new PlannedPeriod(months[i].Start, months[i].End, weight, amount));
            if (remaining <= 0m)
            {
                break;
            }
        }

        return plan;
    }

    /// <summary>Units of production: each month with recorded units takes its share of the depreciable amount, never more than what is left.</summary>
    private static List<PlannedPeriod> PlanByUnits(DepreciationPlanRequest request)
    {
        if (request.TotalUnits is not { } total || total <= 0m || request.UnitsByMonth is null || request.UnitsByMonth.Count == 0)
        {
            return [];
        }

        var plan = new List<PlannedPeriod>();
        var remaining = request.Depreciable;
        foreach (var (month, units) in request.UnitsByMonth.OrderBy(static u => u.Key))
        {
            if (remaining <= 0m)
            {
                break;
            }

            var start = new DateOnly(month.Year, month.Month, 1);
            var amount = Math.Min(remaining, RoundingPolicy.Default.Round(request.Depreciable * units / total, request.MinorUnits));
            remaining -= amount;
            plan.Add(new PlannedPeriod(start, start.AddMonths(1).AddDays(-1), 1m, amount));
        }

        return plan;
    }
}

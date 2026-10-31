using Quicker.FixedAssets.Application;
using Quicker.FixedAssets.Contracts;

namespace Quicker.FixedAssets.Tests;

/// <summary>The depreciation engine alone: every method plans to the cent over the life, under every convention.</summary>
public sealed class DepreciationTests
{
    private static DepreciationPlanRequest Request(decimal depreciable, decimal life, string method, string convention, DateOnly inService, decimal cost, decimal salvage, decimal? rate = null) =>
        new(depreciable, life, method, Depreciation.FirstMonth(convention, inService), Depreciation.FirstMonthWeight(convention, inService), 2, cost, salvage, rate);

    [Fact]
    public void Straight_line_spreads_the_depreciable_amount_evenly_and_sums_to_the_cent()
    {
        var plan = Depreciation.Plan(Request(1_200m, 36, DepreciationMethods.StraightLine, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 15), 1_500m, 300m));
        plan.Count.ShouldBe(36);
        plan.Sum(static p => p.Amount).ShouldBe(1_200m);
        plan.Select(static p => p.Amount).Distinct().ShouldBe([33.33m, 33.34m], ignoreOrder: true);
        plan[0].Start.ShouldBe(new DateOnly(2026, 1, 1));
        plan[^1].End.ShouldBe(new DateOnly(2028, 12, 31));
        plan.All(static p => p.Weight == 1m).ShouldBeTrue();
    }

    [Fact]
    public void Mid_month_takes_half_a_month_first_and_half_a_month_after_the_life()
    {
        var plan = Depreciation.Plan(Request(24_000m, 24, DepreciationMethods.StraightLine, DepreciationConventions.MidMonth, new DateOnly(2026, 2, 10), 24_000m, 0m));
        plan.Count.ShouldBe(25);
        (plan[0].Amount, plan[0].Weight).ShouldBe((500m, 0.5m));
        plan[1].Amount.ShouldBe(1_000m);
        (plan[^1].Amount, plan[^1].Weight).ShouldBe((500m, 0.5m));
        plan.Sum(static p => p.Amount).ShouldBe(24_000m);
    }

    [Fact]
    public void Daily_prorates_the_first_month_by_its_days_and_next_month_starts_the_month_after()
    {
        var daily = Depreciation.Plan(Request(3_100m, 12, DepreciationMethods.StraightLine, DepreciationConventions.Daily, new DateOnly(2026, 10, 16), 3_100m, 0m));
        daily.Count.ShouldBe(13);
        daily[0].Weight.ShouldBe(16m / 31m);
        daily[0].Amount.ShouldBe(133.33m, "16 of 31 days of a month worth 258.33");
        daily.Sum(static p => p.Amount).ShouldBe(3_100m);

        var next = Depreciation.Plan(Request(1_200m, 12, DepreciationMethods.StraightLine, DepreciationConventions.NextMonth, new DateOnly(2026, 10, 16), 1_200m, 0m));
        next.Count.ShouldBe(12);
        next[0].Start.ShouldBe(new DateOnly(2026, 11, 1));
        next.All(static p => p.Amount == 100m).ShouldBeTrue();
    }

    [Fact]
    public void Sum_of_the_years_front_loads_and_still_sums_exactly()
    {
        var plan = Depreciation.Plan(Request(7_800m, 36, DepreciationMethods.SumOfYears, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 1), 7_800m, 0m));
        plan.Count.ShouldBe(36);
        plan.Sum(static p => p.Amount).ShouldBe(7_800m);
        plan[0].Amount.ShouldBeGreaterThan(plan[^1].Amount);
        plan[0].Amount.ShouldBe(421.62m, "the first month carries 36 of 666 digits of 7,800");
        plan[^1].Amount.ShouldBe(11.71m, "the last month carries the single remaining digit");
    }

    [Fact]
    public void Double_declining_starts_at_twice_the_straight_rate_switches_to_straight_line_and_stops_at_salvage()
    {
        var plan = Depreciation.Plan(Request(9_000m, 60, DepreciationMethods.DoubleDeclining, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 1), 10_000m, 1_000m));
        plan[0].Amount.ShouldBe(333.33m, "10,000 at 40 % a year is 333.33 a month");
        plan.Sum(static p => p.Amount).ShouldBe(9_000m);
        plan.Count.ShouldBe(60);
        plan.All(static p => p.Amount >= 0m).ShouldBeTrue();
        // Once straight line over what is left gives more than the declining amount, the months level off to the cent.
        var tail = plan.Skip(48).Select(static p => p.Amount).ToList();
        (tail.Max() - tail.Min()).ShouldBeLessThanOrEqualTo(0.02m);
    }

    [Fact]
    public void Declining_balance_with_a_given_rate_uses_it_and_the_last_month_takes_the_remainder()
    {
        var plan = Depreciation.Plan(Request(5_000m, 24, DepreciationMethods.DecliningBalance, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 1), 5_000m, 0m, 30m));
        plan[0].Amount.ShouldBe(125m, "5,000 at 30 % a year is 125 a month");
        plan[1].Amount.ShouldBe(121.88m, "then 30 % of what is left");
        plan.Sum(static p => p.Amount).ShouldBe(5_000m);
        plan.Count.ShouldBe(24);
        plan[^1].Amount.ShouldBeGreaterThan(plan[^2].Amount, "the last month takes the remainder down to the salvage value");
    }

    [Fact]
    public void Units_of_production_follows_the_units_recorded_and_never_exceeds_what_is_left()
    {
        var units = new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 1, 1)] = 400m, [new DateOnly(2026, 2, 1)] = 700m, [new DateOnly(2026, 3, 1)] = 100m };
        var plan = Depreciation.Plan(new DepreciationPlanRequest(1_000m, 0m, DepreciationMethods.UnitsOfProduction, new DateOnly(2026, 1, 1), 1m, 2, 1_000m, 0m, TotalUnits: 1_000m, UnitsByMonth: units));
        plan.Select(static p => p.Amount).ShouldBe([400m, 600m], "the third month would exceed the depreciable amount");
    }

    [Fact]
    public void A_plan_that_continues_after_a_change_spreads_what_is_left_over_the_months_left()
    {
        // 27,500 left over 21.5 weighted months (a mid-month asset two and a half months in): 21 full months and a final half.
        var plan = Depreciation.Plan(new DepreciationPlanRequest(27_500m, 21.5m, DepreciationMethods.StraightLine, new DateOnly(2026, 5, 1), 1m, 2, 27_500m, 0m));
        plan.Count.ShouldBe(22);
        plan[^1].Weight.ShouldBe(0.5m);
        plan.Sum(static p => p.Amount).ShouldBe(27_500m);
        plan[0].Amount.ShouldBe(1_279.07m);
    }

    [Fact]
    public void Nothing_is_planned_for_land_or_a_fully_depreciated_asset()
    {
        Depreciation.Plan(Request(1_000m, 0, DepreciationMethods.None, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 1), 1_000m, 0m)).ShouldBeEmpty();
        Depreciation.Plan(Request(0m, 36, DepreciationMethods.StraightLine, DepreciationConventions.FullMonth, new DateOnly(2026, 1, 1), 1_000m, 1_000m)).ShouldBeEmpty();
    }

    [Fact]
    public void The_leaving_month_counts_by_the_convention()
    {
        Depreciation.LeavingMonthWeight(DepreciationConventions.FullMonth, new DateOnly(2026, 8, 15)).ShouldBe(0m);
        Depreciation.LeavingMonthWeight(DepreciationConventions.MidMonth, new DateOnly(2026, 8, 15)).ShouldBe(0.5m);
        Depreciation.LeavingMonthWeight(DepreciationConventions.Daily, new DateOnly(2026, 8, 15)).ShouldBe(15m / 31m);
        Depreciation.LeavingMonthWeight(DepreciationConventions.NextMonth, new DateOnly(2026, 8, 15)).ShouldBe(1m);
    }
}

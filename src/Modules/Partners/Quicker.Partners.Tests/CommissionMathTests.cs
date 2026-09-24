using Quicker.Kernel.Amounts;
using Quicker.Partners.Application;

namespace Quicker.Partners.Tests;

/// <summary>
/// The arithmetic of marginal commission bands, checked over many generated plans and sales: splitting a sale never
/// changes what it earns, a credit note gives back exactly what the sale earned, and rounding in the plan's currency
/// leaves bands that sum to the total.
/// </summary>
public sealed class CommissionMathTests
{
    private static readonly Currency Iqd = Currency.IQD;

    private static IReadOnlyList<(decimal FromAmount, decimal RatePct)> Plan(Random random)
    {
        var tiers = new List<(decimal, decimal)>();
        var from = random.Next(2) == 0 ? 0m : random.Next(1, 50) * 100_000m;
        for (var i = random.Next(1, 5); i > 0; i--)
        {
            tiers.Add((from, random.Next(0, 1_000) / 100m));
            from += random.Next(1, 100) * 100_000m;
        }

        return tiers;
    }

    [Fact]
    public void Splitting_a_sale_never_changes_what_it_earns_and_a_credit_note_gives_back_exactly_what_it_earned()
    {
        var random = new Random(20260924);
        for (var run = 0; run < 2_000; run++)
        {
            var tiers = Plan(random);
            var soFar = random.Next(0, 200) * 50_000m;
            var first = random.Next(1, 100_000) * 37m;
            var second = random.Next(1, 100_000) * 53m;

            var whole = CommissionMath.Crossed(tiers, soFar, first + second).Sum(static b => b.Commission);
            var parts = CommissionMath.Crossed(tiers, soFar, first).Sum(static b => b.Commission) + CommissionMath.Crossed(tiers, soFar + first, second).Sum(static b => b.Commission);
            parts.ShouldBe(whole, $"run {run}");
            whole.ShouldBe(CommissionMath.Cumulative(tiers, soFar + first + second) - CommissionMath.Cumulative(tiers, soFar), $"run {run}");

            var back = CommissionMath.Crossed(tiers, soFar + first + second, -(first + second));
            back.Sum(static b => b.Commission).ShouldBe(-whole, $"run {run}");
            back.Sum(static b => b.Basis).ShouldBe(-CommissionMath.Crossed(tiers, soFar, first + second).Sum(static b => b.Basis), $"run {run}");
        }
    }

    [Fact]
    public void Rounded_bands_sum_to_the_rounded_total_and_nothing_below_the_first_threshold_earns()
    {
        var random = new Random(5_1);
        for (var run = 0; run < 2_000; run++)
        {
            var tiers = Plan(random);
            var basis = (random.Next(-50_000, 100_000) * 7.777m) + 0.001m;
            var crossed = CommissionMath.Crossed(tiers, random.Next(0, 100) * 10_000m, basis);
            var (bands, total) = CommissionMath.Round(crossed, Iqd, RoundingPolicy.Default);
            bands.Sum(static b => b.Commission).ShouldBe(total, $"run {run}");
            total.ShouldBe(RoundingPolicy.Default.Round(crossed.Sum(static b => b.Commission), 3), $"run {run}");
            bands.ShouldAllBe(b => Math.Sign(b.Commission) == 0 || Math.Sign(b.Commission) == Math.Sign(basis));
        }

        // A plan whose first band starts at one million pays nothing on the first million.
        var threshold = new List<(decimal, decimal)> { (1_000_000m, 4m) };
        CommissionMath.Crossed(threshold, 0m, 1_000_000m).ShouldBeEmpty();
        CommissionMath.Crossed(threshold, 500_000m, 1_000_000m).ShouldHaveSingleItem().Commission.ShouldBe(20_000m);
        CommissionMath.Crossed([], 0m, 1_000m).ShouldBeEmpty();
    }
}

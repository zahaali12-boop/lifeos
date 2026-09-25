using System.Runtime.InteropServices;
using System.Text.Json;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Engine;
using static Quicker.Pricing.Tests.EngineWorld;

namespace Quicker.Pricing.Tests;

/// <summary>
/// ADR-0030's determinism guarantees over thousands of generated rule sets and baskets: the same inputs give the same
/// prices and the same explanation; the order in which rules were created or read never changes anything (so stacking
/// depends only on priorities); and every priced line and total adds up.
/// </summary>
public sealed class PriceEnginePropertyTests
{
    private static readonly Guid[] Catalogue = [Tea, Cup, Kettle];
    private static readonly string[] Currencies = ["IQD", "USD"];

    [Fact]
    public void The_same_basket_and_rules_give_the_same_prices_and_explanation_in_whatever_order_the_rules_are_read()
    {
        var random = new Random(20260924);
        var priced = 0;
        for (var run = 0; run < 1_500; run++)
        {
            var world = RandomWorld(random);
            var (lines, currency, partner, group, channel, coupons, documentDiscount) = RandomBasket(random);
            var first = world.Run(lines, currency, partner, group, channel, coupons, documentDiscount);
            var expected = JsonSerializer.Serialize(first);
            JsonSerializer.Serialize(world.Run(lines, currency, partner, group, channel, coupons, documentDiscount)).ShouldBe(expected, $"run {run}: the same input twice");

            Shuffle(world, random);
            JsonSerializer.Serialize(world.Run(lines, currency, partner, group, channel, coupons, documentDiscount)).ShouldBe(expected, $"run {run}: the rules read in another order");

            AssertAddsUp(first, run);
            priced += first.Lines.Count(static l => l.Problem is null);
        }

        // The generator must actually exercise the pipeline, not produce baskets nothing prices.
        priced.ShouldBeGreaterThan(2_000);
    }

    private static void AssertAddsUp(PricingResult result, int run)
    {
        foreach (var line in result.Lines.Where(static l => l.Problem is null))
        {
            line.NetAmount.ShouldBe(line.GrossAmount - line.LineDiscountAmount - line.PromotionDiscountAmount - line.DocumentDiscountAmount, $"run {run} line {line.Key}");
            line.NetAmount.ShouldBeGreaterThanOrEqualTo(0m, $"run {run} line {line.Key}");
            line.LineDiscountAmount.ShouldBe(line.Steps.Where(static s => s.Kind == PriceStepKinds.LineDiscount).Sum(static s => s.Amount ?? 0m), $"run {run} line {line.Key}");
            line.PromotionDiscountAmount.ShouldBe(line.Steps.Where(static s => s.Kind == PriceStepKinds.Promotion).Sum(static s => s.Amount ?? 0m), $"run {run} line {line.Key}");
            line.DocumentDiscountAmount.ShouldBe(line.Steps.Where(static s => s.Kind == PriceStepKinds.DocumentDiscount).Sum(static s => s.Amount ?? 0m), $"run {run} line {line.Key}");
        }

        var priced = result.Lines.Where(static l => l.Problem is null).ToList();
        result.NetAmount.ShouldBe(priced.Sum(static l => l.NetAmount), $"run {run}");
        result.GrossAmount.ShouldBe(priced.Sum(static l => l.GrossAmount), $"run {run}");
        result.DiscountAmount.ShouldBe(result.GrossAmount - result.NetAmount, $"run {run}");
        result.DocumentSteps.Where(static s => s.Kind == PriceStepKinds.DocumentDiscount).Sum(static s => s.Amount ?? 0m).ShouldBe(priced.Sum(static l => l.DocumentDiscountAmount), $"run {run}");
        result.Promotions.Sum(static p => p.Benefit).ShouldBe(priced.Sum(static l => l.PromotionDiscountAmount), $"run {run}");
    }

    private static EngineWorld RandomWorld(Random random)
    {
        var w = new EngineWorld();
        for (var i = random.Next(1, 5); i > 0; i--)
        {
            var parent = w.Lists.Count > 0 && random.Next(3) == 0 ? w.Lists[random.Next(w.Lists.Count)] : null;
            var list = w.List(
                $"L{w.Lists.Count}",
                currency: Pick(random, Currencies),
                isDefault: w.Lists.All(static l => !l.IsDefault) && random.Next(3) == 0,
                partners: random.Next(3) == 0 ? [Partner] : null,
                groups: random.Next(3) == 0 ? [Group] : null,
                priority: random.Next(0, 3),
                parent: parent?.Id,
                adjustment: parent is null ? null : random.Next(-30, 40),
                increment: parent is not null && random.Next(2) == 0 ? Pick(random, [0.05m, 250m, 1m]) : null,
                mode: Pick(random, ["nearest", "up", "down"]),
                surcharge: parent is not null && random.Next(4) == 0 ? -0.01m : 0m,
                includesTax: random.Next(10) == 0,
                active: random.Next(10) != 0,
                to: random.Next(10) == 0 ? Today.AddDays(-1) : null);
            foreach (var item in Catalogue)
            {
                // Distinct (unit, break, start) per item, as the database's unique key guarantees.
                var keys = new HashSet<(Guid, decimal, DateOnly?)>();
                for (var n = random.Next(0, 4); n > 0; n--)
                {
                    var uom = item == Tea && random.Next(3) == 0 ? Box : Pc;
                    var key = (uom, (decimal)Pick(random, [0, 6, 12, 24]), random.Next(4) == 0 ? Today.AddDays(-random.Next(1, 30)) : (DateOnly?)null);
                    if (keys.Add(key))
                    {
                        w.Price(list, item, random.Next(1, 5_000) * (list.Currency == "USD" ? 0.01m : 25m), key.Item1, key.Item2, from: key.Item3);
                    }
                }
            }
        }

        var agreementKeys = new HashSet<(Guid?, Guid?, decimal)>();
        for (var i = random.Next(0, 4); i > 0; i--)
        {
            var onCategory = random.Next(3) == 0;
            var item = onCategory ? (Guid?)null : Pick(random, Catalogue);
            var category = onCategory ? Pick(random, [Beverages, TeaCategory, Accessories]) : (Guid?)null;
            var min = (decimal)Pick(random, [0, 12]);
            var withPrice = !onCategory && random.Next(2) == 0;
            if (agreementKeys.Add((item ?? category, withPrice ? Pc : null, min)))
            {
                w.Agreements.Add(new EngineAgreement(w.Id(), $"A{i}", item, null, category, withPrice ? Pc : null, min, withPrice ? random.Next(1, 3_000) : null, withPrice ? Pick(random, Currencies) : null,
                    withPrice ? null : random.Next(1, 20), null, null, random.Next(8) != 0));
            }
        }

        for (var i = random.Next(0, 7); i > 0; i--)
        {
            var level = random.Next(4) == 0 ? DiscountLevels.Document : DiscountLevels.Line;
            var valueType = level == DiscountLevels.Document ? Pick(random, [DiscountValueTypes.Percentage, DiscountValueTypes.Amount]) : Pick(random, DiscountValueTypes.All.ToArray());
            var value = valueType switch
            {
                DiscountValueTypes.Percentage => random.Next(1, 25),
                DiscountValueTypes.Amount => random.Next(1, 500),
                _ => random.Next(100, 3_000),
            };
            var line = level == DiscountLevels.Line;
            decimal? minAmount = random.Next(5) == 0 ? random.Next(1, 50) * 1_000 : null;
            w.Rule(
                $"R{i}",
                valueType,
                value,
                level,
                Pick(random, Combinations.All.ToArray()),
                random.Next(0, 3),
                item: line && random.Next(4) == 0 ? Pick(random, Catalogue) : null,
                category: line && random.Next(4) == 0 ? Pick(random, [Beverages, TeaCategory, Accessories]) : null,
                brand: line && random.Next(6) == 0 ? Brand : null,
                partner: random.Next(5) == 0 ? Partner : null,
                group: random.Next(5) == 0 ? Group : null,
                channel: random.Next(6) == 0 ? "web" : null,
                terms: random.Next(8) == 0 ? Terms : null,
                minQuantity: line && random.Next(4) == 0 ? random.Next(1, 30) : null,
                minAmount: minAmount,
                weekdays: random.Next(6) == 0 ? [(int)Today.DayOfWeek, 5] : null,
                currency: valueType != DiscountValueTypes.Percentage || minAmount is not null ? Pick(random, Currencies) : null);
        }

        for (var i = random.Next(0, 5); i > 0; i--)
        {
            var kind = Pick(random, PromotionKinds.All.ToArray());
            var combination = Pick(random, Combinations.All.ToArray());
            var coupon = kind == PromotionKinds.Coupon || random.Next(5) == 0 ? $"C{i}" : null;
            _ = kind switch
            {
                PromotionKinds.BuyXGetY => w.Promotion($"P{i}", kind, combination, random.Next(0, 3), coupon, item: Pick(random, Catalogue), buy: random.Next(2, 12), getItem: random.Next(2) == 0 ? Pick(random, Catalogue) : null,
                    get: random.Next(1, 3), getPct: Pick(random, [100m, 50m]), maxApplications: random.Next(3) == 0 ? 1 : null, limit: random.Next(5) == 0 ? 2 : null, used: random.Next(0, 3)),
                PromotionKinds.Bundle => w.Promotion($"P{i}", kind, combination, random.Next(0, 3), coupon, bundlePrice: random.Next(1, 40) * 1_000m,
                    components: [new EngineComponent(Tea, random.Next(1, 12)), new EngineComponent(Pick(random, [Cup, Kettle]), 1m)]),
                PromotionKinds.VolumeTier => w.Promotion($"P{i}", kind, combination, random.Next(0, 3), coupon, category: Pick(random, [Beverages, Accessories]),
                    tiers: [new EngineTier(random.Next(1, 10), random.Next(1, 5)), new EngineTier(random.Next(10, 40), random.Next(5, 15))]),
                _ => w.Promotion($"P{i}", kind, combination, random.Next(0, 3), coupon, category: random.Next(2) == 0 ? Accessories : null, discountPct: random.Next(1, 30)),
            };
        }

        foreach (var item in Catalogue.Where(_ => random.Next(3) == 0))
        {
            w.Floors.Add(new EngineFloor(w.Id(), item, null, random.Next(2) == 0 ? random.Next(100, 3_000) : null, "IQD", random.Next(2) == 0 ? random.Next(-10, 40) : null, Pick(random, FloorBreachActions.All.ToArray()), true));
            if (w.Floors[^1] is { MinPrice: null, MinMarginPct: null } empty)
            {
                w.Floors[^1] = empty with { MinMarginPct = 10m };
            }

            if (random.Next(2) == 0)
            {
                w.Costs[item] = random.Next(100, 3_000);
            }
        }

        return w;
    }

    private static (IReadOnlyList<EngineLine> Lines, string Currency, Guid? Partner, Guid? Group, string? Channel, IReadOnlyCollection<string> Coupons, decimal? DocumentDiscount) RandomBasket(Random random)
    {
        var lines = new List<EngineLine>();
        for (var i = random.Next(1, 6); i > 0; i--)
        {
            var item = Pick(random, Catalogue);
            var box = item == Tea && random.Next(3) == 0;
            lines.Add(Line($"l{i}", item, random.Next(1, 40), box ? Box : null, random.Next(12) == 0 ? random.Next(100, 2_000) : null, random.Next(10) == 0 ? random.Next(1, 20) : null));
        }

        return (
            lines,
            Pick(random, Currencies),
            random.Next(3) == 0 ? null : Partner,
            random.Next(2) == 0 ? Group : null,
            Pick(random, new string?[] { null, "web", "field" }),
            random.Next(2) == 0 ? ["C1", "C2", "C3", "C4"] : [],
            random.Next(5) == 0 ? random.Next(1, 10) : null);
    }

    /// <summary>Reorders every collection of the rule set, and the assignments inside each list and the parts of each promotion.</summary>
    private static void Shuffle(EngineWorld w, Random random)
    {
        random.Shuffle(CollectionsMarshal.AsSpan(w.ListItems));
        random.Shuffle(CollectionsMarshal.AsSpan(w.Agreements));
        random.Shuffle(CollectionsMarshal.AsSpan(w.Rules));
        random.Shuffle(CollectionsMarshal.AsSpan(w.Floors));
        var lists = w.Lists.Select(l => l with { PartnerIds = Shuffled(random, l.PartnerIds), GroupIds = Shuffled(random, l.GroupIds) }).ToList();
        random.Shuffle(CollectionsMarshal.AsSpan(lists));
        w.Lists.Clear();
        w.Lists.AddRange(lists);
        var promotions = w.Promotions.Select(p => p with { Components = Shuffled(random, p.Components), Tiers = Shuffled(random, p.Tiers) }).ToList();
        random.Shuffle(CollectionsMarshal.AsSpan(promotions));
        w.Promotions.Clear();
        w.Promotions.AddRange(promotions);
    }

    private static T[] Shuffled<T>(Random random, IReadOnlyList<T> items)
    {
        var copy = items.ToArray();
        random.Shuffle(copy);
        return copy;
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> options) => options[random.Next(options.Count)];
}

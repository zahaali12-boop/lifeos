using Quicker.Pricing.Contracts;
using Quicker.Pricing.Engine;
using static Quicker.Pricing.Tests.EngineWorld;

namespace Quicker.Pricing.Tests;

/// <summary>
/// The pricing pipeline of ADR-0030 step by step, without a database: where a base price comes from, quantity breaks
/// and units, derived lists across currencies, competing and stacking discounts, promotions over the basket, document
/// discounts, floors, and the problems that leave a line unpriced with its reason.
/// </summary>
public sealed class PriceEngineTests
{
    private static PricedLine LineOf(PricingResult result, string key) => result.Lines.Single(l => l.Key == key);

    [Fact]
    public void The_highest_quantity_break_reached_prices_the_line_in_its_own_unit_else_the_base_unit_converted()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Tea, 950m, min: 24m);
        w.Price(list, Tea, 11_500m, uom: Box);
        w.Price(list, Tea, 11_000m, uom: Box, min: 10m);

        var result = w.Run([Line("a", Tea, 12m), Line("b", Tea, 24m), Line("c", Tea, 2m, Box), Line("d", Tea, 10m, Box)]);
        LineOf(result, "a").UnitPrice.ShouldBe(1_000m);
        LineOf(result, "b").UnitPrice.ShouldBe(950m);
        LineOf(result, "c").UnitPrice.ShouldBe(11_500m);
        LineOf(result, "d").UnitPrice.ShouldBe(11_000m);
        LineOf(result, "d").GrossAmount.ShouldBe(110_000m);
        LineOf(result, "d").BaseQuantity.ShouldBe(120m);
        LineOf(result, "b").Steps[0].Facts.ShouldContain(new PriceFact("minQuantity", "24"));

        // Without box prices, two boxes are 24 pieces: the 24-piece break times twelve, and the step says so.
        var pieces = new EngineWorld();
        var only = pieces.List("DEF", isDefault: true);
        pieces.Price(only, Tea, 1_000m);
        pieces.Price(only, Tea, 950m, min: 24m);
        var boxes = LineOf(pieces.Run([Line("c", Tea, 2m, Box)]), "c");
        boxes.UnitPrice.ShouldBe(11_400m);
        boxes.Steps.Select(static s => s.Kind).ShouldBe([PriceStepKinds.BasePrice, PriceStepKinds.Unit]);
        boxes.Steps[1].Facts.ShouldContain(new PriceFact("factor", "12"));
    }

    [Fact]
    public void An_agreement_beats_the_customer_list_which_beats_the_group_list_the_default_list_and_the_item_list_price()
    {
        var w = new EngineWorld();
        var customer = w.List("CUST", partners: [Partner]);
        var group = w.List("GRP", groups: [Group]);
        var standard = w.List("DEF", isDefault: true);
        w.Price(customer, Tea, 900m);
        w.Price(group, Tea, 920m);
        w.Price(standard, Tea, 1_000m);
        w.Price(standard, Cup, 2_000m);
        w.Agreements.Add(new EngineAgreement(w.Id(), "C-2026-01", Tea, null, null, Pc, 0m, 880m, "IQD", null, null, null, true));

        var agreed = w.Run([Line("tea", Tea, 5m), Line("cup", Cup, 1m), Line("kettle", Kettle, 1m)], partner: Partner, group: Group);
        LineOf(agreed, "tea").PriceSource.ShouldBe(PriceSources.Agreement);
        LineOf(agreed, "tea").UnitPrice.ShouldBe(880m);
        LineOf(agreed, "tea").Steps[0].RefCode.ShouldBe("C-2026-01");

        // The cup is on neither the customer's nor the group's list: both are named as considered, the default list wins.
        var cup = LineOf(agreed, "cup");
        cup.PriceSource.ShouldBe(PriceSources.DefaultList);
        cup.Steps[0].Candidates.Select(static c => (c.RefCode, c.Outcome)).ShouldBe([("CUST", CandidateOutcomes.NoPrice), ("GRP", CandidateOutcomes.NoPrice), ("DEF", CandidateOutcomes.Won)]);

        // The kettle is on no list: its own list price of 25 USD, converted at 1310.
        var kettle = LineOf(agreed, "kettle");
        kettle.PriceSource.ShouldBe(PriceSources.ItemListPrice);
        kettle.UnitPrice.ShouldBe(32_750m);
        kettle.Steps.Select(static s => s.Kind).ShouldBe([PriceStepKinds.BasePrice, PriceStepKinds.Currency]);
        kettle.Steps[1].Facts.ShouldContain(new PriceFact("rate", "1310"));

        w.Agreements.Clear();
        LineOf(w.Run([Line("tea", Tea, 5m)], partner: Partner, group: Group), "tea").PriceSource.ShouldBe(PriceSources.CustomerList);
        LineOf(w.Run([Line("tea", Tea, 5m)], partner: OtherPartner, group: Group), "tea").UnitPrice.ShouldBe(920m);
        LineOf(w.Run([Line("tea", Tea, 5m)]), "tea").UnitPrice.ShouldBe(1_000m);

        // A list named on the document comes before the customer's own.
        var named = LineOf(w.Run([Line("tea", Tea, 5m)], partner: Partner, group: Group, documentList: group.Id), "tea");
        named.PriceSource.ShouldBe(PriceSources.DocumentList);
        named.UnitPrice.ShouldBe(920m);

        // An inactive or expired list is passed over, and says why.
        w.Lists[0] = customer with { IsActive = false };
        w.Lists[1] = group with { ValidTo = Today.AddDays(-1) };
        var passed = LineOf(w.Run([Line("tea", Tea, 5m)], partner: Partner, group: Group), "tea");
        passed.UnitPrice.ShouldBe(1_000m);
        passed.Steps[0].Candidates.Select(static c => c.Outcome).ShouldBe([CandidateOutcomes.Inactive, CandidateOutcomes.NotValid, CandidateOutcomes.Won]);
    }

    [Fact]
    public void A_derived_list_converts_its_parents_price_adjusts_rounds_up_to_its_increment_and_adds_its_surcharge()
    {
        var w = new EngineWorld();
        var parent = w.List("BASE");
        w.Price(parent, Tea, 1_000m);
        var export = w.List("EXPORT", currency: "USD", parent: parent.Id, adjustment: -10m, increment: 0.05m, mode: PriceRoundingModes.Up, surcharge: -0.01m);

        // 1000 IQD × 0.00076336 = 0.76336 USD, less 10% = 0.687024, up to 0.70, less 0.01 = 0.69.
        var line = LineOf(w.Run([Line("tea", Tea, 100m)], currency: "USD", documentList: export.Id), "tea");
        line.UnitPrice.ShouldBe(0.69m);
        line.GrossAmount.ShouldBe(69m);
        line.Steps.Select(static s => s.Kind).ShouldBe([PriceStepKinds.BasePrice, PriceStepKinds.Derivation]);
        var derivation = line.Steps[1];
        derivation.Before.ShouldBe(1_000m);
        derivation.After.ShouldBe(0.69m);
        derivation.Facts.ShouldContain(new PriceFact("parentList", "BASE"));
        derivation.Facts.ShouldContain(new PriceFact("adjustmentPct", "-10"));
        derivation.Facts.ShouldContain(new PriceFact("roundingMode", "up"));
    }

    [Fact]
    public void The_best_exclusive_discount_wins_then_stackable_ones_apply_in_priority_order_and_near_misses_are_explained()
    {
        var w = new EngineWorld();
        w.Price(w.List("DEF", isDefault: true), Tea, 1_000m);
        w.Rule("BEV5", DiscountValueTypes.Percentage, 5m, category: Beverages);
        w.Rule("TEA60", DiscountValueTypes.Amount, 60m, item: Tea, minQuantity: 12m, currency: "IQD");
        w.Rule("GRP2", DiscountValueTypes.Percentage, 2m, combination: Combinations.Stackable, priority: 10, group: Group);
        w.Rule("WEB1", DiscountValueTypes.Percentage, 1m, combination: Combinations.Stackable, priority: 20, channel: "web");
        w.Rule("VIP900", DiscountValueTypes.FixedPrice, 900m, partner: Partner, minQuantity: 100m, currency: "IQD");

        var line = LineOf(w.Run([Line("tea", Tea, 24m)], partner: Partner, group: Group, channel: "field"), "tea");
        line.GrossAmount.ShouldBe(24_000m);
        line.LineDiscountAmount.ShouldBe(1_891.2m);
        line.NetAmount.ShouldBe(22_108.8m);
        var discounts = line.Steps.Where(static s => s.Kind == PriceStepKinds.LineDiscount).ToList();
        discounts.Select(static s => (s.RefCode, s.Amount)).ShouldBe([("TEA60", 1_440m), ("GRP2", 451.2m)]);
        discounts[0].Candidates.Select(static c => (c.RefCode, c.Outcome, c.Detail)).ShouldBe(
            [("TEA60", CandidateOutcomes.Won, null), ("BEV5", CandidateOutcomes.Lost, null), ("VIP900", CandidateOutcomes.ConditionNotMet, "min_quantity:100")]);

        // At a hundred pieces the fixed price of 900 saves more than either and wins.
        var volume = LineOf(w.Run([Line("tea", Tea, 100m)], partner: Partner, group: Group), "tea");
        volume.Steps.First(static s => s.Kind == PriceStepKinds.LineDiscount).RefCode.ShouldBe("VIP900");
        volume.NetAmount.ShouldBe(88_200m);
    }

    [Fact]
    public void Equal_exclusive_discounts_break_ties_by_priority_then_specificity_then_code()
    {
        var w = new EngineWorld();
        w.Price(w.List("DEF", isDefault: true), Tea, 1_000m);
        w.Rule("B-CATEGORY", DiscountValueTypes.Percentage, 5m, category: Beverages, priority: 50);
        w.Rule("A-ITEM", DiscountValueTypes.Percentage, 5m, item: Tea, priority: 50);
        w.Rule("Z-FIRST", DiscountValueTypes.Percentage, 5m, priority: 10);
        Winner(w).ShouldBe("Z-FIRST");

        w.Rules.RemoveAll(static r => r.Code == "Z-FIRST");
        Winner(w).ShouldBe("A-ITEM");

        w.Rules.RemoveAll(static r => r.Code == "A-ITEM");
        w.Rule("A-CATEGORY", DiscountValueTypes.Percentage, 5m, category: Beverages, priority: 50);
        Winner(w).ShouldBe("A-CATEGORY");

        static string? Winner(EngineWorld world) => LineOf(world.Run([Line("tea", Tea, 10m)]), "tea").Steps.First(static s => s.Kind == PriceStepKinds.LineDiscount).RefCode;
    }

    [Fact]
    public void Buy_ten_get_a_cup_free_adds_the_cup_as_its_own_line_at_its_regular_price_fully_discounted()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Cup, 2_000m);
        w.Promotion("TEA10-CUP", PromotionKinds.BuyXGetY, item: Tea, buy: 10m, getItem: Cup, get: 1m, getPct: 100m);

        var result = w.Run([Line("tea", Tea, 25m)]);
        result.Lines.Select(static l => l.Key).ShouldBe(["tea", "TEA10-CUP#free"]);
        var free = LineOf(result, "TEA10-CUP#free");
        free.IsFreeGoods.ShouldBeTrue();
        free.Quantity.ShouldBe(2m);
        free.UnitPrice.ShouldBe(2_000m);
        free.GrossAmount.ShouldBe(4_000m);
        free.PromotionDiscountAmount.ShouldBe(4_000m);
        free.NetAmount.ShouldBe(0m);
        free.FreeGoodsForKey.ShouldBe("tea");
        LineOf(result, "tea").NetAmount.ShouldBe(25_000m);
        result.Promotions.ShouldHaveSingleItem().Benefit.ShouldBe(4_000m);
        result.NetAmount.ShouldBe(25_000m);

        // Nine are not enough, and the explanation says how far off the basket is.
        var short9 = w.Run([Line("tea", Tea, 9m)]);
        short9.Lines.Count.ShouldBe(1);
        short9.DocumentSteps.Single(static s => s.Kind == PriceStepKinds.Promotion).Candidates.ShouldHaveSingleItem().Detail.ShouldBe("buy:9/10");
    }

    [Fact]
    public void A_bundle_sells_each_complete_set_at_its_price_and_spreads_the_saving_over_its_lines()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Kettle, 10_000m);
        w.Promotion("TEA-TIME", PromotionKinds.Bundle, bundlePrice: 20_000m, components: [new EngineComponent(Tea, 12m), new EngineComponent(Kettle, 1m)]);

        // One set (twelve tea and a kettle) is worth 22,000; sold at 20,000 the 2,000 saving splits 12:10.
        var result = w.Run([Line("tea", Tea, 24m), Line("kettle", Kettle, 1m)]);
        LineOf(result, "tea").PromotionDiscountAmount.ShouldBe(1_090.909m);
        LineOf(result, "kettle").PromotionDiscountAmount.ShouldBe(909.091m);
        result.Promotions.ShouldHaveSingleItem().Benefit.ShouldBe(2_000m);
        result.NetAmount.ShouldBe(32_000m);

        // No kettle, no set.
        w.Run([Line("tea", Tea, 24m)]).Promotions.ShouldBeEmpty();
    }

    [Fact]
    public void Exclusive_promotions_compete_for_lines_and_stackable_ones_take_the_rest()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Cup, 2_000m);
        w.Promotion("VOLUME", PromotionKinds.VolumeTier, category: Beverages, tiers: [new EngineTier(24m, 3m), new EngineTier(48m, 5m)]);
        var coupon = w.Promotion("RAMADAN", PromotionKinds.Coupon, coupon: "RAMADAN10", discountPct: 10m);
        IReadOnlyList<EngineLine> basket = [Line("tea", Tea, 30m), Line("cup", Cup, 5m)];

        // Without the code only the volume tier applies: 3% on 30,000.
        var noCode = w.Run(basket);
        noCode.Promotions.ShouldHaveSingleItem().Code.ShouldBe("VOLUME");
        LineOf(noCode, "tea").PromotionDiscountAmount.ShouldBe(900m);
        noCode.DocumentSteps.Single().Candidates.Single(static c => c.RefCode == "RAMADAN").Outcome.ShouldBe(CandidateOutcomes.CouponMissing);

        // With it, 10% of everything (4,000) beats 900 and takes the tea line from the volume tier.
        var withCode = w.Run(basket, coupons: ["ramadan10"]);
        withCode.Promotions.ShouldHaveSingleItem().Code.ShouldBe("RAMADAN");
        withCode.DocumentSteps.Single().Candidates.Single(static c => c.RefCode == "VOLUME").Outcome.ShouldBe(CandidateOutcomes.LineTaken);
        withCode.NetAmount.ShouldBe(36_000m);

        // A coupon for accessories only leaves the tea to the volume tier: both apply.
        w.Promotions.Remove(coupon);
        w.Promotion("CUPS", PromotionKinds.Coupon, coupon: "CUPS10", category: Accessories, discountPct: 10m);
        var both = w.Run(basket, coupons: ["CUPS10"]);
        both.Promotions.Select(static p => p.Code).ShouldBe(["CUPS", "VOLUME"]);
        both.NetAmount.ShouldBe(29_100m + 9_000m);

        // Two tea lines reach the second tier together.
        LineOf(w.Run([Line("a", Tea, 30m), Line("b", Tea, 20m)]), "b").PromotionDiscountAmount.ShouldBe(1_000m);

        // A stackable coupon still discounts the lines no exclusive promotion took.
        w.Promotions.RemoveAt(w.Promotions.Count - 1);
        w.Promotion("ALL5", PromotionKinds.Coupon, combination: Combinations.Stackable, coupon: "ALL5", discountPct: 5m);
        var stacked = w.Run(basket, coupons: ["ALL5"]);
        LineOf(stacked, "tea").PromotionDiscountAmount.ShouldBe(900m);
        LineOf(stacked, "cup").PromotionDiscountAmount.ShouldBe(500m);

        // A used-up promotion is not offered.
        w.Promotions.Clear();
        w.Promotion("ONCE", PromotionKinds.Coupon, coupon: "ONCE", discountPct: 50m, limit: 3, used: 3);
        var used = w.Run(basket, coupons: ["ONCE"]);
        used.Promotions.ShouldBeEmpty();
        used.DocumentSteps.Single().Candidates.ShouldHaveSingleItem().Outcome.ShouldBe(CandidateOutcomes.UsageLimitReached);
    }

    [Fact]
    public void Document_discounts_apply_on_the_subtotal_and_are_spread_over_the_lines_to_the_last_fils()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Cup, 2_000m);
        w.Rule("GRP2-DOC", DiscountValueTypes.Percentage, 2m, level: DiscountLevels.Document, combination: Combinations.Stackable, group: Group, minAmount: 10_000m, currency: "IQD");

        var result = w.Run([Line("tea", Tea, 7m), Line("cup", Cup, 3m)], group: Group, documentDiscount: 1m);
        result.DocumentSteps.Select(static s => (s.Source, s.Amount)).ShouldBe([("rule", 260m), (PriceSources.Manual, 127.4m)]);
        LineOf(result, "tea").DocumentDiscountAmount.ShouldBe(208.6m);
        LineOf(result, "cup").DocumentDiscountAmount.ShouldBe(178.8m);
        result.NetAmount.ShouldBe(12_612.6m);

        // Under the minimum the rule is named with the amount it waits for.
        var small = w.Run([Line("tea", Tea, 9m)], group: Group);
        small.DocumentSteps.ShouldHaveSingleItem().Candidates.ShouldHaveSingleItem().Detail.ShouldBe("min_amount:10000 IQD");
    }

    [Fact]
    public void A_price_under_its_floor_blocks_and_a_thin_margin_warns()
    {
        var w = new EngineWorld();
        var list = w.List("DEF", isDefault: true);
        w.Price(list, Tea, 1_000m);
        w.Price(list, Kettle, 10_000m);
        w.Floors.Add(new EngineFloor(w.Id(), Tea, null, 950m, "IQD", null, FloorBreachActions.Block, true));
        w.Floors.Add(new EngineFloor(w.Id(), null, Accessories, null, null, 20m, FloorBreachActions.Warn, true));
        w.Costs[Kettle] = 8_000m;

        var fine = w.Run([Line("tea", Tea, 10m), Line("kettle", Kettle, 1m)]);
        fine.HasFloorBlocks.ShouldBeFalse();
        fine.HasFloorWarnings.ShouldBeFalse();
        LineOf(fine, "kettle").Floor.ShouldNotBeNull().MarginPct.ShouldBe(20m);

        var cut = w.Run([Line("tea", Tea, 10m, manualDiscount: 10m), Line("kettle", Kettle, 1m, manualDiscount: 5m)]);
        var tea = LineOf(cut, "tea").Floor.ShouldNotBeNull();
        tea.Breached.ShouldBeTrue();
        tea.NetPerBaseUnit.ShouldBe(900m);
        tea.Reason.ShouldBe("below_min_price");
        var kettle = LineOf(cut, "kettle").Floor.ShouldNotBeNull();
        kettle.Breached.ShouldBeTrue();
        kettle.MarginPct.ShouldBe(15.7895m);
        kettle.OnBreach.ShouldBe(FloorBreachActions.Warn);
        cut.HasFloorBlocks.ShouldBeTrue();
        cut.HasFloorWarnings.ShouldBeTrue();
    }

    [Fact]
    public void A_line_that_cannot_be_priced_says_why_and_the_rest_of_the_basket_is_still_priced()
    {
        var w = new EngineWorld();
        w.Price(w.List("DEF", isDefault: true), Tea, 1_000m);
        w.Rates.Clear();

        var result = w.Run([
            Line("tea", Tea, 3m),
            Line("ghost", Guid.CreateVersion7(), 1m),
            Line("cup-box", Cup, 1m, Box),
            Line("half", Tea, 1.5m),
            Line("none", Tea, 0m),
            Line("kettle", Kettle, 1m),
        ]);
        LineOf(result, "tea").NetAmount.ShouldBe(3_000m);
        LineOf(result, "ghost").Problem!.Code.ShouldBe("pricing.item_unknown");
        LineOf(result, "cup-box").Problem!.Code.ShouldBe("pricing.uom_not_item_unit");
        LineOf(result, "half").Problem!.Code.ShouldBe("quantity.not_exact_in_base");
        LineOf(result, "none").Problem!.Code.ShouldBe("pricing.quantity_not_positive");
        LineOf(result, "kettle").Problem!.Code.ShouldBe("pricing.rate_missing");
        result.UnpricedLines.ShouldBe(5);
        result.NetAmount.ShouldBe(3_000m);

        // A document whose prices exclude tax cannot take a price that includes it without the tax rate; a document
        // that does not say takes the basis of the prices it finds.
        var taxed = new EngineWorld();
        taxed.Price(taxed.List("INC", isDefault: true, includesTax: true), Tea, 1_000m);
        LineOf(taxed.Run([Line("tea", Tea, 3m)], includesTax: false), "tea").Problem!.Code.ShouldBe("pricing.tax_basis_mismatch");
        taxed.Run([Line("tea", Tea, 3m)]).PricesIncludeTax.ShouldBe(true);
    }
}

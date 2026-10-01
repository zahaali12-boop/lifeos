using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Persistence;
using Quicker.Pricing.Application;
using Quicker.Pricing.Contracts;

namespace Quicker.Migrator.Demo;

/// <summary>What the pricing seed produced.</summary>
public sealed record DemoPricingOutcome(int PriceLists, int Prices, int Agreements, int Rules, int Promotions, int Floors);

/// <summary>
/// Demo seed for roadmap 5.2: each company's standard price list in its own currency (the forty best-selling items,
/// piece and carton prices with a two-dozen break), lists derived from it for wholesale, hotels and corporate buyers
/// (a percentage off, rounded to 250 dinars, to five cents less one, or to a quarter dirham), Basra Oil's contract
/// prices with a carton break, a category discount for Kurdistan Distribution, line and document discount rules, the
/// promotions a distributor runs (twelve plus one, beverage volume tiers, a Ramadan coupon, a juice bundle) and the
/// floors under them. Everything goes through the pricing services, so the rules seeded are rules an admin could type.
/// </summary>
internal static class DemoPricing
{
    private static readonly RoundingPolicy Rounding = RoundingPolicy.Default;

    private sealed record Catalogue(Guid Id, string Code, decimal ListPrice, Guid BaseUomId, Guid? CartonUomId, decimal? PerCarton);

    public static async Task<DemoPricingOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var lists = services.GetRequiredService<PriceListService>();
        var rules = services.GetRequiredService<PricingRulesService>();
        Guid Company(string code) => companies.Single(c => c.Definition.Code == code).Company.Id;

        async Task<Guid> IdAsync(string sql, object args) =>
            await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, args, unitOfWork.Transaction, cancellationToken: cancellationToken));

        async Task<IReadOnlyList<Catalogue>> ItemsAsync(string family, int count) =>
            (await unitOfWork.Connection.QueryAsync<Catalogue>(new CommandDefinition("""
                SELECT i.id AS Id, i.code AS Code, i.list_price AS ListPrice, i.base_uom_id AS BaseUomId, u.uom_id AS CartonUomId, u.numerator AS PerCarton
                FROM app.itm_items i
                LEFT JOIN app.itm_item_uoms u ON u.tenant_id = i.tenant_id AND u.item_id = i.id AND u.uom_id = (SELECT id FROM app.org_uoms WHERE tenant_id = i.tenant_id AND code = 'CTN')
                WHERE i.tenant_id = @tenant AND i.code LIKE @prefix AND i.list_price IS NOT NULL AND NOT i.has_variants
                ORDER BY i.code
                LIMIT @count
                """, new { tenant, prefix = family + "-%", count }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToList();

        var groups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var code in new[] { "WHOLESALE", "HORECA", "CORPORATE" })
        {
            groups[code] = await IdAsync("SELECT id FROM app.ptr_customer_groups WHERE tenant_id = @tenant AND code = @code", new { tenant, code });
        }

        var categories = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var code in new[] { "BEV", "ACC", "STAT" })
        {
            categories[code] = await IdAsync("SELECT id FROM app.itm_item_categories WHERE tenant_id = @tenant AND code = @code", new { tenant, code });
        }

        Guid Group(string code) => groups[code];
        Guid Category(string code) => categories[code];
        Guid Customer(string key) => DemoBooks.Customers.Single(c => c.Key == key).Ref;
        var prices = 0;

        // A company's standard list: its own prices from the item's dollar list price, a piece price with a two-dozen
        // break and, where the item comes in cartons, a carton price a little under twelve pieces.
        async Task<Guid> StandardAsync(string company, string currency, decimal rate, decimal increment, IReadOnlyList<Catalogue> catalogue)
        {
            var list = Require(await lists.SaveAsync(null, new SavePriceListRequest(Company(company), $"{company}-STD", Text("Standard prices 2026", "الأسعار القياسية ٢٠٢٦"), currency, IsDefault: true, Priority: 100), cancellationToken));
            foreach (var item in catalogue)
            {
                var piece = Math.Max(increment, Rounding.RoundToMultiple(item.ListPrice * rate, increment, RoundingDirection.Nearest));
                Require(await lists.SaveItemAsync(list.Id, null, new SavePriceListItemRequest(item.Id, null, item.BaseUomId, 0m, piece), cancellationToken));
                Require(await lists.SaveItemAsync(list.Id, null, new SavePriceListItemRequest(item.Id, null, item.BaseUomId, 24m, Rounding.RoundToMultiple(piece * 0.97m, increment, RoundingDirection.Nearest)), cancellationToken));
                prices += 2;
                if (item is { CartonUomId: { } carton, PerCarton: { } perCarton })
                {
                    Require(await lists.SaveItemAsync(list.Id, null, new SavePriceListItemRequest(item.Id, null, carton, 0m, Rounding.RoundToMultiple(piece * perCarton * 0.95m, increment, RoundingDirection.Nearest)), cancellationToken));
                    prices++;
                }
            }

            return list.Id;
        }

        var beverages = await ItemsAsync("BEV", 40);
        var accessories = await ItemsAsync("ACC", 25);
        var stationery = await ItemsAsync("STAT", 25);
        var iqt = await StandardAsync("IQT", "IQD", 1_310m, 250m, beverages);
        var usi = await StandardAsync("USI", "USD", 1m, 0.05m, accessories);
        var aeg = await StandardAsync("AEG", "AED", 3.6725m, 0.25m, stationery);

        // Lists derived for the groups that buy on better terms.
        Require(await lists.SaveAsync(null, new SavePriceListRequest(Company("IQT"), "IQT-WHOLESALE", Text("Wholesale, 8% under standard", "الجملة، أقل من القياسي بـ ٨٪"), "IQD", ParentListId: iqt, ParentAdjustmentPct: -8m,
            RoundingIncrement: 250m, CustomerGroupIds: [Group("WHOLESALE")], Priority: 50), cancellationToken));
        Require(await lists.SaveAsync(null, new SavePriceListRequest(Company("IQT"), "IQT-HORECA", Text("Hotels and restaurants", "الفنادق والمطاعم"), "IQD", ParentListId: iqt, ParentAdjustmentPct: -5m,
            RoundingIncrement: 250m, RoundingMode: PriceRoundingModes.Up, CustomerGroupIds: [Group("HORECA")], Priority: 50), cancellationToken));
        Require(await lists.SaveAsync(null, new SavePriceListRequest(Company("USI"), "USI-CORP", Text("Corporate accounts", "حسابات الشركات"), "USD", ParentListId: usi, ParentAdjustmentPct: -10m,
            RoundingIncrement: 0.05m, RoundingMode: PriceRoundingModes.Up, PriceSurcharge: -0.01m, CustomerGroupIds: [Group("CORPORATE")], Priority: 50), cancellationToken));
        Require(await lists.SaveAsync(null, new SavePriceListRequest(Company("AEG"), "AEG-HORECA", Text("Hotel supply", "تجهيز الفنادق"), "AED", ParentListId: aeg, ParentAdjustmentPct: -6m,
            RoundingIncrement: 0.25m, CustomerGroupIds: [Group("HORECA")], Priority: 50), cancellationToken));

        // Basra Oil's contract: its own carton prices for the first five beverages, cheaper from ten cartons, for the year.
        var agreements = 0;
        var yearEnd = new DateOnly(today.Year, 12, 31);
        foreach (var item in beverages.Where(static b => b.CartonUomId is not null).Take(5))
        {
            var carton = Rounding.RoundToMultiple(item.ListPrice * 1_310m * item.PerCarton!.Value * 0.9m, 250m, RoundingDirection.Nearest);
            Require(await rules.SaveAgreementAsync(null, new SavePriceAgreementRequest(Company("IQT"), Customer("cust:basra-oil"), "BOS-2026", item.Id, null, null, item.CartonUomId, 0m, carton, "IQD", null, new DateOnly(today.Year, 1, 1), yearEnd), cancellationToken));
            Require(await rules.SaveAgreementAsync(null, new SavePriceAgreementRequest(Company("IQT"), Customer("cust:basra-oil"), "BOS-2026-10", item.Id, null, null, item.CartonUomId, 10m, Rounding.RoundToMultiple(carton * 0.96m, 250m, RoundingDirection.Nearest), "IQD", null, new DateOnly(today.Year, 1, 1), yearEnd), cancellationToken));
            agreements += 2;
        }

        Require(await rules.SaveAgreementAsync(null, new SavePriceAgreementRequest(Company("IQT"), Customer("cust:kurdistan-dist"), "KD-BEV", null, null, Category("BEV"), null, 0m, null, null, 3m), cancellationToken));
        Require(await rules.SaveAgreementAsync(null, new SavePriceAgreementRequest(Company("AEG"), Customer("cust:dubai-hotels"), "DHS-STAT", null, null, Category("STAT"), null, 0m, null, null, 5m), cancellationToken));
        agreements += 2;

        // Discount rules: hotels get 2% on top, beverages 3% on Fridays, a big order 1.5% off the document.
        var saved = new[]
        {
            await rules.SaveRuleAsync(null, new SaveDiscountRuleRequest(Company("IQT"), "HORECA-2", Text("Hotels: 2% on top", "الفنادق: ٢٪ إضافية"), DiscountLevels.Line, DiscountValueTypes.Percentage, 2m,
                Combination: Combinations.Stackable, Priority: 10, CustomerGroupId: Group("HORECA")), cancellationToken),
            await rules.SaveRuleAsync(null, new SaveDiscountRuleRequest(Company("IQT"), "FRIDAY-BEV", Text("Friday beverages 3%", "مشروبات الجمعة ٣٪"), DiscountLevels.Line, DiscountValueTypes.Percentage, 3m,
                CategoryId: Category("BEV"), Weekdays: [5]), cancellationToken),
            await rules.SaveRuleAsync(null, new SaveDiscountRuleRequest(Company("IQT"), "BIG-ORDER", Text("Orders over 5 million", "طلبات فوق ٥ ملايين"), DiscountLevels.Document, DiscountValueTypes.Percentage, 1.5m,
                Currency: "IQD", Combination: Combinations.Stackable, MinAmount: 5_000_000m), cancellationToken),
            await rules.SaveRuleAsync(null, new SaveDiscountRuleRequest(Company("USI"), "CABLES-BULK", Text("Cables by the hundred", "الكابلات بالمئة"), DiscountLevels.Line, DiscountValueTypes.Percentage, 7m,
                ItemId: accessories[0].Id, MinQuantity: 100m), cancellationToken),
        };
        foreach (var rule in saved)
        {
            Require(rule);
        }

        // Promotions: twelve plus one on the best seller, volume tiers on beverages, a Ramadan coupon, a juice bundle.
        var promotions = new[]
        {
            await rules.SavePromotionAsync(null, new SavePromotionRequest(Company("IQT"), "BEV-12PLUS1", Text("Twelve plus one", "١٢ + ١ مجانًا"), PromotionKinds.BuyXGetY, ItemId: beverages[0].Id, BuyQuantity: 12m, GetQuantity: 1m, GetDiscountPct: 100m), cancellationToken),
            await rules.SavePromotionAsync(null, new SavePromotionRequest(Company("IQT"), "BEV-VOLUME", Text("Beverage volume", "خصم كميات المشروبات"), PromotionKinds.VolumeTier, CategoryId: Category("BEV"),
                Combination: Combinations.Stackable, Tiers: [new PromotionTierDto(120m, 2m), new PromotionTierDto(480m, 4m)]), cancellationToken),
            await rules.SavePromotionAsync(null, new SavePromotionRequest(Company("IQT"), "RAMADAN", Text("Ramadan 10%", "رمضان ١٠٪"), PromotionKinds.Coupon, CouponCode: "RAMADAN10", CategoryId: Category("BEV"),
                DiscountPct: 10m, UsageLimit: 500, UsageLimitPerCustomer: 3), cancellationToken),
            await rules.SavePromotionAsync(null, new SavePromotionRequest(Company("IQT"), "JUICE-DUO", Text("Juice duo", "ثنائي العصير"), PromotionKinds.Bundle, BundlePrice: Rounding.RoundToMultiple((beverages[1].ListPrice + beverages[2].ListPrice) * 1_310m * 0.85m, 250m, RoundingDirection.Nearest),
                Currency: "IQD", Components: [new PromotionComponentDto(beverages[1].Id, 1m), new PromotionComponentDto(beverages[2].Id, 1m)]), cancellationToken),
        };
        foreach (var promotion in promotions)
        {
            Require(promotion);
        }

        // Floors: beverages keep an 8% margin over cost (a warning), the best seller never under 80% of its standard price.
        Require(await rules.SaveFloorAsync(null, new SavePriceFloorRequest(Company("IQT"), null, Category("BEV"), null, null, 8m, FloorBreachActions.Warn), cancellationToken));
        Require(await rules.SaveFloorAsync(null, new SavePriceFloorRequest(Company("IQT"), beverages[0].Id, null, Rounding.RoundToMultiple(beverages[0].ListPrice * 1_310m * 0.8m, 250m, RoundingDirection.Down), "IQD", null), cancellationToken));
        Require(await rules.SaveFloorAsync(null, new SavePriceFloorRequest(Company("USI"), null, Category("ACC"), null, null, 12m, FloorBreachActions.Block), cancellationToken));

        return new DemoPricingOutcome(7, prices, agreements, saved.Length, promotions.Length, 3);
    }

    private static Dictionary<string, string> Text(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}

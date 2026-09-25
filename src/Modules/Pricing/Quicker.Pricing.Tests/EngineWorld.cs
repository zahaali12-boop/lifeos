using Quicker.Kernel.Amounts;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Engine;

namespace Quicker.Pricing.Tests;

/// <summary>A small catalogue and customer to price against without a database: tea by the piece and by the box of twelve, cups and a kettle.</summary>
internal sealed class EngineWorld
{
    public static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-000000000001");
    public static readonly Guid Partner = Guid.Parse("01900000-0000-7000-8000-000000000002");
    public static readonly Guid OtherPartner = Guid.Parse("01900000-0000-7000-8000-000000000003");
    public static readonly Guid Group = Guid.Parse("01900000-0000-7000-8000-000000000004");
    public static readonly Guid Terms = Guid.Parse("01900000-0000-7000-8000-000000000005");
    public static readonly Guid Pc = Guid.Parse("01900000-0000-7000-8000-000000000010");
    public static readonly Guid Box = Guid.Parse("01900000-0000-7000-8000-000000000011");
    public static readonly Guid Beverages = Guid.Parse("01900000-0000-7000-8000-000000000020");
    public static readonly Guid TeaCategory = Guid.Parse("01900000-0000-7000-8000-000000000021");
    public static readonly Guid Accessories = Guid.Parse("01900000-0000-7000-8000-000000000022");
    public static readonly Guid Brand = Guid.Parse("01900000-0000-7000-8000-000000000030");
    public static readonly Guid Tea = Guid.Parse("01900000-0000-7000-8000-000000000100");
    public static readonly Guid Cup = Guid.Parse("01900000-0000-7000-8000-000000000101");
    public static readonly Guid Kettle = Guid.Parse("01900000-0000-7000-8000-000000000102");
    public static readonly DateOnly Today = new(2026, 9, 24);

    public List<EngineList> Lists { get; } = [];

    public List<EngineListItem> ListItems { get; } = [];

    public List<EngineAgreement> Agreements { get; } = [];

    public List<EngineDiscountRule> Rules { get; } = [];

    public List<EnginePromotion> Promotions { get; } = [];

    public List<EngineFloor> Floors { get; } = [];

    public Dictionary<string, EngineRate> Rates { get; } = new(StringComparer.Ordinal)
    {
        ["USD>IQD"] = new EngineRate(1310m, "direct", Today),
        ["IQD>USD"] = new EngineRate(0.00076336m, "inverse", Today),
    };

    public Dictionary<Guid, decimal> Costs { get; } = [];

    public Dictionary<Guid, EngineItem> Items { get; } = new()
    {
        [Tea] = new EngineItem(Tea, "TEA", Pc, 0, null, [TeaCategory, Beverages], Brand, null, null, [new EngineUom(Pc, "PC", 1m, 1m), new EngineUom(Box, "BOX", 12m, 1m)], true),
        [Cup] = new EngineItem(Cup, "CUP", Pc, 0, null, [Accessories], null, null, null, [new EngineUom(Pc, "PC", 1m, 1m)], true),
        [Kettle] = new EngineItem(Kettle, "KETTLE", Pc, 0, null, [Accessories], null, 25m, "USD", [new EngineUom(Pc, "PC", 1m, 1m)], true),
    };

    public EngineList List(string code, string currency = "IQD", bool isDefault = false, Guid[]? partners = null, Guid[]? groups = null, int priority = 100, Guid? parent = null, decimal? adjustment = null,
        decimal? increment = null, string mode = "nearest", decimal surcharge = 0m, bool includesTax = false, bool active = true, DateOnly? from = null, DateOnly? to = null)
    {
        var list = new EngineList(Id(), code, currency, includesTax, parent, adjustment, increment, mode, surcharge, from, to, priority, isDefault, active, partners ?? [], groups ?? []);
        Lists.Add(list);
        return list;
    }

    public void Price(EngineList list, Guid item, decimal price, Guid? uom = null, decimal min = 0m, Guid? variant = null, DateOnly? from = null, DateOnly? to = null) =>
        ListItems.Add(new EngineListItem(Id(), list.Id, item, variant, uom ?? Pc, min, price, from, to));

    public EngineDiscountRule Rule(string code, string valueType, decimal value, string level = "line", string combination = "exclusive", int priority = 100, Guid? item = null, Guid? category = null, Guid? brand = null,
        Guid? partner = null, Guid? group = null, string? channel = null, Guid? terms = null, decimal? minQuantity = null, decimal? minAmount = null, int[]? weekdays = null, string? currency = null)
    {
        var rule = new EngineDiscountRule(Id(), code, level, item, category, brand, partner, group, channel, terms, minQuantity, minAmount, weekdays, valueType, value, currency, combination, priority, null, null, true);
        Rules.Add(rule);
        return rule;
    }

    public EnginePromotion Promotion(string code, string kind, string combination = "exclusive", int priority = 100, string? coupon = null, Guid? item = null, Guid? category = null, decimal? buy = null, Guid? getItem = null,
        decimal? get = null, decimal? getPct = null, int? maxApplications = null, decimal? bundlePrice = null, decimal? discountPct = null, int? limit = null, int used = 0, EngineComponent[]? components = null, EngineTier[]? tiers = null)
    {
        var promotion = new EnginePromotion(Id(), code, kind, coupon, item, category, null, null, null, null, buy, getItem, get, getPct, maxApplications, bundlePrice, bundlePrice is null ? null : "IQD", discountPct,
            combination, priority, limit, null, used, 0, null, null, true, components ?? [], tiers ?? []);
        Promotions.Add(promotion);
        return promotion;
    }

    public PricingResult Run(IReadOnlyList<EngineLine> lines, string currency = "IQD", Guid? partner = null, Guid? group = null, string? channel = null, IReadOnlyCollection<string>? coupons = null,
        decimal? documentDiscount = null, Guid? documentList = null, bool? includesTax = null, DateOnly? date = null) =>
        PriceEngine.Price(Input(lines, currency, partner, group, channel, coupons, documentDiscount, documentList, includesTax, date));

    public PricingInput Input(IReadOnlyList<EngineLine> lines, string currency = "IQD", Guid? partner = null, Guid? group = null, string? channel = null, IReadOnlyCollection<string>? coupons = null,
        decimal? documentDiscount = null, Guid? documentList = null, bool? includesTax = null, DateOnly? date = null)
    {
        var context = new PricingContext(Company, "IQD", partner, group, Terms, channel, currency == "USD" ? Currency.USD : Currency.IQD, date ?? Today, "spot", documentList, includesTax,
            (coupons ?? []).Select(static c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal), documentDiscount, RoundingPolicy.Default);
        return new PricingInput(context, lines, Items, new PricingRuleSet(Lists, ListItems, Agreements, Rules, Promotions, Floors), Rates, Costs);
    }

    public static EngineLine Line(string key, Guid item, decimal quantity, Guid? uom = null, decimal? manualPrice = null, decimal? manualDiscount = null, Guid? variant = null) =>
        new(key, item, variant, uom, quantity, manualPrice, manualDiscount);

    private int _next = 1000;

    /// <summary>Ids in creation order, like the database's time-ordered ids, so shuffling the rules is a real test of order independence.</summary>
    public Guid Id() => Guid.Parse($"01900000-0000-7000-8000-{_next++:D12}");
}

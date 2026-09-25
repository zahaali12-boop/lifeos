using Quicker.Kernel.Amounts;

namespace Quicker.Pricing.Engine;

/// <summary>One unit an item is sold in: one unit is <see cref="Factor"/> base units, exactly.</summary>
public sealed record EngineUom(Guid UomId, string Code, decimal Numerator, decimal Denominator)
{
    public decimal Factor => Numerator / Denominator;
}

/// <summary>What the engine needs to know about an item: its units, its category lineage (nearest first), its brand and its own list price.</summary>
public sealed record EngineItem(
    Guid Id,
    string Code,
    Guid BaseUomId,
    int BasePrecision,
    Guid? SalesUomId,
    IReadOnlyList<Guid> CategoryLineage,
    Guid? BrandId,
    decimal? ListPrice,
    string? ListPriceCurrency,
    IReadOnlyList<EngineUom> Uoms,
    bool IsActive);

public sealed record EngineList(
    Guid Id,
    string Code,
    string Currency,
    bool PricesIncludeTax,
    Guid? ParentId,
    decimal? AdjustmentPct,
    decimal? RoundingIncrement,
    string RoundingMode,
    decimal Surcharge,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    bool IsDefault,
    bool IsActive,
    IReadOnlyList<Guid> PartnerIds,
    IReadOnlyList<Guid> GroupIds);

public sealed record EngineListItem(Guid Id, Guid ListId, Guid ItemId, Guid? VariantId, Guid UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom, DateOnly? ValidTo);

public sealed record EngineAgreement(
    Guid Id,
    string? Reference,
    Guid? ItemId,
    Guid? VariantId,
    Guid? CategoryId,
    Guid? UomId,
    decimal MinQuantity,
    decimal? Price,
    string? Currency,
    decimal? DiscountPct,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive);

public sealed record EngineDiscountRule(
    Guid Id,
    string Code,
    string Level,
    Guid? ItemId,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? PartnerId,
    Guid? CustomerGroupId,
    string? Channel,
    Guid? PaymentTermsId,
    decimal? MinQuantity,
    decimal? MinAmount,
    IReadOnlyList<int>? Weekdays,
    string ValueType,
    decimal Value,
    string? Currency,
    string Combination,
    int Priority,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive);

public sealed record EngineComponent(Guid ItemId, decimal Quantity);

public sealed record EngineTier(decimal MinQuantity, decimal DiscountPct);

public sealed record EnginePromotion(
    Guid Id,
    string Code,
    string Kind,
    string? CouponCode,
    Guid? ItemId,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? PartnerId,
    Guid? CustomerGroupId,
    string? Channel,
    decimal? BuyQuantity,
    Guid? GetItemId,
    decimal? GetQuantity,
    decimal? GetDiscountPct,
    int? MaxApplications,
    decimal? BundlePrice,
    string? Currency,
    decimal? DiscountPct,
    string Combination,
    int Priority,
    int? UsageLimit,
    int? UsageLimitPerCustomer,
    int Used,
    int UsedByCustomer,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    IReadOnlyList<EngineComponent> Components,
    IReadOnlyList<EngineTier> Tiers);

public sealed record EngineFloor(Guid Id, Guid? ItemId, Guid? CategoryId, decimal? MinPrice, string? Currency, decimal? MinMarginPct, string OnBreach, bool IsActive);

/// <summary>The rules in force for one company, as read for one pricing request. Order carries no meaning: the engine sorts by explicit keys.</summary>
public sealed record PricingRuleSet(
    IReadOnlyList<EngineList> Lists,
    IReadOnlyList<EngineListItem> ListItems,
    IReadOnlyList<EngineAgreement> Agreements,
    IReadOnlyList<EngineDiscountRule> DiscountRules,
    IReadOnlyList<EnginePromotion> Promotions,
    IReadOnlyList<EngineFloor> Floors);

/// <summary>A rate as the engine uses it: 1 unit of the source currency is <see cref="Rate"/> units of the target.</summary>
public sealed record EngineRate(decimal Rate, string Method, DateOnly EffectiveFrom);

/// <summary>Who, when and in what the basket is priced.</summary>
public sealed record PricingContext(
    Guid CompanyId,
    string FunctionalCurrency,
    Guid? PartnerId,
    Guid? CustomerGroupId,
    Guid? PaymentTermsId,
    string? Channel,
    Currency Currency,
    DateOnly PricingDate,
    string RateType,
    Guid? DocumentListId,
    bool? PricesIncludeTax,
    IReadOnlyCollection<string> CouponCodes,
    decimal? DocumentDiscountPct,
    RoundingPolicy Rounding);

public sealed record EngineLine(string Key, Guid ItemId, Guid? VariantId, Guid? UomId, decimal Quantity, decimal? ManualUnitPrice, decimal? ManualDiscountPct);

/// <summary>
/// Everything one pricing needs, already read: the engine does no I/O, so the same input always gives the same answer.
/// Rates are keyed <c>FROM&gt;TO</c>; unit costs are per base unit in the company's functional currency.
/// </summary>
public sealed record PricingInput(
    PricingContext Context,
    IReadOnlyList<EngineLine> Lines,
    IReadOnlyDictionary<Guid, EngineItem> Items,
    PricingRuleSet Rules,
    IReadOnlyDictionary<string, EngineRate> Rates,
    IReadOnlyDictionary<Guid, decimal> UnitCosts);

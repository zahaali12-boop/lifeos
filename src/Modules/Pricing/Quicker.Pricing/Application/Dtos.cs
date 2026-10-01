namespace Quicker.Pricing.Application;

/// <summary>A record another module owns, as a pricing screen shows it.</summary>
public sealed record RecordRef(Guid Id, string Code, IReadOnlyDictionary<string, string> Name);

// ------------------------------------------------------------------ price lists

public sealed record SavePriceListRequest(
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Currency,
    bool PricesIncludeTax = false,
    Guid? ParentListId = null,
    decimal? ParentAdjustmentPct = null,
    decimal? RoundingIncrement = null,
    string? RoundingMode = null,
    decimal PriceSurcharge = 0m,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    int Priority = 100,
    bool IsDefault = false,
    bool IsActive = true,
    string? Notes = null,
    IReadOnlyList<Guid>? PartnerIds = null,
    IReadOnlyList<Guid>? CustomerGroupIds = null);

public sealed record PriceListSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Currency,
    bool PricesIncludeTax,
    Guid? ParentListId,
    string? ParentCode,
    decimal? ParentAdjustmentPct,
    decimal? RoundingIncrement,
    string RoundingMode,
    decimal PriceSurcharge,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    bool IsDefault,
    bool IsActive,
    string? Notes,
    int Items,
    IReadOnlyList<RecordRef> Customers,
    IReadOnlyList<RecordRef> CustomerGroups,
    DateTimeOffset UpdatedAt);

public sealed record SavePriceListItemRequest(Guid ItemId, Guid? VariantId, Guid? UomId, decimal MinQuantity, decimal Price, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

public sealed record PriceListItemSummary(
    Guid Id,
    Guid PriceListId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? VariantSku,
    Guid UomId,
    string UomCode,
    decimal MinQuantity,
    decimal Price,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    DateTimeOffset UpdatedAt);

/// <summary>Every price in a list changed by a percentage (a yearly increase), rounded to an increment when one is given.</summary>
public sealed record AdjustPriceListRequest(decimal Pct, decimal? RoundingIncrement = null, string? RoundingMode = null);

public sealed record AdjustPriceListResult(int Changed);

// ------------------------------------------------------------------ agreements

public sealed record SavePriceAgreementRequest(
    Guid CompanyId,
    Guid PartnerId,
    string? Reference,
    Guid? ItemId,
    Guid? VariantId,
    Guid? CategoryId,
    Guid? UomId,
    decimal MinQuantity,
    decimal? Price,
    string? Currency,
    decimal? DiscountPct,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true,
    string? Notes = null);

public sealed record PriceAgreementSummary(
    Guid Id,
    Guid CompanyId,
    RecordRef Partner,
    string? Reference,
    RecordRef? Item,
    Guid? VariantId,
    string? VariantSku,
    RecordRef? Category,
    Guid? UomId,
    string? UomCode,
    decimal MinQuantity,
    decimal? Price,
    string? Currency,
    decimal? DiscountPct,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    string? Notes,
    DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ discount rules

public sealed record SaveDiscountRuleRequest(
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Level,
    string ValueType,
    decimal Value,
    string? Currency = null,
    string? Combination = null,
    int Priority = 100,
    Guid? ItemId = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    Guid? PartnerId = null,
    Guid? CustomerGroupId = null,
    string? Channel = null,
    Guid? PaymentTermsId = null,
    decimal? MinQuantity = null,
    decimal? MinAmount = null,
    IReadOnlyList<int>? Weekdays = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true);

public sealed record DiscountRuleSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Level,
    string ValueType,
    decimal Value,
    string? Currency,
    string Combination,
    int Priority,
    RecordRef? Item,
    RecordRef? Category,
    RecordRef? Brand,
    RecordRef? Partner,
    RecordRef? CustomerGroup,
    string? Channel,
    RecordRef? PaymentTerms,
    decimal? MinQuantity,
    decimal? MinAmount,
    IReadOnlyList<int>? Weekdays,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ promotions

public sealed record PromotionComponentDto(Guid ItemId, decimal Quantity);

public sealed record PromotionTierDto(decimal MinQuantity, decimal DiscountPct);

public sealed record SavePromotionRequest(
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string? CouponCode = null,
    Guid? ItemId = null,
    Guid? CategoryId = null,
    Guid? BrandId = null,
    Guid? PartnerId = null,
    Guid? CustomerGroupId = null,
    string? Channel = null,
    decimal? BuyQuantity = null,
    Guid? GetItemId = null,
    decimal? GetQuantity = null,
    decimal? GetDiscountPct = null,
    int? MaxApplications = null,
    decimal? BundlePrice = null,
    string? Currency = null,
    decimal? DiscountPct = null,
    string? Combination = null,
    int Priority = 100,
    int? UsageLimit = null,
    int? UsageLimitPerCustomer = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true,
    IReadOnlyList<PromotionComponentDto>? Components = null,
    IReadOnlyList<PromotionTierDto>? Tiers = null);

public sealed record PromotionComponentSummary(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal Quantity);

public sealed record PromotionSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string? CouponCode,
    RecordRef? Item,
    RecordRef? Category,
    RecordRef? Brand,
    RecordRef? Partner,
    RecordRef? CustomerGroup,
    string? Channel,
    decimal? BuyQuantity,
    RecordRef? GetItem,
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
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    IReadOnlyList<PromotionComponentSummary> Components,
    IReadOnlyList<PromotionTierDto> Tiers,
    DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ floors

public sealed record SavePriceFloorRequest(Guid CompanyId, Guid? ItemId, Guid? CategoryId, decimal? MinPrice, string? Currency, decimal? MinMarginPct, string OnBreach = "block", bool IsActive = true);

public sealed record PriceFloorSummary(
    Guid Id,
    Guid CompanyId,
    RecordRef? Item,
    RecordRef? Category,
    decimal? MinPrice,
    string? Currency,
    decimal? MinMarginPct,
    string OnBreach,
    bool IsActive,
    DateTimeOffset UpdatedAt);

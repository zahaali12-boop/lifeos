using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Pricing.Domain;

/// <summary>app.prc_price_lists: a company's prices in one currency, its own or derived from a parent list.</summary>
public sealed class PriceList : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Currency { get; set; } = string.Empty;

    public bool PricesIncludeTax { get; set; }

    public Guid? ParentListId { get; set; }

    public decimal? ParentAdjustmentPct { get; set; }

    public decimal? RoundingIncrement { get; set; }

    public string RoundingMode { get; set; } = "nearest";

    public decimal PriceSurcharge { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public int Priority { get; set; } = 100;

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.prc_price_list_assignments: a customer or a customer group a list is for.</summary>
public sealed class PriceListAssignment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PriceListId { get; set; }

    public Guid? PartnerId { get; set; }

    public Guid? CustomerGroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>app.prc_price_list_items: an item's price per unit in a list, from a quantity and a date.</summary>
public sealed class PriceListItem : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PriceListId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid UomId { get; set; }

    public decimal MinQuantity { get; set; }

    public decimal Price { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.prc_customer_price_agreements: a net price or a discount agreed with one customer.</summary>
public sealed class PriceAgreement : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid PartnerId { get; set; }

    public string? Reference { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? UomId { get; set; }

    public decimal MinQuantity { get; set; }

    public decimal? Price { get; set; }

    public string? Currency { get; set; }

    public decimal? DiscountPct { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.prc_discount_rules: a line or document discount with its scope, conditions, value and combination.</summary>
public sealed class DiscountRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Level { get; set; } = "line";

    public Guid? ItemId { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    public Guid? PartnerId { get; set; }

    public Guid? CustomerGroupId { get; set; }

    public string? Channel { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public decimal? MinQuantity { get; set; }

    public decimal? MinAmount { get; set; }

    public int[]? Weekdays { get; set; }

    public string ValueType { get; set; } = "percentage";

    public decimal Value { get; set; }

    public string? Currency { get; set; }

    public string Combination { get; set; } = "exclusive";

    public int Priority { get; set; } = 100;

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.prc_promotions: buy X get Y, a bundle, a volume tier or a coupon.</summary>
public sealed class Promotion : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Kind { get; set; } = "coupon";

    public string? CouponCode { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    public Guid? PartnerId { get; set; }

    public Guid? CustomerGroupId { get; set; }

    public string? Channel { get; set; }

    public decimal? BuyQuantity { get; set; }

    public Guid? GetItemId { get; set; }

    public decimal? GetQuantity { get; set; }

    public decimal? GetDiscountPct { get; set; }

    public int? MaxApplications { get; set; }

    public decimal? BundlePrice { get; set; }

    public string? Currency { get; set; }

    public decimal? DiscountPct { get; set; }

    public string Combination { get; set; } = "exclusive";

    public int Priority { get; set; } = 100;

    public int? UsageLimit { get; set; }

    public int? UsageLimitPerCustomer { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PromotionComponent> Components { get; set; } = [];

    public List<PromotionTier> Tiers { get; set; } = [];
}

public sealed class PromotionComponent : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid PromotionId { get; set; }

    public Guid ItemId { get; set; }

    public decimal Quantity { get; set; }
}

public sealed class PromotionTier : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid PromotionId { get; set; }

    public decimal MinQuantity { get; set; }

    public decimal DiscountPct { get; set; }
}

/// <summary>app.prc_promotion_usages: a promotion used by a confirmed document, until the document gives it back.</summary>
public sealed class PromotionUsage : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PromotionId { get; set; }

    public Guid? PartnerId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public DateTimeOffset UsedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }
}

/// <summary>app.prc_price_floors: the lowest price or margin an item or a category may sell at.</summary>
public sealed class PriceFloor : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? CategoryId { get; set; }

    public decimal? MinPrice { get; set; }

    public string? Currency { get; set; }

    public decimal? MinMarginPct { get; set; }

    public string OnBreach { get; set; } = "block";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

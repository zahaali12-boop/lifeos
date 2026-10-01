using Quicker.Identity.Contracts;

namespace Quicker.Pricing;

/// <summary>Permission keys of pricing; the sales-manager template grants <c>pricing.*</c>.</summary>
public static class PricingPermissions
{
    public const string Read = "pricing.price.read";
    public const string PriceListManage = "pricing.price_list.manage";
    public const string AgreementManage = "pricing.agreement.manage";
    public const string PromotionManage = "pricing.promotion.manage";
    public const string FloorManage = "pricing.floor.manage";
    public const string PriceOverride = "pricing.price.override";

    public static readonly PermissionDefinition[] All =
    [
        new(Read, "pricing", "Read price lists, agreements, discounts, promotions and floors, and price a basket with its explanation"),
        new(PriceListManage, "pricing", "Create and change price lists, their prices and who they are for"),
        new(AgreementManage, "pricing", "Create and change the prices and discounts agreed with customers"),
        new(PromotionManage, "pricing", "Create and change discount rules and promotions"),
        new(FloorManage, "pricing", "Set the lowest price and margin items may sell at"),
        new(PriceOverride, "pricing", "Type a line's price or discount by hand instead of the engine's"),
    ];
}

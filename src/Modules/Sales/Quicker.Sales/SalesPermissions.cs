using Quicker.Identity.Contracts;

namespace Quicker.Sales;

/// <summary>Permission keys of order-to-cash documents (roadmap 5.4); the sales_rep template has the quote and order
/// keys, sales_manager has every sales.* key including credit release.</summary>
public static class SalesPermissions
{
    public const string QuoteRead = "sales.quote.read";
    public const string QuoteManage = "sales.quote.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(QuoteRead, "sales", "Read sales quotations"),
        new(QuoteManage, "sales", "Create, edit, send, accept and reject sales quotations"),
    ];
}

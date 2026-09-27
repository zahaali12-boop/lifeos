using Quicker.Identity.Contracts;

namespace Quicker.Sales;

/// <summary>Permission keys of order-to-cash documents (roadmap 5.4); the sales_rep template has the quote and order
/// keys, sales_manager has every sales.* key including credit release.</summary>
public static class SalesPermissions
{
    public const string QuoteRead = "sales.quote.read";
    public const string QuoteManage = "sales.quote.manage";

    // Forward-declared for the default SoD rules (RoleTemplates.cs) that pair them with partners.credit.manage and
    // receivables.writeoff.post: no order or invoice endpoint checks these yet (5.4b, 5.6), but once any sales.*
    // permission ships, a rule naming a sales.* key it does not register would silently never trip (Identity's
    // Default_sod_rules_name_permissions_that_exist_once_their_module_has_shipped test).
    public const string OrderManage = "sales.order.manage";
    public const string InvoicePost = "sales.invoice.post";

    public static readonly PermissionDefinition[] All =
    [
        new(QuoteRead, "sales", "Read sales quotations"),
        new(QuoteManage, "sales", "Create, edit, send, accept and reject sales quotations"),
        new(OrderManage, "sales", "Create, confirm, change and cancel sales orders"),
        new(InvoicePost, "sales", "Post sales invoices into the books and reverse them"),
    ];
}

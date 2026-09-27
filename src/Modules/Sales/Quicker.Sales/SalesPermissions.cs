using Quicker.Identity.Contracts;

namespace Quicker.Sales;

/// <summary>Permission keys of order-to-cash documents (roadmap 5.4); the sales_rep template has the quote and order
/// keys, sales_manager has every sales.* key. Releasing a credit-held order is an ordinary workflow approval
/// (<c>workflow.request.approve</c>), not a sales.* key of its own (ADR-0020).</summary>
public static class SalesPermissions
{
    public const string QuoteRead = "sales.quote.read";
    public const string QuoteManage = "sales.quote.manage";
    public const string OrderRead = "sales.order.read";
    public const string OrderManage = "sales.order.manage";

    // Forward-declared for the default SoD rule (RoleTemplates.cs) pairing it with receivables.writeoff.post: no
    // invoice endpoint checks it yet (5.6), but once any sales.* permission ships, a rule naming a sales.* key it
    // does not register would silently never trip (Identity's
    // Default_sod_rules_name_permissions_that_exist_once_their_module_has_shipped test).
    public const string InvoicePost = "sales.invoice.post";

    public static readonly PermissionDefinition[] All =
    [
        new(QuoteRead, "sales", "Read sales quotations"),
        new(QuoteManage, "sales", "Create, edit, send, accept and reject sales quotations"),
        new(OrderRead, "sales", "Read sales orders"),
        new(OrderManage, "sales", "Create, confirm, cancel and convert sales orders from quotations"),
        new(InvoicePost, "sales", "Post sales invoices into the books and reverse them"),
    ];
}

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
    public const string ShipmentRead = "sales.shipment.read";
    public const string ShipmentManage = "sales.shipment.manage";
    public const string ShipmentPost = "sales.shipment.post";

    public const string InvoiceRead = "sales.invoice.read";
    public const string InvoiceManage = "sales.invoice.manage";
    public const string InvoicePost = "sales.invoice.post";

    public static readonly PermissionDefinition[] All =
    [
        new(QuoteRead, "sales", "Read sales quotations"),
        new(QuoteManage, "sales", "Create, edit, send, accept and reject sales quotations"),
        new(OrderRead, "sales", "Read sales orders"),
        new(OrderManage, "sales", "Create, confirm, cancel and convert sales orders from quotations"),
        new(ShipmentRead, "sales", "Read sales shipments"),
        new(ShipmentManage, "sales", "Create and edit draft sales shipments"),
        new(ShipmentPost, "sales", "Post sales shipments into stock and reverse them"),
        new(InvoiceRead, "sales", "Read sales invoices and credit notes"),
        new(InvoiceManage, "sales", "Create, edit and delete draft sales invoices and credit notes"),
        new(InvoicePost, "sales", "Post sales invoices and credit notes into the books and reverse them"),
    ];
}

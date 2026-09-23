using Quicker.Identity.Contracts;

namespace Quicker.Purchasing;

/// <summary>Permission keys of procure-to-pay documents; the purchaser template grants them.</summary>
public static class PurchasingPermissions
{
    public const string RequisitionRead = "purchasing.requisition.read";
    public const string RequisitionManage = "purchasing.requisition.manage";
    public const string RfqRead = "purchasing.rfq.read";
    public const string RfqManage = "purchasing.rfq.manage";
    public const string AgreementRead = "purchasing.agreement.read";
    public const string AgreementManage = "purchasing.agreement.manage";
    public const string OrderRead = "purchasing.order.read";
    public const string OrderManage = "purchasing.order.manage";
    public const string OrderSend = "purchasing.order.send";
    public const string ReceiptRead = "purchasing.receipt.read";
    public const string ReceiptManage = "purchasing.receipt.manage";
    public const string ReceiptPost = "purchasing.receipt.post";
    public const string InvoiceRead = "purchasing.invoice.read";
    public const string InvoiceManage = "purchasing.invoice.manage";
    public const string InvoicePost = "purchasing.invoice.post";

    public static readonly PermissionDefinition[] All =
    [
        new(RequisitionRead, "purchasing", "Read purchase requisitions"),
        new(RequisitionManage, "purchasing", "Create, edit, submit and cancel purchase requisitions and turn approved ones into orders"),
        new(RfqRead, "purchasing", "Read requests for quotation, quotes and comparisons"),
        new(RfqManage, "purchasing", "Create requests for quotation, invite suppliers, send them, record quotes and award"),
        new(AgreementRead, "purchasing", "Read blanket purchase agreements"),
        new(AgreementManage, "purchasing", "Create, activate and close blanket purchase agreements"),
        new(OrderRead, "purchasing", "Read purchase orders, their revisions and commitments"),
        new(OrderManage, "purchasing", "Create, edit, submit, change and cancel purchase orders"),
        new(OrderSend, "purchasing", "Send an approved purchase order to the supplier"),
        new(ReceiptRead, "purchasing", "Read goods receipts"),
        new(ReceiptManage, "purchasing", "Create, edit and delete draft goods receipts"),
        new(ReceiptPost, "purchasing", "Post goods receipts into stock and reverse them"),
        new(InvoiceRead, "purchasing", "Read supplier invoices, their match results and the payables they created"),
        new(InvoiceManage, "purchasing", "Create, edit, submit and delete draft supplier invoices"),
        new(InvoicePost, "purchasing", "Post supplier invoices into the books and reverse them"),
    ];
}

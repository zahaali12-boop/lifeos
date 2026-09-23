using Quicker.Kernel.Results;

namespace Quicker.Purchasing.Contracts;

public static class PurchaseDocumentTypes
{
    public const string Requisition = "purchase_requisition";
    public const string Rfq = "purchase_rfq";
    public const string BlanketAgreement = "purchase_agreement";
    public const string Order = "purchase_order";

    public const string Receipt = "purchase_receipt";

    public const string Invoice = "purchase_invoice";
}

/// <summary>An open purchase order line as receipts (4.3) and invoices (4.4) see it: quantities in the entered unit and in the base unit, the price in the order's currency.</summary>
public sealed record PurchaseOrderLineInfo(
    Guid OrderId,
    string OrderNumber,
    Guid LineId,
    int LineNo,
    Guid CompanyId,
    Guid PartnerId,
    Guid ItemId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal QuantityBase,
    decimal QtyReceived,
    decimal QtyInvoiced,
    decimal UnitPrice,
    decimal DiscountPct,
    string Currency,
    decimal ExchangeRate,
    Guid? WarehouseId,
    DateOnly? ExpectedDate,
    string Status);

/// <summary>What receiving and invoicing read from purchase orders; the mutating side (recording receipts against lines) arrives with 4.3.</summary>
public interface IPurchaseOrderDirectory
{
    Task<IReadOnlyList<PurchaseOrderLineInfo>> OpenLinesAsync(Guid companyId, Guid? partnerId = null, Guid? orderId = null, CancellationToken cancellationToken = default);

    Task<Result<PurchaseOrderLineInfo>> FindLineAsync(Guid orderLineId, CancellationToken cancellationToken = default);
}

/// <summary>One posted receipt line as invoices (4.4), landed costs (4.5) and returns (4.6) see it: the received quantity, what was booked at the expected cost, and what is still uninvoiced and unreturned.</summary>
public sealed record PurchaseReceiptLineInfo(
    Guid ReceiptId,
    string ReceiptNumber,
    Guid LineId,
    int LineNo,
    Guid CompanyId,
    Guid PartnerId,
    Guid OrderId,
    Guid OrderLineId,
    Guid ItemId,
    Guid? VariantId,
    Guid UomId,
    decimal Quantity,
    decimal QuantityBase,
    decimal QtyInOrderUom,
    decimal QtyInvoiced,
    decimal QtyReturned,
    decimal UnitPrice,
    string Currency,
    decimal ExchangeRate,
    decimal ExpectedCostAmount,
    decimal InvoicedCostAmount,
    decimal ReturnedCostAmount,
    DateOnly PostingDate,
    Guid WarehouseId,
    Guid? SleId);

/// <summary>Posted goods receipts for the modules that settle them.</summary>
public interface IPurchaseReceiptDirectory
{
    /// <summary>Posted lines with something still uninvoiced, for a company and optionally one supplier or one order.</summary>
    Task<IReadOnlyList<PurchaseReceiptLineInfo>> OpenLinesAsync(Guid companyId, Guid? partnerId = null, Guid? orderId = null, CancellationToken cancellationToken = default);

    Task<Result<PurchaseReceiptLineInfo>> FindLineAsync(Guid receiptLineId, CancellationToken cancellationToken = default);
}

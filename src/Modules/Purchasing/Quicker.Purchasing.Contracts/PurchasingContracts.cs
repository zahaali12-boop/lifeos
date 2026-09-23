using Quicker.Kernel.Results;

namespace Quicker.Purchasing.Contracts;

public static class PurchaseDocumentTypes
{
    public const string Requisition = "purchase_requisition";
    public const string Rfq = "purchase_rfq";
    public const string BlanketAgreement = "purchase_agreement";
    public const string Order = "purchase_order";
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

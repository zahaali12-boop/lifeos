using System.Text.Json;

namespace Quicker.Sales.Application;

public sealed record SaveQuotationLineRequest(
    Guid? ItemId = null,
    string? ItemCode = null,
    Guid? VariantId = null,
    string? Description = null,
    decimal Quantity = 0m,
    string? Uom = null,
    Guid? UomId = null,
    decimal? UnitPrice = null,
    decimal DiscountPct = 0m,
    Guid? TaxCodeId = null);

public sealed record SaveQuotationRequest(
    Guid CompanyId,
    Guid PartnerId,
    IReadOnlyList<SaveQuotationLineRequest> Lines,
    string? Currency = null,
    DateOnly? QuoteDate = null,
    DateOnly? ValidUntil = null,
    DateOnly? PricingDate = null,
    Guid? PriceListId = null,
    Guid? OpportunityId = null,
    Guid? BranchId = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record RejectQuotationRequest(string Reason);

public sealed record QuotationLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? Description,
    decimal Quantity,
    Guid UomId,
    string UomCode,
    decimal QuantityBase,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal NetAmount,
    decimal TaxAmount,
    Guid? PromotionId,
    string? PromotionCode,
    Guid? TaxCodeId = null,
    string? TaxCode = null,
    decimal TaxRatePct = 0m,
    bool TaxReverseCharge = false,
    bool TaxRecoverable = true,
    string? TaxReason = null);

public sealed record QuotationSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid? OpportunityId,
    string Currency,
    DateOnly QuoteDate,
    DateOnly? ValidUntil,
    DateOnly PricingDate,
    Guid? PriceListId,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string? RejectionReason,
    string? Notes,
    JsonElement CustomFields,
    DateTimeOffset? SentAt,
    DateTimeOffset? DecidedAt,
    Guid? OrderId,
    IReadOnlyList<QuotationLineSummary> Lines,
    DateTimeOffset UpdatedAt,
    bool IsExpired = false);

public sealed record SaveOrderLineRequest(
    Guid? ItemId = null,
    string? ItemCode = null,
    Guid? VariantId = null,
    string? Description = null,
    decimal Quantity = 0m,
    string? Uom = null,
    Guid? UomId = null,
    decimal? UnitPrice = null,
    decimal DiscountPct = 0m,
    Guid? TaxCodeId = null,
    Guid? WarehouseId = null,
    bool DropShip = false);

public sealed record SaveOrderRequest(
    Guid CompanyId,
    Guid PartnerId,
    Guid WarehouseId,
    IReadOnlyList<SaveOrderLineRequest> Lines,
    string? Currency = null,
    DateOnly? OrderDate = null,
    DateOnly? PricingDate = null,
    Guid? PriceListId = null,
    Guid? BranchId = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record ConvertQuotationRequest(Guid WarehouseId, DateOnly? OrderDate = null);

public sealed record CancelOrderRequest(string Reason);

public sealed record CancelOrderLineRequest(decimal Quantity);

public sealed record LinkPurchaseOrderLineRequest(Guid PurchaseOrderLineId);

public sealed record OrderLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? Description,
    decimal Quantity,
    Guid UomId,
    string UomCode,
    decimal QuantityBase,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal NetAmount,
    decimal TaxAmount,
    Guid? PromotionId,
    string? PromotionCode,
    Guid? WarehouseId,
    decimal QtyReserved,
    decimal QtyShipped,
    decimal QtyInvoiced,
    decimal QtyCancelled,
    string Status,
    bool DropShip = false,
    Guid? PurchaseOrderLineId = null,
    Guid? TaxCodeId = null,
    string? TaxCode = null,
    decimal TaxRatePct = 0m,
    bool TaxReverseCharge = false,
    bool TaxRecoverable = true,
    string? TaxReason = null);

public sealed record OrderSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid? QuotationId,
    string Currency,
    DateOnly OrderDate,
    DateOnly PricingDate,
    Guid? PriceListId,
    Guid WarehouseId,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string? BlockKind,
    string? BlockReason,
    string? Notes,
    JsonElement CustomFields,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<OrderLineSummary> Lines,
    DateTimeOffset UpdatedAt);

/// <summary>A shipment line's quantity is always the item's base unit, exactly like the reservation and
/// <c>qty_shipped</c> it consumes (A-152's own reasoning extended here) -- there is no unit to convert.</summary>
public sealed record SaveShipmentLineRequest(Guid OrderLineId, decimal Quantity, Guid? BinId = null);

public sealed record SaveShipmentRequest(
    Guid OrderId,
    IReadOnlyList<SaveShipmentLineRequest> Lines,
    DateOnly? PostingDate = null,
    string? Carrier = null,
    string? TrackingNumber = null,
    string? Notes = null,
    Guid? BranchId = null,
    JsonElement? CustomFields = null);

public sealed record ReverseShipmentRequest(string Reason, DateOnly? ReversalDate = null);

/// <summary>One part of a shipment line's quantity: the bin and lot it comes from, its serials, and once posted the
/// stock ledger entries it produced (what a reversal returns, entry by entry).</summary>
public sealed record ShipmentAllocation(
    Guid? BinId,
    string? BinCode,
    Guid? LotId,
    string? LotNumber,
    DateOnly? ExpiresOn,
    IReadOnlyList<string> SerialNumbers,
    decimal Quantity,
    IReadOnlyList<ShipmentAllocationEntry>? Entries = null);

public sealed record ShipmentAllocationEntry(Guid SleId, string? SerialNumber, decimal Quantity);

public sealed record ShipmentLineSummary(
    Guid Id,
    int LineNo,
    Guid OrderLineId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    decimal Quantity,
    Guid UomId,
    string UomCode,
    Guid? BinId,
    string? LotNumber,
    IReadOnlyList<string> SerialNumbers,
    decimal CogsAmount,
    IReadOnlyList<ShipmentAllocation> Allocations,
    decimal? QtyPicked,
    decimal QtyPacked);

public sealed record ShipmentPackageLineSummary(Guid OrderLineId, int OrderLineNo, Guid ItemId, string ItemCode, decimal Quantity);

public sealed record ShipmentPackageSummary(
    Guid Id,
    int PackageNo,
    string PackageNumber,
    string PackageType,
    decimal? WeightKg,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    string? TrackingNumber,
    decimal? ContentsWeightKg,
    IReadOnlyList<ShipmentPackageLineSummary> Contents);

public sealed record ShipmentSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    Guid OrderId,
    string OrderNumber,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid WarehouseId,
    DateOnly PostingDate,
    string Status,
    string? Carrier,
    string? TrackingNumber,
    decimal TotalCogs,
    string? Notes,
    JsonElement CustomFields,
    string? ReversalReason,
    DateTimeOffset? PostedAt,
    IReadOnlyList<ShipmentLineSummary> Lines,
    DateTimeOffset UpdatedAt,
    Guid? PickListId,
    string? PickListNumber,
    string? PickListStatus,
    IReadOnlyList<ShipmentPackageSummary> Packages,
    decimal? TotalWeightKg);

public sealed record CancelPickingRequest(string Reason);

public sealed record SavePackageLineRequest(Guid OrderLineId, decimal Quantity);

public sealed record SavePackageRequest(
    IReadOnlyList<SavePackageLineRequest> Contents,
    string? PackageType = null,
    decimal? WeightKg = null,
    decimal? LengthCm = null,
    decimal? WidthCm = null,
    decimal? HeightCm = null,
    string? TrackingNumber = null);

/// <summary>The shipment's packages as a whole: what is sent replaces what was there.</summary>
public sealed record SavePackagesRequest(IReadOnlyList<SavePackageRequest> Packages);

/// <summary>The carrier and its tracking reference, often only known once the goods have left.</summary>
public sealed record SaveCarrierRequest(string? Carrier, string? TrackingNumber);

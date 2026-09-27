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

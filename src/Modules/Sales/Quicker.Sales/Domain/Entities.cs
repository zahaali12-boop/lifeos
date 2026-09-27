using Quicker.Persistence.EntityFramework;

namespace Quicker.Sales.Domain;

/// <summary>A priced offer to a customer (DOMAIN_MODEL §11): drafted with lines through the pricing and tax engines,
/// sent, accepted or rejected, and converted to a sales order once accepted.</summary>
public sealed class SalesQuotation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid PartnerId { get; set; }

    public Guid? OpportunityId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateOnly QuoteDate { get; set; }

    public DateOnly? ValidUntil { get; set; }

    public DateOnly PricingDate { get; set; }

    public Guid? PriceListId { get; set; }

    public string Status { get; set; } = "draft";

    public string? RejectionReason { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalGross { get; set; }

    public string CustomerSnapshot { get; set; } = "{}";

    public Guid? OrderId { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SalesQuotationLine> Lines { get; } = [];
}

public sealed class SalesQuotationLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid QuotationId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountPct { get; set; }

    public decimal DiscountAmount { get; set; }

    public Guid? TaxCodeId { get; set; }

    public decimal TaxRatePct { get; set; }

    public bool TaxReverseCharge { get; set; }

    public bool TaxRecoverable { get; set; } = true;

    public string? TaxReason { get; set; }

    public decimal NetAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public Guid? PromotionId { get; set; }

    public string PriceBreakdown { get; set; } = "[]";
}

/// <summary>A confirmed commitment to a customer (DOMAIN_MODEL §11, roadmap 5.4b): converted from an accepted
/// quotation or created directly, reserving stock and checking credit exposure on confirmation.</summary>
public sealed class SalesOrder : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid PartnerId { get; set; }

    public Guid? QuotationId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateOnly OrderDate { get; set; }

    public DateOnly PricingDate { get; set; }

    public Guid? PriceListId { get; set; }

    public Guid WarehouseId { get; set; }

    public string Status { get; set; } = "draft";

    public string? BlockKind { get; set; }

    public Guid? BlockId { get; set; }

    public string? BlockReason { get; set; }

    public Guid? OverrideId { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalGross { get; set; }

    public string CustomerSnapshot { get; set; } = "{}";

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SalesOrderLine> Lines { get; } = [];
}

public sealed class SalesOrderLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountPct { get; set; }

    public decimal DiscountAmount { get; set; }

    public Guid? TaxCodeId { get; set; }

    public decimal TaxRatePct { get; set; }

    public bool TaxReverseCharge { get; set; }

    public bool TaxRecoverable { get; set; } = true;

    public string? TaxReason { get; set; }

    public decimal NetAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public Guid? PromotionId { get; set; }

    public string PriceBreakdown { get; set; } = "[]";

    public Guid? WarehouseId { get; set; }

    public decimal QtyReserved { get; set; }

    public decimal QtyShipped { get; set; }

    public decimal QtyInvoiced { get; set; }

    public decimal QtyCancelled { get; set; }

    public string Status { get; set; } = "open";

    public bool DropShip { get; set; }

    public Guid? PurchaseOrderLineId { get; set; }
}

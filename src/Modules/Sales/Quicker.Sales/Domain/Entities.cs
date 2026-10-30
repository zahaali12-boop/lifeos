using Quicker.Kernel.Text;
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

    /// <summary>The cost centre and other dimensions the line is charged to (A-157).</summary>
    public Guid? DimensionSetId { get; set; }

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

    /// <summary>The cost centre and other dimensions the line is charged to (A-157).</summary>
    public Guid? DimensionSetId { get; set; }

    public string PriceBreakdown { get; set; } = "[]";

    public Guid? WarehouseId { get; set; }

    public decimal QtyReserved { get; set; }

    public decimal QtyShipped { get; set; }

    public decimal QtyInvoiced { get; set; }

    /// <summary>What the line's net has been invoiced so far, so the last invoice of a line takes the exact remainder (5.6a).</summary>
    public decimal NetInvoiced { get; set; }

    public decimal QtyCancelled { get; set; }

    public string Status { get; set; } = "open";

    public bool DropShip { get; set; }

    public Guid? PurchaseOrderLineId { get; set; }
}

/// <summary>A shipment of a confirmed order's reserved lines (roadmap 5.5a): posts as
/// <c>StockEntryTypes.SaleShipment</c>, consuming the reservation and letting the costing engine value and book cost
/// of goods sold exactly as it already does for every outbound movement (ADR-0008); nothing new there, only this
/// document. A partial shipment leaves the remainder reserved for a later one.</summary>
public sealed class SalesShipment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid OrderId { get; set; }

    public Guid PartnerId { get; set; }

    public Guid WarehouseId { get; set; }

    public DateOnly PostingDate { get; set; }

    public string Status { get; set; } = "draft";

    public string? Carrier { get; set; }

    public string? TrackingNumber { get; set; }

    /// <summary>The pick list the shipment was released to (roadmap 5.5b part 3, A-154); null when it posts without one.</summary>
    public Guid? PickListId { get; set; }

    public Guid? StockPostingId { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalPostingId { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? PostedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SalesShipmentLine> Lines { get; } = [];

    public List<SalesShipmentPackage> Packages { get; } = [];
}

public sealed class SalesShipmentLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ShipmentId { get; set; }

    public int LineNo { get; set; }

    public Guid OrderLineId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public Guid? BinId { get; set; }

    public string? LotNumber { get; set; }

    public string SerialNumbers { get; set; } = "[]";

    /// <summary>Where the quantity comes from, per bin and lot (<see cref="Application.ShipmentAllocation"/>), with the stock ledger entries once posted.</summary>
    public string Allocations { get; set; } = "[]";

    /// <summary>The order line's dimensions, carried to the cost of goods sold of the movement (A-157).</summary>
    public Guid? DimensionSetId { get; set; }

    public decimal CogsAmount { get; set; }

    /// <summary>What the line has been invoiced so far, in base units like its quantity (5.6a).</summary>
    public decimal QtyInvoiced { get; set; }

    public Guid? SleId { get; set; }

    public string SleIds { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A box, pallet or envelope of a shipment (roadmap 5.5b part 3, A-154): its size and weight for the carrier,
/// the carrier's tracking number, and which order lines it holds how much of.</summary>
public sealed class SalesShipmentPackage : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ShipmentId { get; set; }

    public int PackageNo { get; set; }

    public string PackageNumber { get; set; } = string.Empty;

    /// <summary>box, carton, pallet, envelope, crate, drum, bag or other.</summary>
    public string PackageType { get; set; } = "box";

    public decimal? WeightKg { get; set; }

    public decimal? LengthCm { get; set; }

    public decimal? WidthCm { get; set; }

    public decimal? HeightCm { get; set; }

    public string? TrackingNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<SalesShipmentPackageLine> Lines { get; } = [];
}

public sealed class SalesShipmentPackageLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PackageId { get; set; }

    public Guid OrderLineId { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>A sales invoice or credit note (DOMAIN_MODEL §11, roadmap 5.6a): lines against posted shipment lines, service
/// lines of orders, directly priced non-stock items or free lines on an account role; a credit note credits an
/// invoice's lines or stands on its own. Posting books Dr receivables gross / Cr revenue net per line / Cr output tax
/// per code (POSTING_RULES §2) and opens one receivable open item per payment-terms instalment; a posted document
/// is reversed, never edited.</summary>
public sealed class SalesInvoice : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    /// <summary>invoice or credit_note.</summary>
    public string Kind { get; set; } = "invoice";

    public Guid PartnerId { get; set; }

    /// <summary>The customer's own reference (its purchase order number), printed and carried to the open item.</summary>
    public string? CustomerReference { get; set; }

    /// <summary>For a credit note: the invoice it credits, applied to automatically once posted.</summary>
    public Guid? CreditsInvoiceId { get; set; }

    public DateOnly DocumentDate { get; set; }

    public DateOnly PostingDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public Guid? PaymentTermsId { get; set; }

    public Guid? SalesRepId { get; set; }

    public Guid? PriceListId { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalGross { get; set; }

    public string TaxRoundingLevel { get; set; } = "line";

    public bool PricesIncludeTax { get; set; }

    public string Status { get; set; } = "draft";

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    /// <summary>not_required when the regime names no e-invoicing scheme; pending once posted under one; then the clearance adapter's own status (5.6b).</summary>
    public string EinvoiceStatus { get; set; } = "not_required";

    public string? EinvoiceScheme { get; set; }

    public string? EinvoiceSubmissionId { get; set; }

    public string? EinvoiceQr { get; set; }

    public string EinvoiceMessages { get; set; } = "[]";

    public DateTimeOffset? EinvoiceSubmittedAt { get; set; }

    /// <summary>The recurring invoice that generated this one, when one did.</summary>
    public Guid? RecurringInvoiceId { get; set; }

    public string CustomerSnapshot { get; set; } = "{}";

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? PostedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SalesInvoiceLine> Lines { get; } = [];
}

public sealed class SalesInvoiceLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid InvoiceId { get; set; }

    public int LineNo { get; set; }

    /// <summary>shipment (a posted shipment line), order (a service line of an order), item (a non-stock item priced here), service (a free line on an account role) or invoice (a credit note's line against an invoice line).</summary>
    public string Kind { get; set; } = "shipment";

    public Guid? ShipmentLineId { get; set; }

    public Guid? OrderLineId { get; set; }

    public Guid? InvoiceLineId { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public string? AccountRole { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid? UomId { get; set; }

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

    public decimal NetAmountFc { get; set; }

    public decimal QtyCredited { get; set; }

    public decimal NetCredited { get; set; }

    /// <summary>The cost centre and other dimensions the revenue is credited to (A-157).</summary>
    public Guid? DimensionSetId { get; set; }

    public string PriceBreakdown { get; set; } = "[]";

    /// <summary>Set when the line's revenue is deferred: parked on deferred revenue and released over this many monthly periods from the start date by Accounting's schedule (5.6b).</summary>
    public int? DeferralPeriods { get; set; }

    public DateOnly? DeferralStartsOn { get; set; }

    public Guid? DeferralScheduleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A recurring invoice (roadmap 5.6b): the lines of a direct invoice issued to a customer on a cron schedule in the company's time zone, posted at once or left as a draft to review, as the recurring journals are.</summary>
public sealed class SalesRecurringInvoice : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid PartnerId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Cron { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public DateOnly? NextRunOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>The invoice lines as a json array of line requests (items and service lines only).</summary>
    public string Lines { get; set; } = "[]";

    public string? CustomerReference { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public bool AutoPost { get; set; }

    public bool IsActive { get; set; } = true;

    public DateOnly? LastGeneratedOn { get; set; }

    public string? Notes { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What a sales rep earned on an invoice line under their plan (DOMAIN_MODEL §11): basis and commission in the plan's currency, booked with the invoice and reversed with it.</summary>
public sealed class SalesCommissionEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid InvoiceId { get; set; }

    public Guid InvoiceLineId { get; set; }

    public Guid SalesRepId { get; set; }

    public Guid PlanId { get; set; }

    public DateOnly PostingDate { get; set; }

    public string Basis { get; set; } = "revenue";

    public decimal BasisAmount { get; set; }

    public decimal CommissionAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Status { get; set; } = "accrued";

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Purchasing.Domain;

public sealed class Requisition : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid? RequesterMembershipId { get; set; }

    public Guid? DepartmentValueId { get; set; }

    public DateOnly? NeededBy { get; set; }

    public string Status { get; set; } = "draft";

    public string? Justification { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal TotalEstimated { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? RejectionReason { get; set; }

    public string CustomFields { get; set; } = "{}";

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<RequisitionLine> Lines { get; } = [];
}

public sealed class RequisitionLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RequisitionId { get; set; }

    public int LineNo { get; set; }

    public Guid? ItemId { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public decimal? EstimatedPrice { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public Guid? SuggestedSupplierId { get; set; }

    public decimal QtyOrdered { get; set; }

    public string Status { get; set; } = "open";
}

public sealed class Rfq : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string? Title { get; set; }

    public DateOnly? DueOn { get; set; }

    public string Status { get; set; } = "draft";

    public Guid? AwardedQuoteId { get; set; }

    public string? Notes { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<RfqLine> Lines { get; } = [];

    public List<RfqSupplier> Suppliers { get; } = [];
}

public sealed class RfqLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RfqId { get; set; }

    public int LineNo { get; set; }

    public Guid? ItemId { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public Guid? RequisitionLineId { get; set; }
}

public sealed class RfqSupplier : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RfqId { get; set; }

    public Guid PartnerId { get; set; }

    public string? ContactEmail { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string Status { get; set; } = "invited";
}

public sealed class SupplierQuote : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RfqSupplierId { get; set; }

    public string? SupplierReference { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateOnly? ValidUntil { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public int LeadTimeDays { get; set; }

    public decimal FreightAmount { get; set; }

    public decimal OtherCharges { get; set; }

    public string? Notes { get; set; }

    public string? ComparisonScore { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SupplierQuoteLine> Lines { get; } = [];
}

public sealed class SupplierQuoteLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid QuoteId { get; set; }

    public Guid RfqLineId { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public int? LeadTimeDays { get; set; }
}

public sealed class BlanketAgreement : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid PartnerId { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly ValidTo { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal CommittedAmount { get; set; }

    public decimal ReleasedAmount { get; set; }

    public string Status { get; set; } = "draft";

    public string? Notes { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<BlanketLine> Lines { get; } = [];
}

public sealed class BlanketLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid AgreementId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public Guid UomId { get; set; }

    public decimal AgreedQty { get; set; }

    public decimal AgreedPrice { get; set; }

    public decimal ReleasedQty { get; set; }
}

public sealed class PurchaseOrder : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid PartnerId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public DateOnly OrderDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public Guid? DeliveryTermsId { get; set; }

    public Guid? WarehouseId { get; set; }

    public string Status { get; set; } = "draft";

    public int Revision { get; set; } = 1;

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalGross { get; set; }

    public string TaxRoundingLevel { get; set; } = "line";

    public string SupplierSnapshot { get; set; } = "{}";

    public Guid? ApprovalRequestId { get; set; }

    public string? RejectionReason { get; set; }

    public Guid? RequisitionId { get; set; }

    public Guid? RfqId { get; set; }

    public Guid? AgreementId { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? SentTo { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PurchaseOrderLine> Lines { get; } = [];
}

public sealed class PurchaseOrderLine : ITenantEntity
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

    public Guid? TaxCodeId { get; set; }

    public decimal NetAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TaxRatePct { get; set; }

    /// <summary>The company self-assesses this tax (reverse charge); the supplier does not charge it.</summary>
    public bool TaxReverseCharge { get; set; }

    public bool TaxRecoverable { get; set; } = true;

    /// <summary>Why the line has its code: exemption, rule, chosen or not_registered (A-146).</summary>
    public string? TaxReason { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public decimal QtyReceived { get; set; }

    public decimal QtyInvoiced { get; set; }

    public decimal QtyCancelled { get; set; }

    public Guid? RequisitionLineId { get; set; }

    public Guid? BlanketLineId { get; set; }

    public string Status { get; set; } = "open";
}

public sealed class PurchaseOrderRevision : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public int Revision { get; set; }

    public string Snapshot { get; set; } = "{}";

    public string? Reason { get; set; }

    public Guid? ChangedBy { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class Commitment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid OrderId { get; set; }

    public Guid OrderLineId { get; set; }

    public string AccountRole { get; set; } = string.Empty;

    public Guid? DimensionSetId { get; set; }

    public string PeriodKey { get; set; } = string.Empty;

    public decimal AmountFc { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal AmountRc { get; set; }

    public decimal ConsumedRc { get; set; }

    public string Status { get; set; } = "open";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A goods receipt against one purchase order: drafted, posted through the stock engine at the expected cost with GRNI as the offset, reversed as a whole.</summary>
public sealed class Receipt : ITenantEntity
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

    public string? SupplierDeliveryNote { get; set; }

    public string Status { get; set; } = "draft";

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public decimal TotalExpectedCost { get; set; }

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

    public List<ReceiptLine> Lines { get; } = [];
}

public sealed class ReceiptLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ReceiptId { get; set; }

    public int LineNo { get; set; }

    public Guid OrderLineId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    /// <summary>The received quantity expressed in the order line's unit, what the order's received quantity counts.</summary>
    public decimal QtyInOrderUom { get; set; }

    public Guid? BinId { get; set; }

    public string? LotNumber { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public string SerialNumbers { get; set; } = "[]";

    /// <summary>The order line's price after its discount, per order unit, in the order currency.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The expected cost per received unit in the company's currency (order price at the receipt-date rate).</summary>
    public decimal ExpectedUnitCost { get; set; }

    /// <summary>What the stock engine booked on Inventory / GRNI for this line, in the company's currency.</summary>
    public decimal ExpectedCostAmount { get; set; }

    public decimal InvoicedCostAmount { get; set; }

    public decimal ReturnedCostAmount { get; set; }

    public decimal QtyInvoiced { get; set; }

    public decimal QtyReturned { get; set; }

    public Guid? SleId { get; set; }

    /// <summary>Every stock entry the line posted (one per unit for serialised items), as a JSON array of ids.</summary>
    public string SleIds { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A supplier invoice: matched to receipts (three-way), to order lines for services (two-way) or free expense lines; posted against GRNI and AP.</summary>
public sealed class Invoice : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Kind { get; set; } = "invoice";

    public Guid PartnerId { get; set; }

    public string? SupplierInvoiceNumber { get; set; }

    public DateOnly DocumentDate { get; set; }

    public DateOnly PostingDate { get; set; }

    public DateOnly? DueDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public Guid? PaymentTermsId { get; set; }

    public Guid? WhtCodeId { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    /// <summary>Tax the company self-assesses on the invoice (reverse charge): reported and posted, never paid to the supplier.</summary>
    public decimal TotalReverseChargeTax { get; set; }

    public string TaxRoundingLevel { get; set; } = "line";

    public decimal TotalWht { get; set; }

    public decimal TotalGross { get; set; }

    public decimal TotalPayable { get; set; }

    public string Status { get; set; } = "draft";

    public string? BlockKind { get; set; }

    public string? BlockReason { get; set; }

    public Guid? BlockId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? RejectionReason { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? PostedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<InvoiceLine> Lines { get; } = [];
}

public sealed class InvoiceLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid InvoiceId { get; set; }

    public int LineNo { get; set; }

    /// <summary>receipt (three-way), order (two-way, services) or expense (free line on an account role).</summary>
    public string Kind { get; set; } = "receipt";

    public Guid? ReceiptLineId { get; set; }

    public Guid? OrderLineId { get; set; }

    public Guid? ItemId { get; set; }

    public string? AccountRole { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public Guid? UomId { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountPct { get; set; }

    public Guid? TaxCodeId { get; set; }

    public decimal NetAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TaxRatePct { get; set; }

    /// <summary>The company self-assesses this tax (reverse charge); the supplier does not charge it.</summary>
    public bool TaxReverseCharge { get; set; }

    public bool TaxRecoverable { get; set; } = true;

    /// <summary>Why the line has its code: exemption, rule, chosen or not_registered (A-146).</summary>
    public string? TaxReason { get; set; }

    public decimal WhtAmount { get; set; }

    public decimal NetAmountFc { get; set; }

    /// <summary>The order price after its discount the line is matched against, per unit.</summary>
    public decimal? ExpectedUnitPrice { get; set; }

    public decimal? PriceVariancePct { get; set; }

    public decimal? QtyVariance { get; set; }

    public Guid? DimensionSetId { get; set; }

    /// <summary>For a charge line: the landed-cost charge the invoice settles.</summary>
    public Guid? LandedCostChargeId { get; set; }

    /// <summary>For a return line of a debit note: the supplier-return line it credits.</summary>
    public Guid? ReturnLineId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MatchResult : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid InvoiceId { get; set; }

    public string Status { get; set; } = "matched";

    public decimal PriceTolerancePct { get; set; }

    public decimal QtyTolerancePct { get; set; }

    public decimal PriceVarianceAmount { get; set; }

    public decimal PriceVariancePct { get; set; }

    public decimal QtyVariance { get; set; }

    public string Details { get; set; } = "[]";

    public Guid? OverrideId { get; set; }

    public DateTimeOffset MatchedAt { get; set; }
}

/// <summary>A kind of landed cost (freight, customs, duty, insurance, handling) with the basis it is allocated by unless a charge says otherwise.</summary>
public sealed class ChargeType : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string DefaultAllocationBasis { get; set; } = "value";

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Charges allocated onto posted receipt lines: estimated against the clearing account, settled by the charge invoices, pushed to consumption for what was already sold.</summary>
public sealed class LandedCostDocument : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public DateOnly PostingDate { get; set; }

    public string Status { get; set; } = "draft";

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public decimal TotalAmount { get; set; }

    public decimal TotalAmountFc { get; set; }

    public decimal OnHandPortionFc { get; set; }

    public decimal SoldPortionFc { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? PostedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<LandedCostCharge> Charges { get; } = [];

    public List<LandedCostAllocation> Allocations { get; } = [];
}

public sealed class LandedCostCharge : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LandedCostId { get; set; }

    public int LineNo { get; set; }

    public Guid ChargeTypeId { get; set; }

    public Guid? PartnerId { get; set; }

    public string? Description { get; set; }

    public decimal Amount { get; set; }

    public decimal AmountFc { get; set; }

    public string AllocationBasis { get; set; } = "value";

    public bool IsEstimate { get; set; } = true;

    public Guid? SupplierInvoiceLineId { get; set; }

    public decimal InvoicedAmountFc { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LandedCostAllocation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LandedCostId { get; set; }

    public Guid ChargeId { get; set; }

    public Guid ReceiptLineId { get; set; }

    public decimal BasisValue { get; set; }

    public decimal AllocatedAmountFc { get; set; }

    public decimal OnHandPortionFc { get; set; }

    public decimal SoldPortionFc { get; set; }

    public Guid? AdjustmentRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Goods sent back to the supplier from one receipt, out of stock at the exact cost they came in at, GRNI as the offset until the debit note credits them.</summary>
public sealed class SupplierReturn : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid ReceiptId { get; set; }

    public Guid PartnerId { get; set; }

    public Guid WarehouseId { get; set; }

    public DateOnly PostingDate { get; set; }

    public string Status { get; set; } = "draft";

    public string Currency { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? SupplierRma { get; set; }

    public decimal TotalCostFc { get; set; }

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

    public List<SupplierReturnLine> Lines { get; } = [];
}

public sealed class SupplierReturnLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ReturnId { get; set; }

    public int LineNo { get; set; }

    public Guid ReceiptLineId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal QuantityBase { get; set; }

    public Guid? BinId { get; set; }

    public string? LotNumber { get; set; }

    public string SerialNumbers { get; set; } = "[]";

    public string? Reason { get; set; }

    /// <summary>What the stock engine took out, at the receipt's exact cost, in the company's currency (positive).</summary>
    public decimal CostAmountFc { get; set; }

    public decimal CreditedAmountFc { get; set; }

    public decimal QtyCredited { get; set; }

    public Guid? SleId { get; set; }

    public string SleIds { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}


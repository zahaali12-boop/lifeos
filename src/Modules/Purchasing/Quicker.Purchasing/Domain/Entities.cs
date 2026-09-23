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

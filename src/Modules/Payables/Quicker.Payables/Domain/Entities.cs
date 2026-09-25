using Quicker.Persistence.EntityFramework;

namespace Quicker.Payables.Domain;

/// <summary>One payable open item (DOMAIN_MODEL §13): an invoice instalment, a debit note, an advance or a payment on account; amounts are signed (positive owed to the supplier).</summary>
public sealed class ApOpenItem : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid PartnerId { get; set; }

    public string Kind { get; set; } = "invoice";

    public string DocumentType { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public int Instalment { get; set; } = 1;

    public string? SupplierReference { get; set; }

    public DateOnly PostingDate { get; set; }

    public DateOnly DocumentDate { get; set; }

    public DateOnly DueDate { get; set; }

    public DateOnly? DiscountDate { get; set; }

    public decimal DiscountPct { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal OriginalTc { get; set; }

    public decimal OriginalFc { get; set; }

    public decimal BookedRate { get; set; } = 1m;

    public decimal SettledTc { get; set; }

    public decimal SettledFc { get; set; }

    public decimal RemainingTc { get; set; }

    public decimal RemainingFc { get; set; }

    public Guid? JournalEntryId { get; set; }

    public bool PaymentBlocked { get; set; }

    public string? BlockReason { get; set; }

    public Guid? HeldBy { get; set; }

    public DateTimeOffset? HeldAt { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public string Status { get; set; } = "open";

    public DateOnly? ReversedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A settlement between two open items; a reversal is a mirror row (negative amounts) naming the row it reverses.</summary>
public sealed class ApSettlement : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid SettlingItemId { get; set; }

    public Guid SettledItemId { get; set; }

    public DateOnly SettlementDate { get; set; }

    public string Kind { get; set; } = "credit_application";

    public string Currency { get; set; } = string.Empty;

    public decimal AmountTc { get; set; }

    public decimal AmountFcSettledItem { get; set; }

    public decimal AmountFcSettlingItem { get; set; }

    public decimal SettlementRate { get; set; } = 1m;

    public decimal FxGainLossFc { get; set; }

    public decimal DiscountTakenTc { get; set; }

    public decimal WhtWithheldTc { get; set; }

    public decimal WriteOffTc { get; set; }

    public decimal BankChargeTc { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversesSettlementId { get; set; }

    public string? Reason { get; set; }

    public string Status { get; set; } = "posted";

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PaymentProposal : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public DateOnly RunDate { get; set; }

    public DateOnly PayThrough { get; set; }

    public string Currency { get; set; } = string.Empty;

    public Guid? BankAccountId { get; set; }

    public Guid? PartnerId { get; set; }

    public string Status { get; set; } = "draft";

    public decimal TotalTc { get; set; }

    public decimal DiscountTc { get; set; }

    public string? Notes { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PaymentProposalLine> Lines { get; } = [];
}

public sealed class PaymentProposalLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ProposalId { get; set; }

    public Guid OpenItemId { get; set; }

    public Guid PartnerId { get; set; }

    public decimal AmountTc { get; set; }

    public decimal DiscountTc { get; set; }

    public bool Selected { get; set; } = true;

    public Guid? PaymentId { get; set; }
}

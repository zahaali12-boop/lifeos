using Quicker.Persistence.EntityFramework;

namespace Quicker.Receivables.Domain;

/// <summary>One receivable open item (DOMAIN_MODEL §13): an invoice instalment, a credit note, a deposit or a receipt on account; amounts are signed (positive owed by the customer).</summary>
public sealed class ArOpenItem : ITenantEntity
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

    public string? CustomerReference { get; set; }

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

    public Guid? SalesRepId { get; set; }

    public bool OnHold { get; set; }

    public string? HoldReason { get; set; }

    public Guid? HeldBy { get; set; }

    public DateTimeOffset? HeldAt { get; set; }

    public int DunningLevel { get; set; }

    public DateOnly? PromisedPaymentDate { get; set; }

    public string? PromiseNote { get; set; }

    public DateTimeOffset? PromisedAt { get; set; }

    public Guid? PromisedBy { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? DimensionSetId { get; set; }

    public string Status { get; set; } = "open";

    public DateOnly? ReversedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A settlement between two open items; a reversal is a mirror row (negative amounts) naming the row it reverses.</summary>
public sealed class ArSettlement : ITenantEntity
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

    public decimal WhtDeductedTc { get; set; }

    public decimal WriteOffTc { get; set; }

    public decimal BankChargeTc { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversesSettlementId { get; set; }

    public string? Reason { get; set; }

    public string Status { get; set; } = "posted";

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A write-off of what a customer still owes on one item (bad debt, or a small balance not worth chasing): a document with its journal, the credit item it opened and the settlement that closed the written-off item with it.</summary>
public sealed class ArWriteOff : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid PartnerId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid OpenItemId { get; set; }

    public Guid? AdjustmentItemId { get; set; }

    public Guid? SettlementId { get; set; }

    public DateOnly WriteOffDate { get; set; }

    public string Kind { get; set; } = "bad_debt";

    public string Currency { get; set; } = string.Empty;

    public decimal AmountTc { get; set; }

    public decimal AmountFc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Status { get; set; } = "posted";

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

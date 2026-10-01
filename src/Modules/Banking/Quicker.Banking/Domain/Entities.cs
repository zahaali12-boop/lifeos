using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Banking.Domain;

public sealed class BankAccount : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Kind { get; set; } = "bank";

    public string Currency { get; set; } = string.Empty;

    public Guid GlAccountId { get; set; }

    public string? BankName { get; set; }

    public string? BranchName { get; set; }

    public string? AccountNumber { get; set; }

    public string? Iban { get; set; }

    public string? Swift { get; set; }

    public Guid? BranchId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One movement of a bank or cash account, signed in the account's currency; the control account's journal line references it.</summary>
public sealed class BankTransaction : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid BankAccountId { get; set; }

    public Guid CompanyId { get; set; }

    public DateOnly PostingDate { get; set; }

    public DateOnly ValueDate { get; set; }

    public string Kind { get; set; } = "disbursement";

    public decimal AmountTc { get; set; }

    public decimal AmountFc { get; set; }

    public string? Reference { get; set; }

    public string? SourceDocumentType { get; set; }

    public Guid? SourceDocumentId { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? ReversesTransactionId { get; set; }

    public string ReconciliationStatus { get; set; } = "unreconciled";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Payment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Kind { get; set; } = "supplier_payment";

    public Guid PartnerId { get; set; }

    public Guid BankAccountId { get; set; }

    public DateOnly PaymentDate { get; set; }

    public string Method { get; set; } = "transfer";

    public string? Reference { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal ExchangeRate { get; set; } = 1m;

    public string Status { get; set; } = "draft";

    public decimal AmountTc { get; set; }

    public decimal OnAccountTc { get; set; }

    public decimal DiscountTc { get; set; }

    public decimal WhtTc { get; set; }

    public decimal ChargesBank { get; set; }

    public decimal BankAmount { get; set; }

    public string BankCurrency { get; set; } = string.Empty;

    public bool ApplyWht { get; set; } = true;

    public Guid? WhtCodeId { get; set; }

    public Guid? OpenItemId { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? BankTransactionId { get; set; }

    public Guid? ReversalEntryId { get; set; }

    public string? ReversalReason { get; set; }

    public DateTimeOffset? ReversedAt { get; set; }

    public Guid? ReversedBy { get; set; }

    public Guid? ProposalId { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? PostedBy { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PaymentLine> Lines { get; } = [];
}

public sealed class PaymentLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public int LineNo { get; set; }

    public Guid OpenItemId { get; set; }

    public decimal AmountTc { get; set; }

    public decimal DiscountTc { get; set; }

    public decimal WhtTc { get; set; }

    public Guid? SettlementId { get; set; }
}

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

/// <summary>A book of cheque leaves of a bank account: issued cheques take the next number from it (roadmap 6.3).</summary>
public sealed class ChequeBook : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Prefix { get; set; } = string.Empty;

    public long FirstNumber { get; set; }

    public long LastNumber { get; set; }

    public long NextNumber { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A cheque received from a customer or issued to a supplier (roadmap 6.3, POSTING_RULES §5): one per receipt or
/// payment by cheque, with its life from in hand through deposit, clearing or bounce (received), or from issued to
/// paid, bounced or cancelled (issued). Post-dated when due after the day it was taken or written.
/// </summary>
public sealed class Cheque : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>received or issued.</summary>
    public string Direction { get; set; } = "received";

    public string ChequeNumber { get; set; } = string.Empty;

    public Guid? ChequeBookId { get; set; }

    public string? DrawerBank { get; set; }

    public string? DrawerName { get; set; }

    public Guid PartnerId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>The amount in the company's currency at the receipt's or payment's rate: what the cheque carries through the PDC accounts.</summary>
    public decimal AmountFc { get; set; }

    public DateOnly IssueDate { get; set; }

    public DateOnly DueDate { get; set; }

    public bool IsPostDated { get; set; }

    /// <summary>received: draft, in_hand, deposited, cleared, bounced, returned; issued: draft, issued, paid, bounced, cancelled.</summary>
    public string Status { get; set; } = "draft";

    public Guid PaymentId { get; set; }

    public Guid? DepositId { get; set; }

    /// <summary>The account the cheque is deposited to (received) or drawn on (issued).</summary>
    public Guid BankAccountId { get; set; }

    public Guid? ReplacedById { get; set; }

    public Guid? ReplacesId { get; set; }

    /// <summary>The receivable opened on the customer for the fee re-charged when the cheque bounced.</summary>
    public Guid? FeeOpenItemId { get; set; }

    /// <summary>The bank movement of the clearing (received) or the payment (issued).</summary>
    public Guid? BankTransactionId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ChequeEvent> Events { get; } = [];
}

/// <summary>One step of a cheque's life, with the charges it cost and the journal it posted.</summary>
public sealed class ChequeEvent : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ChequeId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>received, deposited, cleared, bounced, returned, replaced, issued, paid, cancelled.</summary>
    public string Event { get; set; } = string.Empty;

    public DateOnly EffectiveDate { get; set; }

    public decimal BankCharge { get; set; }

    public decimal CustomerFee { get; set; }

    public string? Reason { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? Actor { get; set; }
}

/// <summary>A deposit slip: cheques in hand taken to a bank account together, moving from PDC receivable to cheques under collection.</summary>
public sealed class ChequeDeposit : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public Guid BankAccountId { get; set; }

    public DateOnly DepositDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal Total { get; set; }

    /// <summary>deposited, cleared, partially_bounced, bounced.</summary>
    public string Status { get; set; } = "deposited";

    public Guid? JournalEntryId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

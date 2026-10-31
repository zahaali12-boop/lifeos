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

/// <summary>A saved way of reading a bank's CSV export: which columns hold the date, the amount (or debit and credit), the text and the reference, and how numbers and dates are written (roadmap 6.2).</summary>
public sealed class StatementMapping : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Settings { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A bank statement imported for an account (CSV, OFX, MT940 or CAMT.053): its period, balances and lines.</summary>
public sealed class BankStatement : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Format { get; set; } = "csv";

    public string FileName { get; set; } = string.Empty;

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public decimal? OpeningBalance { get; set; }

    public decimal? ClosingBalance { get; set; }

    public int LineCount { get; set; }

    public int SkippedCount { get; set; }

    /// <summary>imported, reconciled.</summary>
    public string Status { get; set; } = "imported";

    public Guid? ImportedBy { get; set; }

    public DateTimeOffset ImportedAt { get; set; }

    public List<StatementLine> Lines { get; } = [];
}

/// <summary>One line of a bank statement, signed in the account's currency; matched to the book's movements, created into the books, or excluded.</summary>
public sealed class StatementLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid StatementId { get; set; }

    public Guid BankAccountId { get; set; }

    public int LineNo { get; set; }

    public DateOnly BookingDate { get; set; }

    public DateOnly? ValueDate { get; set; }

    public decimal Amount { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Counterparty { get; set; }

    public string? Reference { get; set; }

    public string? BankCode { get; set; }

    public string? ExternalId { get; set; }

    /// <summary>A hash of what identifies the line, so the same statement imported twice adds nothing.</summary>
    public string ImportKey { get; set; } = string.Empty;

    /// <summary>unmatched, matched, excluded.</summary>
    public string Status { get; set; } = "unmatched";

    public string? ExcludeReason { get; set; }

    public DateTimeOffset? MatchedAt { get; set; }
}

/// <summary>A standing instruction for the matcher: lines that read a certain way are booked to an account (a bank fee, interest) and matched at once.</summary>
public sealed class ReconciliationRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public LocalizedText Name { get; set; } = new();

    public int Priority { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    /// <summary>in, out or any.</summary>
    public string Direction { get; set; } = "any";

    public string Conditions { get; set; } = "{}";

    /// <summary>post_to_account.</summary>
    public string Action { get; set; } = "post_to_account";

    public string? AccountRole { get; set; }

    public Guid? BankAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A statement line paired with a book movement: by the matcher, by hand, by a rule that created the movement, or through a cheque the line cleared or paid.</summary>
public sealed class ReconciliationMatch : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid StatementLineId { get; set; }

    public Guid BankTransactionId { get; set; }

    public Guid? RuleId { get; set; }

    /// <summary>auto, manual, created, cheque.</summary>
    public string MatchKind { get; set; } = "auto";

    public decimal Confidence { get; set; } = 1m;

    public Guid? MatchedBy { get; set; }

    public DateTimeOffset MatchedAt { get; set; }
}

/// <summary>A reconciliation of a bank account at a date: the bank's balance against the book's, with what is in transit either way; completed when they agree.</summary>
public sealed class BankReconciliation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Number { get; set; } = string.Empty;

    public DateOnly AsOf { get; set; }

    public decimal StatementBalance { get; set; }

    public decimal BookBalance { get; set; }

    public decimal DepositsInTransit { get; set; }

    public decimal OutstandingPayments { get; set; }

    public decimal UnmatchedBankItems { get; set; }

    public decimal Difference { get; set; }

    /// <summary>open, completed.</summary>
    public string Status { get; set; } = "open";

    public Guid? CompletedBy { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

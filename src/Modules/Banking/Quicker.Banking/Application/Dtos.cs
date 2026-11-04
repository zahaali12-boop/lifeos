using System.Text.Json;

namespace Quicker.Banking.Application;

public sealed record SaveCompanyBankAccountRequest(Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string Currency, Guid? GlAccountId = null, string? BankName = null, string? BranchName = null, string? AccountNumber = null, string? Iban = null, string? Swift = null, Guid? BranchId = null, bool IsActive = true);

public sealed record CompanyBankAccountSummary(Guid Id, Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string Currency, Guid GlAccountId, string GlAccountCode, string? BankName, string? BranchName, string? AccountNumberMasked, string? Iban, string? Swift, Guid? BranchId, bool IsActive, decimal BalanceTc, decimal BalanceFc, string FunctionalCurrency, DateTimeOffset UpdatedAt);

public sealed record BankTransactionSummary(Guid Id, Guid BankAccountId, DateOnly PostingDate, DateOnly ValueDate, string Kind, decimal AmountTc, decimal AmountFc, string? Reference, string? SourceDocumentType, Guid? SourceDocumentId, Guid? JournalEntryId, Guid? ReversesTransactionId, string ReconciliationStatus);

public sealed record SavePaymentLineRequest(Guid OpenItemId, decimal Amount, decimal? DiscountTc = null, decimal? WhtTc = null);

/// <summary>
/// A supplier payment: lines settle invoice open items (amount, early-payment discount, withholding at payment), the
/// remainder goes on account; an advance (kind <c>supplier_advance</c>) has no lines. Charges and the bank amount are
/// in the bank account's currency; the bank amount is required when that differs from the payment currency.
/// </summary>
public sealed record SavePaymentRequest(Guid CompanyId, Guid PartnerId, Guid BankAccountId, string Kind = "supplier_payment", DateOnly? PaymentDate = null, string Method = "transfer", string? Reference = null, string? Currency = null, decimal? ExchangeRate = null, IReadOnlyList<SavePaymentLineRequest>? Lines = null, decimal OnAccount = 0m, decimal Charges = 0m, decimal? BankAmount = null, bool ApplyWht = true, Guid? WhtCodeId = null, string? Notes = null, JsonElement? CustomFields = null, ChequeDetailsRequest? Cheque = null);

public sealed record PaymentLineSummary(Guid Id, int LineNo, Guid OpenItemId, string DocumentNumber, int Instalment, DateOnly DueDate, decimal ItemRemainingTc, decimal AmountTc, decimal DiscountTc, decimal WhtTc, decimal CashTc, Guid? SettlementId);

public sealed record PaymentSummary(Guid Id, Guid CompanyId, string Number, string Kind, string Status, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid BankAccountId, string BankAccountCode, DateOnly PaymentDate, string Method, string? Reference, string Currency, decimal ExchangeRate, string FunctionalCurrency,
    decimal AmountTc, decimal OnAccountTc, decimal DiscountTc, decimal WhtTc, decimal ChargesBank, decimal BankAmount, string BankCurrency, bool ApplyWht, Guid? WhtCodeId, string? WhtCode, Guid? OpenItemId, Guid? JournalEntryId, Guid? BankTransactionId, Guid? ReversalEntryId, string? ReversalReason, Guid? ProposalId, string? Notes, JsonElement CustomFields, IReadOnlyList<PaymentLineSummary> Lines, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt, ChequeSummary? Cheque = null);

/// <summary>
/// A customer receipt: lines settle invoice open items (amount, early-payment discount given, tax the customer withheld),
/// the remainder goes on account; a deposit (kind <c>customer_deposit</c>) has no lines. Charges are what the bank kept,
/// in the bank account's currency; the bank amount is what it credited and is required when that currency differs
/// from the receipt's.
/// </summary>
public sealed record SaveCustomerReceiptRequest(Guid CompanyId, Guid PartnerId, Guid BankAccountId, string Kind = "customer_receipt", DateOnly? ReceiptDate = null, string Method = "transfer", string? Reference = null, string? Currency = null, decimal? ExchangeRate = null, IReadOnlyList<SavePaymentLineRequest>? Lines = null, decimal OnAccount = 0m, decimal Charges = 0m, decimal? BankAmount = null, Guid? WhtCodeId = null, string? Notes = null, JsonElement? CustomFields = null, ChequeDetailsRequest? Cheque = null);

public sealed record ReversePaymentRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record PayProposalRequest(Guid ProposalId, Guid BankAccountId, DateOnly? PaymentDate = null, string Method = "transfer");

/// <summary>The cheque behind a receipt or payment by cheque: its number (or the book an issued one takes it from), the drawer and the due date (post-dated when after the document's date).</summary>
public sealed record ChequeDetailsRequest(string? ChequeNumber = null, Guid? ChequeBookId = null, string? DrawerBank = null, string? DrawerName = null, DateOnly? DueDate = null, Guid? ReplacesChequeId = null, string? Notes = null);

public sealed record SaveChequeBookRequest(Guid CompanyId, Guid BankAccountId, string? Prefix, long FirstNumber, long LastNumber, bool IsActive = true);

public sealed record ChequeBookSummary(Guid Id, Guid CompanyId, Guid BankAccountId, string BankAccountCode, string Prefix, long FirstNumber, long LastNumber, long NextNumber, long Remaining, bool IsActive);

public sealed record ChequeEventSummary(Guid Id, string Event, DateOnly EffectiveDate, decimal BankCharge, decimal CustomerFee, string? Reason, Guid? JournalEntryId, DateTimeOffset OccurredAt);

public sealed record ChequeSummary(
    Guid Id,
    Guid CompanyId,
    string Direction,
    string ChequeNumber,
    Guid? ChequeBookId,
    string? DrawerBank,
    string? DrawerName,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    string Currency,
    decimal Amount,
    decimal AmountFc,
    string FunctionalCurrency,
    DateOnly IssueDate,
    DateOnly DueDate,
    bool IsPostDated,
    string Status,
    Guid PaymentId,
    string PaymentNumber,
    Guid? DepositId,
    string? DepositNumber,
    Guid BankAccountId,
    string BankAccountCode,
    Guid? ReplacedById,
    Guid? ReplacesId,
    Guid? FeeOpenItemId,
    Guid? BankTransactionId,
    string? Notes,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChequeEventSummary> Events);

public sealed record DepositChequesRequest(Guid CompanyId, Guid BankAccountId, IReadOnlyList<Guid> ChequeIds, DateOnly? DepositDate = null);

/// <summary>A step of a cheque's life on a date: clearing or paying (with what the bank charged), or cancelling an issued one.</summary>
public sealed record ChequeActionRequest(DateOnly? Date = null, decimal BankCharge = 0m, string? Reason = null);

/// <summary>A received cheque the bank returned unpaid: what the bank charged, and what is re-charged to the customer as a new receivable.</summary>
public sealed record BounceChequeRequest(DateOnly? Date = null, decimal BankCharge = 0m, decimal CustomerFee = 0m, string? Reason = null);

public sealed record ChequeDepositSummary(Guid Id, Guid CompanyId, string Number, Guid BankAccountId, string BankAccountCode, DateOnly DepositDate, string Currency, decimal Total, int ChequeCount, string Status, Guid? JournalEntryId, DateTimeOffset CreatedAt, IReadOnlyList<ChequeSummary> Cheques);

/// <summary>Cheques falling due in a month: what is expected in from customers and out to suppliers, in the company's currency.</summary>
public sealed record ChequeForecastRow(DateOnly PeriodStart, int ReceivedCount, decimal ReceivedFc, int IssuedCount, decimal IssuedFc, decimal NetFc);

// ------------------------------------------------------------------ statements and reconciliation (roadmap 6.2)

public sealed record SaveStatementMappingRequest(string Name, StatementMappingSettings Settings, bool IsActive = true);

public sealed record StatementMappingSummary(Guid Id, string Name, StatementMappingSettings Settings, bool IsActive, DateTimeOffset UpdatedAt);

/// <summary>A CSV read with a mapping before it is imported: the first lines as they would be taken, or why they cannot be.</summary>
public sealed record StatementPreviewRequest(string Content, StatementMappingSettings? Settings = null, string? Format = null);

public sealed record StatementPreview(string Format, int LineCount, DateOnly? From, DateOnly? To, decimal? OpeningBalance, decimal? ClosingBalance, string? Currency, string? AccountNumber, IReadOnlyList<ParsedLine> Lines);

public sealed record StatementLineSummary(
    Guid Id,
    Guid StatementId,
    Guid BankAccountId,
    int LineNo,
    DateOnly BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Description,
    string? Counterparty,
    string? Reference,
    string? BankCode,
    string Status,
    string? ExcludeReason,
    IReadOnlyList<ReconciliationMatchSummary> Matches);

public sealed record ReconciliationMatchSummary(Guid Id, Guid BankTransactionId, string MatchKind, decimal Confidence, DateOnly TransactionDate, string TransactionKind, decimal TransactionAmount, string? TransactionReference, string? SourceDocumentType, Guid? SourceDocumentId, DateTimeOffset MatchedAt);

public sealed record BankStatementSummary(
    Guid Id,
    Guid CompanyId,
    Guid BankAccountId,
    string BankAccountCode,
    string Format,
    string FileName,
    DateOnly? FromDate,
    DateOnly? ToDate,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    int LineCount,
    int SkippedCount,
    int MatchedCount,
    int UnmatchedCount,
    int ExcludedCount,
    string Status,
    DateTimeOffset ImportedAt,
    IReadOnlyList<StatementLineSummary> Lines);

/// <summary>What the matcher did on a statement or an account: lines paired, movements created, lines left for a person.</summary>
public sealed record AutoMatchResult(int Matched, int Created, int ChequesCleared, int Remaining);

public sealed record MatchLinesRequest(IReadOnlyList<Guid> BankTransactionIds);

/// <summary>A movement the books lacked, made from a statement line: a bank charge, interest, or another income or expense by its account role.</summary>
public sealed record CreateFromLineRequest(string AccountRole, string? Description = null);

public sealed record ExcludeLineRequest(string? Reason = null);

/// <summary>What a rule looks for in a line: words in its text, a pattern on its reference, a side and an amount range.</summary>
public sealed record RuleConditions(IReadOnlyList<string>? DescriptionContains = null, string? ReferencePattern = null, decimal? AmountMin = null, decimal? AmountMax = null);

public sealed record SaveReconciliationRuleRequest(Guid CompanyId, IReadOnlyDictionary<string, string> Name, string AccountRole, int Priority = 100, string Direction = "any", RuleConditions? Conditions = null, Guid? BankAccountId = null, bool IsActive = true);

public sealed record ReconciliationRuleSummary(Guid Id, Guid CompanyId, IReadOnlyDictionary<string, string> Name, int Priority, bool IsActive, string Direction, RuleConditions Conditions, string Action, string? AccountRole, Guid? BankAccountId, DateTimeOffset UpdatedAt);

public sealed record BankReconciliationReport(
    Guid BankAccountId,
    string Currency,
    DateOnly AsOf,
    decimal StatementBalance,
    decimal BookBalance,
    decimal DepositsInTransit,
    decimal OutstandingPayments,
    decimal AdjustedBankBalance,
    decimal UnmatchedBankItems,
    decimal Difference,
    bool CanComplete,
    IReadOnlyList<BankTransactionSummary> OutstandingTransactions,
    IReadOnlyList<StatementLineSummary> UnmatchedLines);

public sealed record CreateReconciliationRequest(Guid BankAccountId, DateOnly AsOf, decimal StatementBalance);

public sealed record BankReconciliationSummary(Guid Id, Guid CompanyId, Guid BankAccountId, string BankAccountCode, string Number, DateOnly AsOf, decimal StatementBalance, decimal BookBalance, decimal DepositsInTransit, decimal OutstandingPayments, decimal UnmatchedBankItems, decimal Difference, string Status, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);

/// <summary>Book movements the bank has not confirmed yet, by how long they have waited.</summary>
public sealed record UnreconciledAgingRow(Guid BankAccountId, string BankAccountCode, string Currency, int Count, decimal Current, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);

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

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
public sealed record SavePaymentRequest(Guid CompanyId, Guid PartnerId, Guid BankAccountId, string Kind = "supplier_payment", DateOnly? PaymentDate = null, string Method = "transfer", string? Reference = null, string? Currency = null, decimal? ExchangeRate = null, IReadOnlyList<SavePaymentLineRequest>? Lines = null, decimal OnAccount = 0m, decimal Charges = 0m, decimal? BankAmount = null, bool ApplyWht = true, Guid? WhtCodeId = null, string? Notes = null, JsonElement? CustomFields = null);

public sealed record PaymentLineSummary(Guid Id, int LineNo, Guid OpenItemId, string DocumentNumber, int Instalment, DateOnly DueDate, decimal ItemRemainingTc, decimal AmountTc, decimal DiscountTc, decimal WhtTc, decimal CashTc, Guid? SettlementId);

public sealed record PaymentSummary(Guid Id, Guid CompanyId, string Number, string Kind, string Status, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid BankAccountId, string BankAccountCode, DateOnly PaymentDate, string Method, string? Reference, string Currency, decimal ExchangeRate, string FunctionalCurrency,
    decimal AmountTc, decimal OnAccountTc, decimal DiscountTc, decimal WhtTc, decimal ChargesBank, decimal BankAmount, string BankCurrency, bool ApplyWht, Guid? WhtCodeId, string? WhtCode, Guid? OpenItemId, Guid? JournalEntryId, Guid? BankTransactionId, Guid? ReversalEntryId, string? ReversalReason, Guid? ProposalId, string? Notes, JsonElement CustomFields, IReadOnlyList<PaymentLineSummary> Lines, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt);

public sealed record ReversePaymentRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record PayProposalRequest(Guid ProposalId, Guid BankAccountId, DateOnly? PaymentDate = null, string Method = "transfer");

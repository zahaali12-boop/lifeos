using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Banking.Contracts;

public static class BankDocumentTypes
{
    public const string Payment = "bank_payment";
    public const string Receipt = "bank_receipt";
    public const string Transaction = "bank_transaction";
    public const string Cheque = "bank_cheque";
    public const string ChequeDeposit = "bank_cheque_deposit";
    public const string Statement = "bank_statement";
    public const string Reconciliation = "bank_reconciliation";
}

public static class ChequeDirections
{
    public const string Received = "received";
    public const string Issued = "issued";
}

/// <summary>Where a cheque stands: a received one from in hand to the bank, an issued one from written to paid.</summary>
public static class ChequeStatuses
{
    public const string Draft = "draft";
    public const string InHand = "in_hand";
    public const string Deposited = "deposited";
    public const string Cleared = "cleared";
    public const string Bounced = "bounced";
    public const string Returned = "returned";
    public const string Issued = "issued";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";
}

public static class BankAccountKinds
{
    public const string Bank = "bank";
    public const string Cash = "cash";
    public const string PettyCash = "petty_cash";

    public static readonly IReadOnlyList<string> All = [Bank, Cash, PettyCash];
}

public sealed record BankAccountInfo(Guid Id, Guid CompanyId, string Code, LocalizedText Name, string Kind, string Currency, Guid GlAccountId, string? BankName, string? IbanMasked, Guid? BranchId, bool IsActive);

/// <summary>A bank account's or cash box's balance at a date from its transactions (its subledger), in its currency and in the company's.</summary>
public sealed record BankBalanceInfo(Guid Id, string Code, LocalizedText Name, string Kind, string Currency, bool IsActive, decimal BalanceTc, decimal BalanceFc);

public interface IBankAccountDirectory
{
    Task<BankAccountInfo?> FindAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    /// <summary>A company's bank account or cash box by its code (codes are kept upper-case), for imports that name it.</summary>
    Task<BankAccountInfo?> FindByCodeAsync(Guid companyId, string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BankAccountInfo>> ListAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Every account of the company with its balance at the date, for a screen that says what money there is (the trader's health, ADR-0035 §8).</summary>
    Task<IReadOnlyList<BankBalanceInfo>> BalancesAsync(Guid companyId, DateOnly asOf, CancellationToken cancellationToken = default);
}

/// <summary>A movement of a bank or cash account another module's document makes (a petty cash voucher, a fund replenishment): signed in the account's currency, the journal line referencing it by <paramref name="Id"/>.</summary>
public sealed record NewBankTransaction(Guid Id, Guid BankAccountId, Guid CompanyId, DateOnly PostingDate, string Kind, decimal AmountTc, decimal AmountFc, string? Reference, string SourceDocumentType, Guid SourceDocumentId, Guid? JournalEntryId);

/// <summary>The bank transactions subledger as other modules' documents feed it (A-174): record a movement, mirror a document's movements on reversal, read a balance.</summary>
public interface IBankTransactions
{
    Task RecordAsync(NewBankTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Writes the mirror of every movement a document made, dated the reversal, each pointing at the row it undoes.</summary>
    Task ReverseDocumentAsync(string sourceDocumentType, Guid sourceDocumentId, DateOnly reversalDate, Guid? reversalEntryId, CancellationToken cancellationToken = default);

    /// <summary>The account's balance in its own currency from its movements up to a day (every movement when no day is given).</summary>
    Task<decimal> BalanceAsync(Guid bankAccountId, DateOnly? asOf = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// One payment that settles one payable open item from a bank account on a day: what an expense's "pay" button sends.
/// The amount is in the item's currency; when the account is in the company's currency and the item is not, the bank
/// amount is what left the account (the rate actually paid), and the exchange rate defaults to the day's spot rate.
/// </summary>
public sealed record PayOpenItemRequest(Guid CompanyId, Guid PartnerId, Guid BankAccountId, Guid OpenItemId, decimal Amount, DateOnly PaymentDate, string Method = "transfer", string? Reference = null, decimal? BankAmount = null, decimal? ExchangeRate = null);

/// <summary>
/// An advance to a supplier before its invoice (a deposit on an order, freight paid before the goods arrive): an open
/// item on the supplier-advances control in the advance's currency, applied to the invoice when it is posted.
/// </summary>
public sealed record SupplierAdvanceRequest(Guid CompanyId, Guid PartnerId, Guid BankAccountId, string Currency, decimal Amount, DateOnly PaymentDate, decimal? ExchangeRate = null, decimal? BankAmount = null, string Method = "transfer", string? Reference = null);

/// <summary>A posted payment: its number, the advance's open item when it is one, the rate it was booked at and what left the account in the account's currency.</summary>
public sealed record PaymentRef(Guid Id, string Number, Guid? OpenItemId = null, decimal ExchangeRate = 1m, decimal BankAmount = 0m, string? BankCurrency = null);

/// <summary>Supplier payments as other modules drive them: created and posted in one go, under the payment permissions.</summary>
public interface IPayments
{
    Task<Result<PaymentRef>> PayAsync(PayOpenItemRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pays a supplier in advance: a posted advance whose open item waits for the invoice.</summary>
    Task<Result<PaymentRef>> AdvanceAsync(SupplierAdvanceRequest request, CancellationToken cancellationToken = default);
}

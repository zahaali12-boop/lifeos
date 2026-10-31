using System.Text.Json;

namespace Quicker.Expenses.Application;

public sealed record SaveExpenseCategoryRequest(string Code, IReadOnlyDictionary<string, string> Name, string AccountCode, Guid? TaxCodeId = null, bool ReceiptRequired = false, decimal? MaxAmount = null, bool IsActive = true);

public sealed record ExpenseCategorySummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string AccountCode, Guid? TaxCodeId, bool ReceiptRequired, decimal? MaxAmount, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveFundRequest(Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, Guid BankAccountId, Guid? CustodianMembershipId = null, decimal FloatAmount = 0m, bool IsActive = true);

/// <summary>A fund with where it stands: its balance from the cash box's movements, and how far below the float it is.</summary>
public sealed record FundSummary(Guid Id, Guid CompanyId, string Code, IReadOnlyDictionary<string, string> Name, Guid BankAccountId, string BankAccountCode, Guid? CustodianMembershipId, decimal FloatAmount, string Currency, decimal Balance, decimal ToReplenish, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record ReplenishFundRequest(Guid FromBankAccountId, decimal Amount, DateOnly? ReplenishedOn = null, string? Reference = null);

public sealed record ReplenishmentSummary(Guid Id, Guid CompanyId, Guid FundId, string Number, DateOnly ReplenishedOn, decimal Amount, Guid FromBankAccountId, string? Reference, string Status, Guid? JournalEntryId, Guid? ReversalEntryId, DateTimeOffset CreatedAt);

public sealed record ReverseExpenseRequest(string Reason, DateOnly? ReversalDate = null);

/// <summary>One line of an expense: the category decides the account (and the usual tax code unless the line names one); dimensions by code.</summary>
public sealed record SaveExpenseLineRequest(Guid CategoryId, decimal Amount, string? Description = null, Guid? TaxCodeId = null, IReadOnlyDictionary<string, Guid>? Dimensions = null);

/// <summary>
/// An expense to record: owed to a supplier or an employee (<paramref name="PartnerId"/>), or paid from a petty cash
/// fund (<paramref name="FundId"/>); dated the day it was incurred, in the currency it was incurred in.
/// </summary>
public sealed record SaveExpenseRequest(
    Guid CompanyId,
    string Kind,
    DateOnly ExpenseDate,
    IReadOnlyList<SaveExpenseLineRequest> Lines,
    Guid? PartnerId = null,
    Guid? FundId = null,
    DateOnly? DueDate = null,
    string? Currency = null,
    string? Reference = null,
    string? Description = null,
    JsonElement? CustomFields = null);

/// <summary>A payment of a confirmed supplier or employee expense from a bank account on a day; the whole remainder unless an amount is given.</summary>
public sealed record PayExpenseRequest(Guid BankAccountId, DateOnly? PaymentDate = null, decimal? Amount = null, string Method = "transfer", string? Reference = null);

public sealed record ExpenseLineSummary(
    Guid Id,
    int LineNo,
    Guid CategoryId,
    string CategoryCode,
    IReadOnlyDictionary<string, string> CategoryName,
    Guid? AccountId,
    string? Description,
    decimal Amount,
    Guid? TaxCodeId,
    decimal TaxRatePct,
    decimal TaxAmount,
    bool TaxRecoverable,
    IReadOnlyDictionary<string, Guid>? Dimensions);

/// <summary>
/// An expense as the screens read it. <paramref name="PaymentStatus"/> is derived from the payable: <c>unpaid</c>,
/// <c>partial</c>, <c>paid</c>, or <c>n/a</c> for a petty cash voucher (paid when it was incurred) and for anything not
/// yet confirmed.
/// </summary>
public sealed record ExpenseSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Kind,
    string Status,
    Guid? PartnerId,
    string? PartnerCode,
    IReadOnlyDictionary<string, string>? PartnerName,
    Guid? FundId,
    string? FundCode,
    DateOnly ExpenseDate,
    DateOnly? DueDate,
    string Currency,
    string? Reference,
    string? Description,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    decimal PaidTc,
    decimal RemainingTc,
    string PaymentStatus,
    Guid? JournalEntryId,
    Guid? ReversalEntryId,
    Guid? ApprovalRequestId,
    string? RejectionReason,
    JsonElement CustomFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExpenseLineSummary>? Lines);

public sealed record ExpensePaymentResult(Guid PaymentId, string PaymentNumber, ExpenseSummary Expense);

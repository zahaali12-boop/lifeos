using System.Text.Json;
using Quicker.Expenses.Contracts;
using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Expenses.Domain;

/// <summary>What kind of spend a line is: the account it lands on (by code, resolved in the company's chart), its usual tax code and the policy a claim must respect.</summary>
public sealed class ExpenseCategory : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public LocalizedText Name { get; set; } = new();
    public string AccountCode { get; set; } = string.Empty;
    public Guid? TaxCodeId { get; set; }
    public bool ReceiptRequired { get; set; }
    public decimal? MaxAmount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A petty cash fund: a cash box (a bank account of kind cash or petty cash) with its custodian and float; vouchers spend it, replenishments refill it.</summary>
public sealed class PettyCashFund : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public LocalizedText Name { get; set; } = new();
    public Guid BankAccountId { get; set; }
    public Guid? CustodianMembershipId { get; set; }
    public decimal FloatAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One expense with its lines: owed to a supplier (a bill without an order), claimed by an employee, or paid from a petty
/// cash fund. Draft until confirmed; confirming posts the accrual (or the cash voucher) on the expense date and, for a
/// supplier or employee, opens the payable that a payment settles on its own date.
/// </summary>
public sealed class Expense : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string? Number { get; set; }
    public string Kind { get; set; } = ExpenseKinds.Supplier;
    public Guid? PartnerId { get; set; }
    public Guid? FundId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = ExpenseStatuses.Draft;
    public decimal TotalNet { get; set; }
    public decimal TotalTax { get; set; }
    public decimal TotalGross { get; set; }
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public Guid? BankTransactionId { get; set; }
    public string? RejectionReason { get; set; }
    public JsonElement CustomFields { get; set; } = JsonDocument.Parse("{}").RootElement;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ExpenseLine> Lines { get; } = [];
}

public sealed class ExpenseLine : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid ExpenseId { get; set; }
    public int LineNo { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public Guid? TaxCodeId { get; set; }
    public decimal TaxRatePct { get; set; }
    public decimal TaxAmount { get; set; }
    public bool TaxRecoverable { get; set; }
    public bool TaxReverseCharge { get; set; }
    public Guid? DimensionSetId { get; set; }
}

/// <summary>Cash moved from a bank account into a petty cash fund on a day: two bank movements and one journal.</summary>
public sealed class FundReplenishment : ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FundId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateOnly ReplenishedOn { get; set; }
    public decimal Amount { get; set; }
    public Guid FromBankAccountId { get; set; }
    public string? Reference { get; set; }
    public string Status { get; set; } = "posted";
    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public Guid FundTransactionId { get; set; }
    public Guid BankTransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

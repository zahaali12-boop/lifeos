namespace Quicker.Expenses.Contracts;

/// <summary>The documents of the Expenses module (founder programme item 2, A-174), as numbering, posting and the record services name them.</summary>
public static class ExpenseDocumentTypes
{
    public const string Expense = "expense";
    public const string Replenishment = "fund_replenishment";
}

/// <summary>Who an expense is owed to, or where it was paid from: a supplier's bill, an employee's claim, or a petty cash fund's voucher.</summary>
public static class ExpenseKinds
{
    public const string Supplier = "supplier";
    public const string Employee = "employee";
    public const string PettyCash = "petty_cash";

    public static readonly IReadOnlyList<string> All = [Supplier, Employee, PettyCash];
}

public static class ExpenseStatuses
{
    public const string Draft = "draft";
    public const string PendingApproval = "pending_approval";
    public const string Confirmed = "confirmed";
    public const string Reversed = "reversed";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
}

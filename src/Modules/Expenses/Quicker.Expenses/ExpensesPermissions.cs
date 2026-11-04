using Quicker.Identity.Contracts;

namespace Quicker.Expenses;

/// <summary>Permission keys of expenses, their categories and petty cash funds; the accountant and payables clerk templates grant <c>expenses.*</c>.</summary>
public static class ExpensesPermissions
{
    public const string ExpenseRead = "expenses.expense.read";
    public const string ExpenseManage = "expenses.expense.manage";
    public const string ExpenseConfirm = "expenses.expense.confirm";
    public const string CategoryManage = "expenses.category.manage";
    public const string FundRead = "expenses.fund.read";
    public const string FundManage = "expenses.fund.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(ExpenseRead, "expenses", "Read expenses, expense categories and what was paid on them"),
        new(ExpenseManage, "expenses", "Record, edit, cancel and submit expenses"),
        new(ExpenseConfirm, "expenses", "Confirm expenses (post the accrual or the cash voucher) and reverse them"),
        new(CategoryManage, "expenses", "Define expense categories: their accounts, default tax and policy limits"),
        new(FundRead, "expenses", "Read petty cash funds, their balances and replenishments"),
        new(FundManage, "expenses", "Create petty cash funds, replenish them and reverse a replenishment"),
    ];
}

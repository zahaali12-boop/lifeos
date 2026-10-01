using Quicker.Identity.Contracts;

namespace Quicker.Banking;

/// <summary>Permission keys of bank and cash accounts and payments; the accountant template grants <c>banking.*</c>.</summary>
public static class BankingPermissions
{
    public const string BankAccountRead = "banking.bank_account.read";
    public const string BankAccountManage = "banking.bank_account.manage";
    public const string PaymentRead = "banking.payment.read";
    public const string PaymentManage = "banking.payment.manage";
    public const string PaymentPost = "banking.payment.post";

    public static readonly PermissionDefinition[] All =
    [
        new(BankAccountRead, "banking", "Read bank and cash accounts and their transactions"),
        new(BankAccountManage, "banking", "Create and edit bank and cash accounts"),
        new(PaymentRead, "banking", "Read supplier payments and advances"),
        new(PaymentManage, "banking", "Create, edit and delete draft supplier payments and advances, and draft them from a proposal"),
        new(PaymentPost, "banking", "Post supplier payments and advances and reverse them"),
    ];
}

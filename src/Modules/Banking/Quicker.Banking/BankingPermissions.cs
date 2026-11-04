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
    public const string ReceiptRead = "banking.receipt.read";
    public const string ReceiptManage = "banking.receipt.manage";
    public const string ReceiptPost = "banking.receipt.post";
    public const string ChequeRead = "banking.cheque.read";
    public const string ChequeManage = "banking.cheque.manage";
    public const string ReconciliationRead = "banking.reconciliation.read";
    public const string ReconciliationManage = "banking.reconciliation.manage";
    public const string TransferRead = "banking.transfer.read";
    public const string TransferPost = "banking.transfer.post";

    public static readonly PermissionDefinition[] All =
    [
        new(BankAccountRead, "banking", "Read bank and cash accounts and their transactions"),
        new(BankAccountManage, "banking", "Create and edit bank and cash accounts"),
        new(PaymentRead, "banking", "Read supplier payments and advances"),
        new(PaymentManage, "banking", "Create, edit and delete draft supplier payments and advances, and draft them from a proposal"),
        new(PaymentPost, "banking", "Post supplier payments and advances and reverse them"),
        new(ReceiptRead, "banking", "Read customer receipts and deposits"),
        new(ReceiptManage, "banking", "Create, edit and delete draft customer receipts and deposits"),
        new(ReceiptPost, "banking", "Post customer receipts and deposits and reverse them"),
        new(ChequeRead, "banking", "Read the cheque registers, deposit slips and the maturity forecast"),
        new(ChequeManage, "banking", "Deposit, clear, bounce, pay and cancel cheques and keep cheque books"),
        new(ReconciliationRead, "banking", "Read bank statements, matches, reconciliations and the unreconciled aging"),
        new(ReconciliationManage, "banking", "Import statements, match and create movements, keep the rules and complete reconciliations"),
        new(TransferRead, "banking", "Read transfers between the company's bank accounts and cash boxes"),
        new(TransferPost, "banking", "Record transfers between the company's bank accounts and cash boxes (deposits, withdrawals, currency changed) and reverse them"),
    ];
}

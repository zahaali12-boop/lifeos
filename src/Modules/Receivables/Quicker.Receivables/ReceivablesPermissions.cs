using Quicker.Identity.Contracts;

namespace Quicker.Receivables;

/// <summary>Permission keys of the receivables subledger; the ar_clerk template grants <c>receivables.*</c>.</summary>
public static class ReceivablesPermissions
{
    public const string OpenItemRead = "receivables.open_item.read";
    public const string OpenItemManage = "receivables.open_item.manage";
    public const string SettlementPost = "receivables.settlement.post";

    public const string WriteOffPost = "receivables.writeoff.post";

    public static readonly PermissionDefinition[] All =
    [
        new(OpenItemRead, "receivables", "Read receivable open items, aging, statements and settlements"),
        new(OpenItemManage, "receivables", "Hold and release receivable open items and record promises to pay"),
        new(SettlementPost, "receivables", "Apply credit notes, deposits and receipts on account to invoices and reverse such applications"),
        new(WriteOffPost, "receivables", "Write off customer balances and reverse write-offs"),
    ];
}

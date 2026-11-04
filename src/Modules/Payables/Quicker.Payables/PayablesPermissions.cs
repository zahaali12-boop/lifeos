using Quicker.Identity.Contracts;

namespace Quicker.Payables;

/// <summary>Permission keys of the payables subledger; the accountant template grants <c>payables.*</c>.</summary>
public static class PayablesPermissions
{
    public const string OpenItemRead = "payables.open_item.read";
    public const string OpenItemManage = "payables.open_item.manage";
    public const string SettlementPost = "payables.settlement.post";
    public const string ProposalRead = "payables.proposal.read";
    public const string ProposalManage = "payables.proposal.manage";
    public const string ProposalApprove = "payables.proposal.approve";

    public static readonly PermissionDefinition[] All =
    [
        new(OpenItemRead, "payables", "Read payable open items, aging and settlements"),
        new(OpenItemManage, "payables", "Hold and release payable open items"),
        new(SettlementPost, "payables", "Apply credits, advances and payments on account to invoices and reverse such applications"),
        new(ProposalRead, "payables", "Read payment proposals"),
        new(ProposalManage, "payables", "Create, edit, cancel and delete payment proposals"),
        new(ProposalApprove, "payables", "Approve payment proposals for payment"),
    ];
}

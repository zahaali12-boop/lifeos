using Quicker.Identity.Contracts;

namespace Quicker.Inventory;

public static class InventoryPermissions
{
    public const string WarehouseRead = "inventory.warehouse.read";
    public const string WarehouseManage = "inventory.warehouse.manage";
    public const string StockRead = "inventory.stock.read";
    public const string TransferRead = "inventory.transfer.read";
    public const string TransferManage = "inventory.transfer.manage";
    public const string TransferShip = "inventory.transfer.ship";
    public const string TransferReceive = "inventory.transfer.receive";
    public const string ReservationManage = "inventory.reservation.manage";
    public const string PostInSoftClosed = "inventory.period.post_in_soft_closed";
    public const string CostingRead = "inventory.costing.read";
    public const string CostingManage = "inventory.costing.manage";
    public const string ReasonCodeManage = "inventory.reason_code.manage";
    public const string AdjustmentRead = "inventory.adjustment.read";
    public const string AdjustmentManage = "inventory.adjustment.manage";
    public const string AdjustmentApprove = "inventory.adjustment.approve";
    public const string AdjustmentPost = "inventory.adjustment.post";
    public const string AssemblyRead = "inventory.assembly.read";
    public const string AssemblyManage = "inventory.assembly.manage";
    public const string AssemblyPost = "inventory.assembly.post";

    public static readonly PermissionDefinition[] All =
    [
        new(WarehouseRead, "inventory", "Read warehouses and bins"),
        new(WarehouseManage, "inventory", "Create and edit warehouses and bins"),
        new(StockRead, "inventory", "Read stock balances, availability, the stock ledger and reservations"),
        new(TransferRead, "inventory", "Read stock transfers"),
        new(TransferManage, "inventory", "Create, edit and cancel stock transfers"),
        new(TransferShip, "inventory", "Ship a stock transfer (moves stock out to transit)"),
        new(TransferReceive, "inventory", "Receive a stock transfer (moves stock from transit in)"),
        new(ReservationManage, "inventory", "Reserve stock for a document and release reservations"),
        new(PostInSoftClosed, "inventory", "Move stock in a soft-closed inventory period"),
        new(CostingRead, "inventory", "Read item costs, the valuation report, value entries and cost adjustment runs"),
        new(CostingManage, "inventory", "Set standard costs, settle inbound costs (late invoices, landed costs) and post revaluations"),
        new(ReasonCodeManage, "inventory", "Create and edit reason codes for adjustments, scrap, counts, returns and shortages"),
        new(AdjustmentRead, "inventory", "Read stock adjustments"),
        new(AdjustmentManage, "inventory", "Create, edit, submit and cancel stock adjustments"),
        new(AdjustmentApprove, "inventory", "Approve or reject stock adjustments awaiting approval"),
        new(AdjustmentPost, "inventory", "Post stock adjustments (moves stock and posts the journal)"),
        new(AssemblyRead, "inventory", "Read assembly builds"),
        new(AssemblyManage, "inventory", "Create, edit and cancel assembly builds"),
        new(AssemblyPost, "inventory", "Post assembly builds (consumes components, produces the assembly)"),
    ];
}

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
        new(CostingManage, "inventory", "Set standard costs and settle inbound costs (late invoices, landed costs)"),
    ];
}

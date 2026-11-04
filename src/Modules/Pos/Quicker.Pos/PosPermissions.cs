using Quicker.Identity.Contracts;

namespace Quicker.Pos;

/// <summary>Permission keys of the point of sale: the registers an admin sets up, the sessions a cashier opens and closes, the orders a till syncs; the cashier template grants selling and the back office reading.</summary>
public static class PosPermissions
{
    public const string RegisterRead = "pos.register.read";
    public const string RegisterManage = "pos.register.manage";
    public const string SessionRead = "pos.session.read";
    public const string SessionOperate = "pos.session.operate";
    public const string OrderRead = "pos.order.read";
    public const string OrderSell = "pos.order.sell";
    public const string OrderRefund = "pos.order.refund";

    public static readonly PermissionDefinition[] All =
    [
        new(RegisterRead, "pos", "Read the registers of a company and the catalogue a till sells from"),
        new(RegisterManage, "pos", "Set up registers: warehouse, cash box, card account, price list, walk-in customer, rounding and what a cashier may do"),
        new(SessionRead, "pos", "Read cashier sessions with their float, takings, expected and counted cash"),
        new(SessionOperate, "pos", "Open a session on a register with a float and close it with the cash counted"),
        new(OrderRead, "pos", "Read the orders of a till and print their receipts"),
        new(OrderSell, "pos", "Sell from a till: sync its orders, which are posted on arrival"),
        new(OrderRefund, "pos", "Refund an order from a till: the goods come back and the money goes out"),
    ];
}

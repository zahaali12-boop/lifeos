using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Inventory.Application;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Persistence;
using Quicker.Sales.Application;

namespace Quicker.Migrator.Demo;

/// <summary>What the pick, pack and ship seed produced.</summary>
public sealed record DemoShippingOutcome(int Orders, int Shipments, int PickLists);

/// <summary>
/// Demo seed for roadmap 5.5 (A-154): Iraq Trading sells from the Baghdad main warehouse, whose stock sits in bins, so
/// every stage of pick, pack and ship is on screen the first time anyone opens it. Baghdad Mall's shipment was picked by
/// the warehouse operator (one carton short, damaged), posted and packed into two cartons with a carrier's tracking
/// number; Al-Noor's is half picked on the scanner; Kurdistan Distribution's is released and waiting for a picker; Basra
/// Oil's is a draft located bin by bin and lot by lot, not yet released. The groceries lines are lot-tracked, so their
/// lots come first expiry first out.
/// </summary>
internal static class DemoShipping
{
    private sealed record Stocked(Guid Id, string Code, decimal Available);

    public static async Task<DemoShippingOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var orders = services.GetRequiredService<OrderService>();
        var shipments = services.GetRequiredService<ShipmentService>();
        var picks = services.GetRequiredService<PickListService>();
        var iqt = companies.Single(c => c.Definition.Code == "IQT").Company.Id;
        var picker = DemoData.MembershipId(DemoData.Users.Single(u => u.Local == "warehouse"));
        Guid Customer(string key) => DemoBooks.Customers.Single(c => c.Key == key).Ref;

        var warehouse = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.inv_warehouses WHERE tenant_id = @tenant AND code = 'BGD-MAIN'", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken));

        // Items with plenty free in the warehouse, preferring those spread over more than one bin (so a line splits).
        async Task<IReadOnlyList<Stocked>> StockedAsync(string family, int count) =>
            (await unitOfWork.Connection.QueryAsync<Stocked>(new CommandDefinition("""
                SELECT i.id AS Id, i.code AS Code, sum(b.on_hand - b.reserved - b.quality_hold) AS Available
                FROM app.itm_items i
                JOIN app.inv_stock_balances b ON b.tenant_id = i.tenant_id AND b.item_id = i.id AND b.warehouse_id = @warehouse
                WHERE i.tenant_id = @tenant AND i.code LIKE @prefix AND i.list_price IS NOT NULL AND NOT i.has_variants AND i.is_active
                GROUP BY i.id, i.code
                HAVING sum(b.on_hand - b.reserved - b.quality_hold) >= 80
                ORDER BY count(DISTINCT b.bin_id) DESC, i.code
                LIMIT @count
                """, new { tenant, warehouse, prefix = family + "-%", count }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToList();
        var beverages = await StockedAsync("BEV", 3);
        var groceries = await StockedAsync("GROC", 1);
        if (beverages.Count < 3 || groceries.Count < 1)
        {
            throw new InvalidOperationException("Demo seed failed: the Baghdad main warehouse has too little free stock to seed shipments.");
        }

        async Task<OrderSummary> OrderAsync(string customer, params (Stocked Item, decimal Quantity)[] lines)
        {
            var created = Require(await orders.CreateAsync(new SaveOrderRequest(iqt, Customer(customer), warehouse,
                lines.Select(static l => new SaveOrderLineRequest(ItemCode: l.Item.Code, Quantity: l.Quantity)).ToList(), OrderDate: today), cancellationToken));
            return Require(await orders.ConfirmAsync(created.Id, cancellationToken));
        }

        async Task<ShipmentSummary> DraftAsync(OrderSummary order, string? carrier = null) =>
            Require(await shipments.CreateAsync(new SaveShipmentRequest(order.Id, order.Lines.Select(static l => new SaveShipmentLineRequest(l.Id, l.QtyReserved)).ToList(), Carrier: carrier), cancellationToken));

        // Baghdad Mall: picked (one line short), posted, packed and handed to the carrier.
        var mall = await OrderAsync("cust:baghdad-mall", (beverages[0], 24m), (beverages[1], 12m), (groceries[0], 20m));
        var mallShipment = await DraftAsync(mall, "Iraqi Post Express");
        var mallList = Require(await shipments.ReleaseAsync(mallShipment.Id, cancellationToken)).PickListId!.Value;
        var mallLines = (await picks.FindAsync(mallList, cancellationToken))!.Lines;
        var shortLine = mallLines.Last(l => l.ItemId == beverages[1].Id);
        foreach (var line in mallLines)
        {
            var isShort = line.Id == shortLine.Id && line.QtyToPick > 2m;
            Require(await picks.PickAsync(mallList, line.Id, new PickLineRequest(isShort ? line.QtyToPick - 2m : line.QtyToPick, ShortReason: isShort ? "Carton damaged" : null), cancellationToken));
        }

        // Picked on the operator's scanner: the list is his (handed over after the seed's own confirmations).
        Require(await picks.AssignAsync(mallList, new AssignPickListRequest(picker), cancellationToken));
        var mallPosted = Require(await shipments.PostAsync(mallShipment.Id, cancellationToken));
        var byItem = mallPosted.Lines.ToDictionary(static l => l.ItemId);
        Require(await shipments.SavePackagesAsync(mallShipment.Id, new SavePackagesRequest(
        [
            new([new(byItem[beverages[0].Id].OrderLineId, byItem[beverages[0].Id].Quantity)], "carton", 14.5m, 60m, 40m, 35m, "IPX-4471-0001"),
            new([new(byItem[beverages[1].Id].OrderLineId, byItem[beverages[1].Id].Quantity), new(byItem[groceries[0].Id].OrderLineId, byItem[groceries[0].Id].Quantity)], "carton", 18.2m, 60m, 40m, 40m, "IPX-4471-0002"),
        ]), cancellationToken));
        Require(await shipments.SaveCarrierAsync(mallShipment.Id, new SaveCarrierRequest("Iraqi Post Express", "IPX-4471"), cancellationToken));

        // Al-Noor: with the picker on the scanner, the first stop done.
        var noor = await OrderAsync("cust:al-noor", (beverages[2], 18m), (beverages[0], 6m));
        var noorShipment = await DraftAsync(noor);
        var noorList = Require(await shipments.ReleaseAsync(noorShipment.Id, cancellationToken)).PickListId!.Value;
        var noorFirst = (await picks.FindAsync(noorList, cancellationToken))!.Lines[0];
        Require(await picks.PickAsync(noorList, noorFirst.Id, new PickLineRequest(noorFirst.QtyToPick), cancellationToken));
        Require(await picks.AssignAsync(noorList, new AssignPickListRequest(picker), cancellationToken));

        // Kurdistan Distribution: released, waiting for someone to take it.
        var kurdistan = await OrderAsync("cust:kurdistan-dist", (beverages[1], 30m), (groceries[0], 10m));
        Require(await shipments.ReleaseAsync((await DraftAsync(kurdistan)).Id, cancellationToken));

        // Basra Oil: a draft, located but not yet sent to the warehouse.
        await DraftAsync(await OrderAsync("cust:basra-oil", (beverages[2], 10m), (groceries[0], 8m)));

        return new DemoShippingOutcome(Orders: 4, Shipments: 4, PickLists: 3);
    }

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}

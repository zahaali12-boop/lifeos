using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Inventory.Application;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Persistence;

namespace Quicker.Migrator.Demo;

/// <summary>What the warehouse activity seed produced: transfers and counts.</summary>
public sealed record DemoOperationsOutcome(int Transfers, int Counts);

/// <summary>
/// Warehouse activity for the live month (roadmap 4.9, ASSUMPTIONS A-115), so the transfer and count screens open on
/// real documents: in the trading company, a two-step transfer from Basra to Erbil received two days after it shipped,
/// a second one still in transit, a count of the Basra beverages posted with one counting difference and its reason,
/// and a count of the Erbil warehouse frozen and waiting to be counted. Everything goes through the inventory services
/// and the stock posting engine.
/// </summary>
internal static class DemoOperations
{
    public static async Task<DemoOperationsOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var transfers = services.GetRequiredService<TransferService>();
        var counts = services.GetRequiredService<CountService>();
        var companyId = companies.Single(static c => c.Definition.Code == "IQT").Company.Id;
        var opening = new DateOnly(today.Year, today.Month, 1);
        DateOnly Day(int offset) => opening.AddDays(offset) < today ? opening.AddDays(offset) : today;

        async Task<Guid> WarehouseAsync(string code) => await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.inv_warehouses WHERE tenant_id = @tenant AND company_id = @companyId AND code = @code", new { tenant, companyId, code }, unitOfWork.Transaction, cancellationToken: cancellationToken));

        var basra = await WarehouseAsync("BSR-WH");
        var erbil = await WarehouseAsync("EBL-WH");
        var beverages = (await unitOfWork.Connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.itm_items WHERE tenant_id = @tenant AND code LIKE 'BEV-%' AND tracking = 'none' ORDER BY code LIMIT 4", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToArray();

        // Basra to Erbil: one received, one still on the road.
        var received = Require(await transfers.CreateAsync(new SaveTransferRequest(companyId, basra, erbil,
            [new SaveTransferLineRequest(ItemId: beverages[2], Quantity: 30m, Uom: "PCS"), new SaveTransferLineRequest(ItemId: beverages[1], Quantity: 20m, Uom: "PCS")], Reference: "Erbil restock"), cancellationToken));
        Require(await transfers.ShipAsync(received.Id, new ShipTransferRequest(Day(15)), cancellationToken));
        Require(await transfers.ReceiveAsync(received.Id, new ReceiveTransferRequest(Day(17)), cancellationToken));
        var onTheRoad = Require(await transfers.CreateAsync(new SaveTransferRequest(companyId, basra, erbil,
            [new SaveTransferLineRequest(ItemId: beverages[2], Quantity: 40m, Uom: "PCS")], Reference: "Erbil weekend promotion"), cancellationToken));
        Require(await transfers.ShipAsync(onTheRoad.Id, new ShipTransferRequest(Day(19)), cancellationToken));

        // The Basra beverages counted: two cartons of the first item missing, explained as a counting difference.
        var count = Require(await counts.CreateAsync(new SaveCountRequest(companyId, basra, Scope: "items", ItemIds: beverages, PostingDate: today, Notes: "Month-end beverage count"), cancellationToken));
        Require(await counts.FreezeAsync(count.Id, cancellationToken));
        var sheet = Require(await counts.SheetAsync(count.Id, cancellationToken));
        Require(await counts.EnterAsync(count.Id, new CountEntriesRequest([.. sheet.Lines.Select((l, i) => new CountEntryRequest(LineId: l.Id, CountedQty: (l.ExpectedQty ?? 0m) - (i == 0 ? 2m : 0m)))]), cancellationToken));
        var reviewed = Require(await counts.ReviewAsync(count.Id, cancellationToken));
        foreach (var line in reviewed.Lines.Where(static l => l.VarianceQty is not null and not 0m))
        {
            Require(await counts.SetReasonAsync(count.Id, line.Id, new CountLineReasonRequest(ReasonCode: "MISCOUNT", Note: "Two units short on the shelf"), cancellationToken));
        }

        Require(await counts.ApproveAsync(count.Id, cancellationToken));
        Require(await counts.PostAsync(count.Id, cancellationToken));

        // Erbil frozen and waiting for the counters (the scanner picks it up).
        var waiting = Require(await counts.CreateAsync(new SaveCountRequest(companyId, erbil, PostingDate: today, Notes: "Quarterly full count"), cancellationToken));
        Require(await counts.FreezeAsync(waiting.Id, cancellationToken));

        return new DemoOperationsOutcome(2, 2);
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

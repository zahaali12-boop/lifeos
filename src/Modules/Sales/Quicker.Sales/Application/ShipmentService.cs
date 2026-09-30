using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Sales.Domain;
using Quicker.Sales.Persistence;

namespace Quicker.Sales.Application;

/// <summary>
/// Shipments of a confirmed order's reserved lines (roadmap 5.5, DOMAIN_MODEL §11, POSTING_RULES "sale_shipment"). A
/// draft is checked against what the order's lines still have reserved and its stock is located by the inventory
/// module's planner (<see cref="IPickLists"/>, A-154): lots first expiry first out, bins in their pick sequence,
/// serials oldest received first -- one line may take from several bins and lots, and a lot-and-serial item takes a
/// lot first and then serials from within it. The draft then either posts directly (located again at that moment) or
/// is released for picking: a pick list goes to the warehouse, the shipment waits in "picking", and posting ships
/// exactly what was picked (a short pick reduces the line; what is not shipped stays reserved on the order). Posting
/// goes through the stock engine as <see cref="StockEntryTypes.SaleShipment"/>, consuming the order's reservations and
/// letting the costing engine book cost of goods sold (ADR-0008); each part of each line keeps the ledger entries it
/// produced, and a reversal returns every one of them at its own cost and re-reserves the quantity for the order.
/// Packages record which box holds what, with weight, size and the carrier's tracking number.
/// </summary>
public sealed class ShipmentService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    ICustomerDirectory customers,
    IStockReservations reservations,
    IInventoryPosting inventory,
    IPickLists picks,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "sales_shipment";

    public static readonly IReadOnlyList<string> PackageTypes = ["box", "carton", "pallet", "envelope", "crate", "drum", "bag", "other"];

    private static readonly string[] ShippableOrderStatuses = ["confirmed", "partially_shipped"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ShipmentSummary>> ListAsync(Guid? companyId, string? status, Guid? orderId, CancellationToken cancellationToken)
    {
        var query = db.Shipments.AsNoTracking().Include(static s => s.Lines).Include(static s => s.Packages).ThenInclude(static p => p.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status == status);
        }

        if (orderId is { } o)
        {
            query = query.Where(s => s.OrderId == o);
        }

        var shipments = await query.OrderByDescending(static s => s.PostingDate).ThenByDescending(static s => s.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<ShipmentSummary>(shipments.Count);
        foreach (var shipment in shipments)
        {
            result.Add(await MapAsync(shipment, cancellationToken));
        }

        return result;
    }

    public async Task<Result<ShipmentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.AsNoTracking().Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return shipment is null ? Error.NotFound(DocumentType, id) : await MapAsync(shipment, cancellationToken);
    }

    public async Task<Result<ShipmentSummary>> CreateAsync(SaveShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        if (order is null)
        {
            return Error.Validation("shipment.order_unknown", "The sales order does not exist.").WithWhy(("orderId", request.OrderId));
        }

        var shipment = new SalesShipment { Id = Guid.CreateVersion7(), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(shipment, order, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(order.CompanyId), "SHP", "SHP-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(order.CompanyId), null, shipment.PostingDate, shipment.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        shipment.Number = number.Value.Text;
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Created, After: new { shipment.Number, order = order.Number, lines = shipment.Lines.Count }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    public async Task<Result<ShipmentSummary>> UpdateAsync(Guid id, SaveShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await db.Shipments.Include(static s => s.Lines).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        var editable = Editable(shipment);
        if (editable.IsFailure)
        {
            return editable.Error!;
        }

        if (request.OrderId != shipment.OrderId)
        {
            return Error.Validation("shipment.order_locked", "A shipment stays with the order it was drafted against.").WithWhy(("orderId", shipment.OrderId));
        }

        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
        db.ShipmentLines.RemoveRange(shipment.Lines);
        shipment.Lines.Clear();
        var applied = await ApplyAsync(shipment, order, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var packed = PackedWithinLines(shipment);
        if (packed.IsFailure)
        {
            return packed.Error!;
        }

        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>Deletes a draft; a shipment being picked has its pick list cancelled with it.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is not ("draft" or "picking"))
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment is deleted; a posted one is reversed.").WithWhy(("status", shipment.Status));
        }

        if (shipment.Status == "picking")
        {
            var cancelled = await CancelPickListAsync(shipment, "The shipment was deleted.", cancellationToken);
            if (cancelled.IsFailure)
            {
                return cancelled.Error!;
            }
        }

        db.Shipments.Remove(shipment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Deleted, CompanyId: shipment.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Sends a draft to the warehouse: its lines are located again as the stock stands now and kept as a pick
    /// list, and the shipment waits in "picking" until every line is picked or short-picked.</summary>
    public async Task<Result<ShipmentSummary>> ReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "draft")
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment is released for picking.").WithWhy(("status", shipment.Status));
        }

        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
        var checkedLines = await CheckAsync(order, Request(shipment), cancellationToken);
        if (checkedLines.IsFailure)
        {
            return checkedLines.Error!;
        }

        var released = await picks.ReleaseAsync(new PickReleaseRequest(
            new PickPlanRequest(shipment.CompanyId, shipment.WarehouseId, OrderService.DocumentType, order.Id, shipment.PostingDate,
                shipment.Lines.Select(static l => new PickRequestLine(l.Id, l.ItemId, l.VariantId, l.QuantityBase, l.BinId)).ToList()),
            DocumentType, shipment.Id, shipment.Number), cancellationToken);
        if (released.IsFailure)
        {
            return released.Error!;
        }

        shipment.Status = "picking";
        shipment.PickListId = released.Value.Id;
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.StateChanged, After: new { status = shipment.Status, pickList = released.Value.Number }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>Takes a shipment back from the warehouse: its pick list is cancelled (if the warehouse has not already) and the shipment is a draft again.</summary>
    public async Task<Result<ShipmentSummary>> CancelPickingAsync(Guid id, CancelPickingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("shipment.reason_required", "Taking a shipment back from picking names the reason.");
        }

        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "picking")
        {
            return Error.Conflict("shipment.not_picking", "The shipment is not being picked.").WithWhy(("status", shipment.Status));
        }

        var cancelled = await CancelPickListAsync(shipment, reason, cancellationToken);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        shipment.Status = "draft";
        shipment.PickListId = null;
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.StateChanged, After: new { status = shipment.Status }, Reason: reason, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>
    /// Posts the shipment. Released for picking, it ships what the pick list says was picked, once every line is picked
    /// or short-picked; otherwise its stock is located now. Either way the lines are re-checked against the order's
    /// reservations as they stand, the stock engine consumes them (oldest first) and books cost of goods sold, each
    /// part of each line keeps the entries it produced, and the order's shipped quantities and status follow.
    /// </summary>
    public async Task<Result<ShipmentSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is not ("draft" or "picking"))
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment, or one being picked, is posted.").WithWhy(("status", shipment.Status));
        }

        var order = await db.Orders.Include(static o => o.Lines).SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
        PickListInfo? pickList = null;
        if (shipment.Status == "picking")
        {
            var picked = await ApplyPicksAsync(shipment, order, cancellationToken);
            if (picked.IsFailure)
            {
                return picked.Error!;
            }

            pickList = picked.Value;
        }
        else
        {
            var kept = shipment.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
            var request = Request(shipment);
            db.ShipmentLines.RemoveRange(shipment.Lines);
            shipment.Lines.Clear();
            var applied = await ApplyAsync(shipment, order, request, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }

            foreach (var line in shipment.Lines)
            {
                if (kept.TryGetValue(line.LineNo, out var lineId))
                {
                    line.Id = lineId;
                }
            }
        }

        var packed = PackedWithinLines(shipment);
        if (packed.IsFailure)
        {
            return packed.Error!;
        }

        var activeReservations = (await reservations.ForDocumentAsync(OrderService.DocumentType, order.Id, cancellationToken)).Where(static r => r.Status == "active").OrderBy(static r => r.CreatedAt).ToList();
        var stockLines = new List<StockLine>();
        foreach (var line in shipment.Lines)
        {
            // Each part of the line draws on the order line's reservations, oldest first; one reservation may
            // cover several parts and one part may need several reservations.
            var holds = activeReservations.Where(r => r.SourceLineId == line.OrderLineId).Select(static r => (r.Id, Left: r.Remaining)).ToList();
            var hold = 0;
            foreach (var allocation in Allocations(line.Allocations))
            {
                var remaining = allocation.Quantity;
                var serialsTaken = 0;
                while (remaining > 0m)
                {
                    while (hold < holds.Count && holds[hold].Left <= 0m)
                    {
                        hold++;
                    }

                    if (hold >= holds.Count)
                    {
                        return Error.Conflict("shipment.exceeds_reserved", "The order line no longer has enough reserved to ship; review its reservations and try again.").WithWhy(("orderLineId", line.OrderLineId), ("shortBy", remaining));
                    }

                    var take = Math.Min(remaining, holds[hold].Left);
                    IReadOnlyList<string>? serials = null;
                    if (allocation.SerialNumbers.Count > 0)
                    {
                        serials = allocation.SerialNumbers.Skip(serialsTaken).Take((int)take).ToList();
                        serialsTaken += serials.Count;
                    }

                    stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.SaleShipment, take, shipment.WarehouseId, VariantId: line.VariantId, BinId: allocation.BinId, LotId: allocation.LotId,
                        SourceLineId: line.Id, ReservationId: holds[hold].Id, PartnerId: order.PartnerId, SerialNumbers: serials));
                    holds[hold] = (holds[hold].Id, holds[hold].Left - take);
                    remaining -= take;
                }
            }
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(shipment.CompanyId, shipment.PostingDate, DocumentType, shipment.Id, stockLines, IdempotencyKey: $"sales_shipment:{shipment.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var entries = posted.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.OrderBy(static e => e.Sequence).ToList());
        foreach (var line in shipment.Lines)
        {
            if (!entries.TryGetValue(line.Id, out var own))
            {
                continue;
            }

            // What the line actually shipped, part by part, as the stock engine wrote it -- in the order the parts were
            // planned (the walking order), since entries written by one posting carry no dependable order of their own.
            var planned = Allocations(line.Allocations).ToList();
            var parts = own.GroupBy(static e => (e.BinId, e.LotId)).OrderBy(g => Rank(planned.FindIndex(a => a.BinId == g.Key.BinId && a.LotId == g.Key.LotId))).Select(g =>
            {
                var like = planned.FirstOrDefault(a => a.BinId == g.Key.BinId && a.LotId == g.Key.LotId);
                var serialOrder = like?.SerialNumbers.ToList() ?? [];
                var list = g.OrderBy(e => Rank(e.SerialNumber is null ? -1 : serialOrder.IndexOf(e.SerialNumber))).ThenBy(static e => e.Sequence).ToList();
                return new ShipmentAllocation(g.Key.BinId, like?.BinCode, g.Key.LotId, list[0].LotNumber ?? like?.LotNumber, like?.ExpiresOn,
                    list.Where(static e => e.SerialNumber is not null).Select(static e => e.SerialNumber!).ToList(), list.Sum(static e => -e.Quantity),
                    list.Select(static e => new ShipmentAllocationEntry(e.Id, e.SerialNumber, -e.Quantity)).ToList());
            }).ToList();
            SetAllocations(line, parts);
            line.SleId = own[0].Id;
            line.SleIds = JsonSerializer.Serialize(own.Select(static e => e.Id).ToList());
            // An outbound entry's cost amount is signed negative (the value leaving inventory); cost of goods sold is reported positive.
            line.CogsAmount = own.Sum(static e => Math.Abs(e.CostAmount));
        }

        if (pickList is not null)
        {
            var closed = await picks.CloseAsync(pickList.Id, cancellationToken);
            if (closed.IsFailure)
            {
                return closed.Error!;
            }
        }

        shipment.StockPostingId = posted.Value.PostingId;
        shipment.JournalEntryId = posted.Value.JournalEntryId;
        shipment.Status = "posted";
        shipment.PostedAt = clock.UtcNow;
        shipment.PostedBy = principal.Principal?.MembershipId.Value;
        shipment.UpdatedAt = clock.UtcNow;
        Ship(order, shipment);
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Posted, After: new { status = shipment.Status, order = order.Number, totalCogs = shipment.Lines.Sum(static l => l.CogsAmount), journal = posted.Value.JournalNumber, orderStatus = order.Status, pickList = pickList?.Number }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>Reverses a posted shipment as a whole: a sale return applied to each ledger entry the shipment wrote
    /// puts the stock back where it came from at that entry's exact cost, and the returned quantity is re-reserved for
    /// the order exactly as a fresh reservation would be (a short warehouse re-reserves what it can and leaves the line
    /// backordered again).</summary>
    public async Task<Result<ShipmentSummary>> ReverseAsync(Guid id, ReverseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("shipment.reason_required", "A reversal names its reason.");
        }

        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "posted")
        {
            return Error.Conflict("shipment.not_posted", "Only a posted shipment is reversed.").WithWhy(("status", shipment.Status));
        }

        var company = await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < shipment.PostingDate)
        {
            return Error.Validation("shipment.reversal_date_invalid", "A shipment is reversed on or after the day it was posted.").WithWhy(("postingDate", shipment.PostingDate), ("reversalDate", reversalDate));
        }

        var order = await db.Orders.Include(static o => o.Lines).SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
        var stockLines = new List<StockLine>();
        foreach (var line in shipment.Lines)
        {
            // The order line's warehouse, which is where the stock left from: shipments posted before a shipment had
            // to leave from one warehouse may carry lines of another than the header names.
            var warehouseId = order.Lines.Single(ol => ol.Id == line.OrderLineId).WarehouseId ?? order.WarehouseId;
            foreach (var allocation in Allocations(line.Allocations))
            {
                foreach (var entry in allocation.Entries ?? [])
                {
                    stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.SaleReturn, entry.Quantity, warehouseId, VariantId: line.VariantId, BinId: allocation.BinId, LotId: allocation.LotId,
                        SourceLineId: line.Id, AppliesToSleId: entry.SleId, PartnerId: shipment.PartnerId, SerialNumbers: entry.SerialNumber is { } serial ? [serial] : null));
                }
            }
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(shipment.CompanyId, reversalDate, DocumentType, shipment.Id, stockLines, IdempotencyKey: $"sales_shipment_reversal:{shipment.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var unshipped = await UnshipAsync(order, shipment, cancellationToken);
        if (unshipped.IsFailure)
        {
            return unshipped.Error!;
        }

        shipment.Status = "reversed";
        shipment.ReversalPostingId = posted.Value.PostingId;
        shipment.ReversalReason = reason;
        shipment.ReversedAt = clock.UtcNow;
        shipment.ReversedBy = principal.Principal?.MembershipId.Value;
        shipment.UpdatedAt = clock.UtcNow;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Reversed, After: new { status = shipment.Status, reversalDate, journal = posted.Value.JournalNumber, orderStatus = order.Status }, Reason: reason, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>Replaces the shipment's packages: each holds a quantity of some of the shipment's order lines, and no
    /// order line is packed beyond what the shipment carries. Packing goes on until the shipment is reversed, since
    /// weights and tracking numbers often arrive after the goods left.</summary>
    public async Task<Result<ShipmentSummary>> SavePackagesAsync(Guid id, SavePackagesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status == "reversed")
        {
            return Error.Conflict("shipment.reversed", "A reversed shipment is not packed.").WithWhy(("status", shipment.Status));
        }

        var packages = new List<SalesShipmentPackage>();
        var packageNo = 0;
        foreach (var p in request.Packages ?? [])
        {
            packageNo++;
            var type = Shared.Trim(p.PackageType)?.ToLowerInvariant() ?? "box";
            if (!PackageTypes.Contains(type, StringComparer.Ordinal))
            {
                return Error.Validation("shipment.package_type_invalid", "Unknown package type.").WithWhy(("packageNo", packageNo), ("packageType", type), ("allowed", PackageTypes));
            }

            if (p.WeightKg <= 0m || p.LengthCm <= 0m || p.WidthCm <= 0m || p.HeightCm <= 0m)
            {
                return Error.Validation("shipment.package_measure_invalid", "Weights and dimensions are positive when given.").WithWhy(("packageNo", packageNo));
            }

            if (p.Contents is null || p.Contents.Count == 0)
            {
                return Error.Validation("shipment.package_empty", "A package holds at least one line of the shipment.").WithWhy(("packageNo", packageNo));
            }

            var package = new SalesShipmentPackage
            {
                Id = Guid.CreateVersion7(),
                ShipmentId = shipment.Id,
                PackageNo = packageNo,
                PackageNumber = $"{shipment.Number}-{packageNo:D2}",
                PackageType = type,
                WeightKg = p.WeightKg,
                LengthCm = p.LengthCm,
                WidthCm = p.WidthCm,
                HeightCm = p.HeightCm,
                TrackingNumber = Shared.Trim(p.TrackingNumber),
                CreatedAt = clock.UtcNow,
            };
            foreach (var content in p.Contents)
            {
                if (shipment.Lines.All(l => l.OrderLineId != content.OrderLineId))
                {
                    return Error.Validation("shipment.package_line_unknown", "A package holds lines of this shipment only.").WithWhy(("packageNo", packageNo), ("orderLineId", content.OrderLineId));
                }

                if (content.Quantity <= 0m)
                {
                    return Error.Validation("shipment.quantity_invalid", "Quantities are positive.").WithWhy(("packageNo", packageNo), ("orderLineId", content.OrderLineId));
                }

                if (package.Lines.Any(l => l.OrderLineId == content.OrderLineId))
                {
                    return Error.Validation("shipment.package_line_duplicated", "A line appears once per package.").WithWhy(("packageNo", packageNo), ("orderLineId", content.OrderLineId));
                }

                package.Lines.Add(new SalesShipmentPackageLine { Id = Guid.CreateVersion7(), PackageId = package.Id, OrderLineId = content.OrderLineId, Quantity = content.Quantity });
            }

            packages.Add(package);
        }

        db.ShipmentPackages.RemoveRange(shipment.Packages);
        shipment.Packages.Clear();
        shipment.Packages.AddRange(packages);
        var packed = PackedWithinLines(shipment);
        if (packed.IsFailure)
        {
            return packed.Error!;
        }

        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Updated, After: new { packages = packages.Count, weightKg = packages.Sum(static p => p.WeightKg ?? 0m) }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    public async Task<Result<ShipmentSummary>> SaveCarrierAsync(Guid id, SaveCarrierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).Include(static s => s.Packages).ThenInclude(static p => p.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status == "reversed")
        {
            return Error.Conflict("shipment.reversed", "A reversed shipment keeps its carrier details as they were.").WithWhy(("status", shipment.Status));
        }

        var before = new { shipment.Carrier, shipment.TrackingNumber };
        shipment.Carrier = Shared.Trim(request.Carrier);
        shipment.TrackingNumber = Shared.Trim(request.TrackingNumber);
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Updated, Before: before, After: new { shipment.Carrier, shipment.TrackingNumber }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private sealed record CheckedLine(int LineNo, SalesOrderLine OrderLine, ItemInfo Item, decimal Quantity, Guid? BinId);

    private sealed record Checked(IReadOnlyList<CheckedLine> Lines, DateOnly PostingDate, Guid WarehouseId, string CustomFields);

    private static Result Editable(SalesShipment shipment) => shipment.Status switch
    {
        "draft" => Result.Success(),
        "picking" => Error.Conflict("shipment.picking_in_progress", "The shipment is with the warehouse; take it back from picking before changing it.").WithWhy(("status", shipment.Status)),
        _ => Error.Conflict("shipment.not_draft", "Only a draft shipment is edited.").WithWhy(("status", shipment.Status)),
    };

    private static SaveShipmentRequest Request(SalesShipment shipment) =>
        new(shipment.OrderId, shipment.Lines.OrderBy(static l => l.LineNo).Select(static l => new SaveShipmentLineRequest(l.OrderLineId, l.Quantity, l.BinId)).ToList(),
            shipment.PostingDate, shipment.Carrier, shipment.TrackingNumber, shipment.Notes, shipment.BranchId, Shared.Parse(shipment.CustomFields));

    /// <summary>Validates the request against the order and its lines' reservations as they stand now: the order ships, every line is its own, not cancelled, not drop-shipped, reserved enough, and every line ships from one warehouse.</summary>
    private async Task<Result<Checked>> CheckAsync(SalesOrder order, SaveShipmentRequest request, CancellationToken cancellationToken)
    {
        if (!ShippableOrderStatuses.Contains(order.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("shipment.order_not_shippable", "Only a confirmed or partially shipped order is shipped against.").WithWhy(("order", order.Number), ("status", order.Status));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("shipment.lines_required", "A shipment has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", order.CompanyId);
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        var activeReservations = (await reservations.ForDocumentAsync(OrderService.DocumentType, order.Id, cancellationToken)).Where(static r => r.Status == "active").ToList();
        var seenOrderLines = new HashSet<Guid>();
        var lines = new List<CheckedLine>();
        Guid? warehouseId = null;
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            var orderLine = order.Lines.SingleOrDefault(x => x.Id == l.OrderLineId);
            if (orderLine is null)
            {
                return Error.Validation("shipment.order_line_unknown", "The order line does not belong to the order.").WithWhy(("lineNo", lineNo), ("orderLineId", l.OrderLineId));
            }

            if (!seenOrderLines.Add(orderLine.Id))
            {
                return Error.Validation("shipment.order_line_duplicated", "An order line is shipped once per shipment.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo));
            }

            if (orderLine.Status == "cancelled")
            {
                return Error.Conflict("shipment.order_line_cancelled", "The order line has been cancelled.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo));
            }

            if (orderLine.DropShip)
            {
                return Error.Conflict("shipment.line_is_drop_ship", "A drop-ship line ships from the supplier directly; there is nothing to pick here.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo));
            }

            if (l.Quantity <= 0m)
            {
                return Error.Validation("shipment.quantity_invalid", "Quantities are positive.").WithWhy(("lineNo", lineNo));
            }

            var item = await items.FindAsync(orderLine.ItemId, cancellationToken);
            if (item is null)
            {
                return Error.NotFound("item", orderLine.ItemId);
            }

            if (item.Tracking is "serial" or "lot_and_serial" && l.Quantity != decimal.Truncate(l.Quantity))
            {
                return Error.Validation("shipment.serial_quantity_not_whole", "A serial-tracked item ships in whole units.").WithWhy(("lineNo", lineNo), ("item", item.Code), ("quantity", l.Quantity));
            }

            var reserved = activeReservations.Where(r => r.SourceLineId == orderLine.Id).Sum(static r => r.Remaining);
            if (l.Quantity > reserved)
            {
                return Error.Conflict("shipment.exceeds_reserved", "The shipment exceeds what the order line has reserved.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo), ("reserved", reserved), ("requested", l.Quantity));
            }

            var lineWarehouse = orderLine.WarehouseId ?? order.WarehouseId;
            if (warehouseId is { } first && first != lineWarehouse)
            {
                return Error.Validation("shipment.one_warehouse", "A shipment leaves from one warehouse; ship the lines of another warehouse separately.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo));
            }

            warehouseId = lineWarehouse;
            lines.Add(new CheckedLine(lineNo, orderLine, item, l.Quantity, l.BinId));
        }

        return new Checked(lines, postingDate, warehouseId!.Value, custom.Value);
    }

    /// <summary>Checks the request, locates every line's stock (the planner's allocations) and rebuilds the shipment's header and lines.</summary>
    private async Task<Result> ApplyAsync(SalesShipment shipment, SalesOrder order, SaveShipmentRequest request, CancellationToken cancellationToken)
    {
        var checkedLines = await CheckAsync(order, request, cancellationToken);
        if (checkedLines.IsFailure)
        {
            return checkedLines.Error!;
        }

        var ok = checkedLines.Value;
        var lines = ok.Lines.Select(c => new SalesShipmentLine
        {
            Id = Guid.CreateVersion7(),
            TenantId = shipment.TenantId,
            ShipmentId = shipment.Id,
            LineNo = c.LineNo,
            OrderLineId = c.OrderLine.Id,
            ItemId = c.OrderLine.ItemId,
            VariantId = c.OrderLine.VariantId,
            Quantity = c.Quantity,
            UomId = c.Item.BaseUomId,
            QuantityBase = c.Quantity,
            BinId = c.BinId,
            CreatedAt = clock.UtcNow,
        }).ToList();

        var plan = await picks.PlanAsync(new PickPlanRequest(order.CompanyId, ok.WarehouseId, OrderService.DocumentType, order.Id, ok.PostingDate,
            lines.Select(static l => new PickRequestLine(l.Id, l.ItemId, l.VariantId, l.QuantityBase, l.BinId)).ToList()), cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        foreach (var line in lines)
        {
            var located = plan.Value.Single(p => p.SourceLineId == line.Id);
            var item = ok.Lines.Single(c => c.LineNo == line.LineNo).Item;
            if (located.Located < located.Requested)
            {
                return item.Tracking is "serial" or "lot_and_serial"
                    ? Error.Conflict("shipment.not_enough_serials_on_hand", "Not enough serialised units are on hand to cover this quantity.").WithWhy(("lineNo", line.LineNo), ("item", item.Code), ("available", located.Located), ("requested", line.Quantity))
                    : Error.Conflict("shipment.not_enough_stock_located", "Not all of the quantity can be located in the warehouse (lots expired or on hold, bins not for picking, or stock another pick list already claims); ship less or later.").WithWhy(("lineNo", line.LineNo), ("item", item.Code), ("located", located.Located), ("requested", line.Quantity));
            }

            SetAllocations(line, located.Allocations.Select(static a => new ShipmentAllocation(a.BinId, a.BinCode, a.LotId, a.LotNumber, a.ExpiresOn, a.SerialNumbers, a.Quantity)).ToList());
        }

        shipment.CompanyId = order.CompanyId;
        shipment.OrderId = order.Id;
        shipment.PartnerId = order.PartnerId;
        shipment.WarehouseId = ok.WarehouseId;
        shipment.PostingDate = ok.PostingDate;
        shipment.Carrier = Shared.Trim(request.Carrier);
        shipment.TrackingNumber = Shared.Trim(request.TrackingNumber);
        shipment.Notes = Shared.Trim(request.Notes);
        shipment.BranchId = request.BranchId ?? order.BranchId;
        shipment.CustomFields = ok.CustomFields;
        shipment.Lines.AddRange(lines);
        return Result.Success();
    }

    /// <summary>For a shipment being picked: every line's quantity and allocations become what was picked, once the pick
    /// list is complete. A line with nothing picked leaves the shipment; its order line keeps its reservation.</summary>
    private async Task<Result<PickListInfo>> ApplyPicksAsync(SalesShipment shipment, SalesOrder order, CancellationToken cancellationToken)
    {
        var list = shipment.PickListId is { } listId ? await picks.FindAsync(listId, cancellationToken) : null;
        if (list is null || list.Status == PickListStatuses.Cancelled)
        {
            return Error.Conflict("shipment.pick_list_cancelled", "The warehouse cancelled the pick list; take the shipment back from picking and release it again.").WithWhy(("pickList", list?.Number));
        }

        if (list.Status != PickListStatuses.Picked)
        {
            return Error.Conflict("shipment.picking_incomplete", "Not every line has been picked yet.").WithWhy(("pickList", list.Number), ("linesDone", list.LinesDone), ("linesTotal", list.LinesTotal));
        }

        var byLine = list.Lines.Where(static l => l.QtyPicked > 0m).GroupBy(static l => l.SourceLineId).ToDictionary(static g => g.Key, static g => g.ToList());
        var emptied = shipment.Lines.Where(l => !byLine.ContainsKey(l.Id)).ToList();
        if (emptied.Count == shipment.Lines.Count)
        {
            return Error.Conflict("shipment.nothing_picked", "Nothing was picked; take the shipment back from picking or delete it.").WithWhy(("pickList", list.Number));
        }

        db.ShipmentLines.RemoveRange(emptied);
        foreach (var line in emptied)
        {
            shipment.Lines.Remove(line);
        }

        foreach (var line in shipment.Lines)
        {
            var picked = byLine[line.Id];
            line.Quantity = picked.Sum(static p => p.QtyPicked);
            line.QuantityBase = line.Quantity;
            SetAllocations(line, picked.GroupBy(static p => (p.PickedBinId, p.PickedLotId)).Select(static g =>
            {
                var first = g.First();
                return new ShipmentAllocation(first.PickedBinId, first.PickedBinCode, first.PickedLotId, first.PickedLotNumber, first.PickedLotId == first.LotId ? first.ExpiresOn : null,
                    g.SelectMany(static p => p.PickedSerialNumbers).ToList(), g.Sum(static p => p.QtyPicked));
            }).ToList());
        }

        var checkedLines = await CheckAsync(order, Request(shipment), cancellationToken);
        return checkedLines.IsFailure ? checkedLines.Error! : list;
    }

    private async Task<Result> CancelPickListAsync(SalesShipment shipment, string reason, CancellationToken cancellationToken)
    {
        var list = shipment.PickListId is { } listId ? await picks.FindAsync(listId, cancellationToken) : null;
        if (list is null || !PickListStatuses.Live.Contains(list.Status, StringComparer.Ordinal))
        {
            return Result.Success();
        }

        var cancelled = await picks.CancelAsync(list.Id, reason, cancellationToken);
        return cancelled.IsFailure ? cancelled.Error! : Result.Success();
    }

    /// <summary>No order line is packed beyond what the shipment carries for it.</summary>
    private static Result PackedWithinLines(SalesShipment shipment)
    {
        foreach (var group in shipment.Packages.SelectMany(static p => p.Lines).GroupBy(static l => l.OrderLineId))
        {
            var line = shipment.Lines.SingleOrDefault(l => l.OrderLineId == group.Key);
            var packed = group.Sum(static l => l.Quantity);
            if (line is null || packed > line.Quantity)
            {
                return Error.Conflict("shipment.packed_exceeds_shipped", "More is packed than the shipment carries for the line; repack before continuing.").WithWhy(("orderLineId", group.Key), ("packed", packed), ("shipped", line?.Quantity ?? 0m));
            }
        }

        return Result.Success();
    }

    /// <summary>Keeps the line's parts and their summary: the lot when there is one only, and every serial. The line's
    /// own bin stays the one the request asked for (it narrows every later search); the parts say where it came from.</summary>
    private static void SetAllocations(SalesShipmentLine line, IReadOnlyList<ShipmentAllocation> allocations)
    {
        line.Allocations = JsonSerializer.Serialize(allocations, Json);
        var lots = allocations.Select(static a => a.LotNumber).Distinct(StringComparer.Ordinal).ToList();
        line.LotNumber = lots.Count == 1 ? lots[0] : null;
        line.SerialNumbers = JsonSerializer.Serialize(allocations.SelectMany(static a => a.SerialNumbers).ToList());
    }

    private static IReadOnlyList<ShipmentAllocation> Allocations(string json) => JsonSerializer.Deserialize<List<ShipmentAllocation>>(json, Json) ?? [];

    /// <summary>A position in a planned order; what was not planned (not found) goes last.</summary>
    private static int Rank(int index) => index < 0 ? int.MaxValue : index;

    /// <summary>Adds the shipment's quantities to the order lines, consuming their reservation, and sets the line and order statuses.</summary>
    private static void Ship(SalesOrder order, SalesShipment shipment)
    {
        foreach (var group in shipment.Lines.GroupBy(static l => l.OrderLineId))
        {
            var orderLine = order.Lines.Single(l => l.Id == group.Key);
            var shipped = group.Sum(static l => l.QuantityBase);
            orderLine.QtyShipped += shipped;
            orderLine.QtyReserved = Math.Max(0m, orderLine.QtyReserved - shipped);
            var remaining = orderLine.QuantityBase - orderLine.QtyCancelled - orderLine.QtyShipped;
            orderLine.Status = remaining <= 0m ? "fulfilled" : orderLine.QtyReserved > 0m ? "open" : "backordered";
        }

        SetOrderStatus(order);
    }

    /// <summary>Gives the shipment's quantities back to the order lines and re-reserves what stock allows, exactly as a fresh reservation would (<see cref="OrderService"/>'s own confirmation logic).</summary>
    private async Task<Result> UnshipAsync(SalesOrder order, SalesShipment shipment, CancellationToken cancellationToken)
    {
        foreach (var group in shipment.Lines.GroupBy(static l => l.OrderLineId))
        {
            var orderLine = order.Lines.Single(l => l.Id == group.Key);
            var returned = group.Sum(static l => l.QuantityBase);
            orderLine.QtyShipped = Math.Max(0m, orderLine.QtyShipped - returned);
            if (order.Status != "cancelled" && orderLine.Status != "cancelled")
            {
                var reserved = await ReserveReturnedAsync(order, orderLine, returned, cancellationToken);
                if (reserved.IsFailure)
                {
                    return reserved.Error!;
                }

                orderLine.QtyReserved += reserved.Value;
            }

            var remaining = orderLine.QuantityBase - orderLine.QtyCancelled - orderLine.QtyShipped;
            orderLine.Status = orderLine.Status == "cancelled" ? "cancelled" : remaining <= 0m ? "fulfilled" : orderLine.QtyReserved >= remaining ? "open" : "backordered";
        }

        SetOrderStatus(order);
        return Result.Success();
    }

    private async Task<Result<decimal>> ReserveReturnedAsync(SalesOrder order, SalesOrderLine line, decimal quantity, CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(line.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", line.ItemId);
        }

        var warehouseId = line.WarehouseId ?? order.WarehouseId;
        var full = await reservations.ReserveAsync(new ReservationRequest(order.CompanyId, line.ItemId, quantity, warehouseId, OrderService.DocumentType, order.Id, line.Id, item.BaseUomId, line.VariantId), cancellationToken);
        if (full.IsSuccess)
        {
            return quantity;
        }

        if (full.Error!.Code != "stock.insufficient_to_reserve")
        {
            return full.Error!;
        }

        var availability = await reservations.AvailabilityAsync(order.CompanyId, line.ItemId, warehouseId, line.VariantId, cancellationToken);
        if (availability.Available <= 0m)
        {
            return 0m;
        }

        var toReserve = Math.Min(quantity, availability.Available);
        var partial = await reservations.ReserveAsync(new ReservationRequest(order.CompanyId, line.ItemId, toReserve, warehouseId, OrderService.DocumentType, order.Id, line.Id, item.BaseUomId, line.VariantId), cancellationToken);
        return partial.IsSuccess ? toReserve : 0m;
    }

    private static void SetOrderStatus(SalesOrder order)
    {
        var live = order.Lines.Where(static l => l.Status != "cancelled").ToList();
        if (live.Count > 0 && live.All(static l => l.DropShip || l.Status == "fulfilled"))
        {
            order.Status = "shipped";
        }
        else if (live.Any(static l => l.QtyShipped > 0m))
        {
            order.Status = "partially_shipped";
        }
        else if (order.Status is "partially_shipped" or "shipped")
        {
            order.Status = "confirmed";
        }
    }

    private async Task<ShipmentSummary> MapAsync(SalesShipment s, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleAsync(o => o.Id == s.OrderId, cancellationToken);
        var partner = await customers.FindCustomerAsync(s.CompanyId, s.PartnerId, cancellationToken);
        var pickList = s.PickListId is { } listId ? await picks.FindAsync(listId, cancellationToken) : null;
        var picked = pickList?.Lines.GroupBy(static l => l.SourceLineId).ToDictionary(static g => g.Key, static g => g.Sum(static l => l.QtyPicked));
        var packedByOrderLine = s.Packages.SelectMany(static p => p.Lines).GroupBy(static l => l.OrderLineId).ToDictionary(static g => g.Key, static g => g.Sum(static l => l.Quantity));
        var itemCache = new Dictionary<Guid, ItemInfo?>();
        async Task<ItemInfo?> ItemAsync(Guid itemId)
        {
            if (!itemCache.TryGetValue(itemId, out var item))
            {
                item = await items.FindAsync(itemId, cancellationToken);
                itemCache[itemId] = item;
            }

            return item;
        }

        var lines = new List<ShipmentLineSummary>(s.Lines.Count);
        foreach (var l in s.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await ItemAsync(l.ItemId);
            var uom = (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            lines.Add(new ShipmentLineSummary(l.Id, l.LineNo, l.OrderLineId, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.VariantId, l.Quantity, l.UomId, uom?.UomCode ?? string.Empty,
                l.BinId, l.LotNumber, Serials(l.SerialNumbers), l.CogsAmount, Allocations(l.Allocations), picked is null ? null : picked.GetValueOrDefault(l.Id), packedByOrderLine.GetValueOrDefault(l.OrderLineId)));
        }

        var packages = new List<ShipmentPackageSummary>(s.Packages.Count);
        foreach (var p in s.Packages.OrderBy(static p => p.PackageNo))
        {
            var contents = new List<ShipmentPackageLineSummary>(p.Lines.Count);
            decimal? contentsWeight = 0m;
            foreach (var c in p.Lines)
            {
                var orderLine = order.Lines.Single(ol => ol.Id == c.OrderLineId);
                var item = await ItemAsync(orderLine.ItemId);
                contents.Add(new ShipmentPackageLineSummary(c.OrderLineId, orderLine.LineNo, orderLine.ItemId, item?.Code ?? string.Empty, c.Quantity));
                contentsWeight = item?.WeightKg is { } w && contentsWeight is { } sum ? sum + (w * c.Quantity) : null;
            }

            packages.Add(new ShipmentPackageSummary(p.Id, p.PackageNo, p.PackageNumber, p.PackageType, p.WeightKg, p.LengthCm, p.WidthCm, p.HeightCm, p.TrackingNumber, contentsWeight,
                contents.OrderBy(static c => c.OrderLineNo).ToList()));
        }

        var totalWeight = packages.Count > 0 && packages.All(static p => p.WeightKg is not null) ? packages.Sum(static p => p.WeightKg!.Value) : (decimal?)null;
        return new ShipmentSummary(s.Id, s.CompanyId, s.Number, s.OrderId, order.Number, s.PartnerId, partner?.PartnerCode ?? string.Empty, partner?.PartnerName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            s.WarehouseId, s.PostingDate, s.Status, s.Carrier, s.TrackingNumber, lines.Sum(static l => l.CogsAmount), s.Notes, Shared.Parse(s.CustomFields), s.ReversalReason, s.PostedAt, lines, s.UpdatedAt,
            s.PickListId, pickList?.Number, pickList?.Status, packages, totalWeight);
    }

    private static IReadOnlyList<string> Serials(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
}

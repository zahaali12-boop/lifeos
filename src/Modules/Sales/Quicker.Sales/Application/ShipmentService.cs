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
/// Shipments of a confirmed order's reserved lines (roadmap 5.5a, DOMAIN_MODEL §11, POSTING_RULES "sale_shipment"): a
/// draft is validated against what the order's lines still have reserved, posted through the stock engine as
/// <see cref="StockEntryTypes.SaleShipment"/> -- which consumes the reservation exactly as much as it ships and lets
/// the costing engine (roadmap 3.3) value and book cost of goods sold, nothing new to wire there -- and reversed as a
/// whole, returning the stock and re-reserving it for the order. A short shipment against a line's reservation
/// leaves the remainder reserved for a later one (partial shipments, roadmap 5.5 acceptance). A lot-tracked item
/// ships from the single earliest-expiring lot with enough available stock to cover the whole line
/// (<see cref="IFefoSuggestions"/>, roadmap 5.5b: a quantity no single lot covers is refused, asking for a smaller
/// quantity or a second shipment, rather than splitting one line across lots). Serial-tracked lines are still
/// refused; a real pick-list document with bin-sequence ordering, a mobile picking screen and packages follow later
/// in 5.5b/5.5c.
/// </summary>
public sealed class ShipmentService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    ICustomerDirectory customers,
    IStockReservations reservations,
    IInventoryPosting inventory,
    IFefoSuggestions fefo,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "sales_shipment";

    private static readonly string[] ShippableOrderStatuses = ["confirmed", "partially_shipped"];

    public async Task<IReadOnlyList<ShipmentSummary>> ListAsync(Guid? companyId, string? status, Guid? orderId, CancellationToken cancellationToken)
    {
        var query = db.Shipments.AsNoTracking().Include(static s => s.Lines).AsQueryable();
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
        var shipment = await db.Shipments.AsNoTracking().Include(static s => s.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
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
        var shipment = await db.Shipments.Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "draft")
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment is edited.").WithWhy(("status", shipment.Status));
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

        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "draft")
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment is deleted; a posted one is reversed.").WithWhy(("status", shipment.Status));
        }

        db.Shipments.Remove(shipment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Deleted, CompanyId: shipment.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Posts the draft: the lines are re-checked against the order's reservations as they stand now, the
    /// stock engine consumes one or more of a line's reservations (oldest first) and books cost of goods sold, and
    /// the order's shipped quantities and status follow.</summary>
    public async Task<Result<ShipmentSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status != "draft")
        {
            return Error.Conflict("shipment.not_draft", "Only a draft shipment is posted.").WithWhy(("status", shipment.Status));
        }

        var order = await db.Orders.Include(static o => o.Lines).SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
        var request = new SaveShipmentRequest(shipment.OrderId, shipment.Lines.Select(static l => new SaveShipmentLineRequest(l.OrderLineId, l.Quantity, l.BinId)).ToList(),
            shipment.PostingDate, shipment.Carrier, shipment.TrackingNumber, shipment.Notes, shipment.BranchId, Shared.Parse(shipment.CustomFields));
        var kept = shipment.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
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

        var activeReservations = (await reservations.ForDocumentAsync(OrderService.DocumentType, order.Id, cancellationToken)).Where(static r => r.Status == "active").OrderBy(static r => r.CreatedAt).ToList();
        var stockLines = new List<StockLine>();
        foreach (var line in shipment.Lines)
        {
            var orderLine = order.Lines.Single(l => l.Id == line.OrderLineId);
            var warehouseId = orderLine.WarehouseId ?? order.WarehouseId;
            var remaining = line.Quantity;
            foreach (var reservation in activeReservations.Where(r => r.SourceLineId == line.OrderLineId))
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var take = Math.Min(remaining, reservation.Remaining);
                if (take <= 0m)
                {
                    continue;
                }

                stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.SaleShipment, take, warehouseId, VariantId: line.VariantId, BinId: line.BinId,
                    SourceLineId: line.Id, ReservationId: reservation.Id, PartnerId: order.PartnerId, LotNumber: line.LotNumber));
                remaining -= take;
            }

            if (remaining > 0m)
            {
                return Error.Conflict("shipment.exceeds_reserved", "The order line no longer has enough reserved to ship; review its reservations and try again.").WithWhy(("orderLineId", line.OrderLineId), ("shortBy", remaining));
            }
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(shipment.CompanyId, shipment.PostingDate, DocumentType, shipment.Id, stockLines, IdempotencyKey: $"sales_shipment:{shipment.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var entries = posted.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.ToList());
        foreach (var line in shipment.Lines)
        {
            if (entries.TryGetValue(line.Id, out var own))
            {
                line.SleId = own[0].Id;
                line.SleIds = JsonSerializer.Serialize(own.Select(static e => e.Id).ToList());
                // An outbound entry's cost amount is signed negative (the value leaving inventory); cost of goods sold is reported positive.
                line.CogsAmount = own.Sum(static e => Math.Abs(e.CostAmount));
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
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, shipment.Number, AuditActions.Posted, After: new { status = shipment.Status, order = order.Number, totalCogs = shipment.Lines.Sum(static l => l.CogsAmount), journal = posted.Value.JournalNumber, orderStatus = order.Status }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, cancellationToken);
    }

    /// <summary>Reverses a posted shipment as a whole: a sale return applied to each original entry puts the stock
    /// back at its exact cost, and the returned quantity is re-reserved for the order exactly as a fresh
    /// reservation would be (a short warehouse re-reserves what it can and leaves the line backordered again).</summary>
    public async Task<Result<ShipmentSummary>> ReverseAsync(Guid id, ReverseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("shipment.reason_required", "A reversal names its reason.");
        }

        var shipment = await db.Shipments.Include(static s => s.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
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
        var stockLines = shipment.Lines.Select(l => new StockLine(l.ItemId, StockEntryTypes.SaleReturn, l.Quantity, order.Lines.Single(ol => ol.Id == l.OrderLineId).WarehouseId ?? order.WarehouseId,
            VariantId: l.VariantId, BinId: l.BinId, SourceLineId: l.Id, AppliesToSleId: l.SleId, PartnerId: shipment.PartnerId, LotNumber: l.LotNumber)).ToList();
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

    // ------------------------------------------------------------------ internals

    /// <summary>Validates the request against the order and its lines' reservations as they stand now, and rebuilds the shipment's header and lines.</summary>
    private async Task<Result> ApplyAsync(SalesShipment shipment, SalesOrder order, SaveShipmentRequest request, CancellationToken cancellationToken)
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
        var lines = new List<SalesShipmentLine>();
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

            if (item.Tracking is "serial" or "lot_and_serial")
            {
                return Error.Validation("shipment.serial_tracked_unsupported", "Serial-tracked items are not shipped from this screen yet (5.5b).").WithWhy(("lineNo", lineNo), ("item", item.Code), ("tracking", item.Tracking));
            }

            var reserved = activeReservations.Where(r => r.SourceLineId == orderLine.Id).Sum(static r => r.Remaining);
            if (l.Quantity > reserved)
            {
                return Error.Conflict("shipment.exceeds_reserved", "The shipment exceeds what the order line has reserved.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo), ("reserved", reserved), ("requested", l.Quantity));
            }

            string? lotNumber = null;
            if (item.Tracking == "lot")
            {
                var lineWarehouseId = orderLine.WarehouseId ?? order.WarehouseId;
                var suggestions = await fefo.SuggestAsync(order.CompanyId, item.Id, lineWarehouseId, l.Quantity, postingDate, cancellationToken);
                var chosen = suggestions.FirstOrDefault(s => s.Available >= l.Quantity);
                if (chosen is null)
                {
                    return Error.Conflict("shipment.no_single_lot_covers_quantity", "No single lot has enough available stock for this quantity (first-expiry-first-out); ship a smaller quantity or in more than one shipment.").WithWhy(("lineNo", lineNo), ("item", item.Code), ("requested", l.Quantity));
                }

                lotNumber = chosen.LotNumber;
            }

            lines.Add(new SalesShipmentLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = shipment.TenantId,
                ShipmentId = shipment.Id,
                LineNo = lineNo,
                OrderLineId = orderLine.Id,
                ItemId = orderLine.ItemId,
                VariantId = orderLine.VariantId,
                Quantity = l.Quantity,
                UomId = item.BaseUomId,
                QuantityBase = l.Quantity,
                BinId = l.BinId,
                LotNumber = lotNumber,
                CreatedAt = clock.UtcNow,
            });
        }

        shipment.CompanyId = order.CompanyId;
        shipment.OrderId = order.Id;
        shipment.PartnerId = order.PartnerId;
        shipment.WarehouseId = order.WarehouseId;
        shipment.PostingDate = postingDate;
        shipment.Carrier = Shared.Trim(request.Carrier);
        shipment.TrackingNumber = Shared.Trim(request.TrackingNumber);
        shipment.Notes = Shared.Trim(request.Notes);
        shipment.BranchId = request.BranchId ?? order.BranchId;
        shipment.CustomFields = custom.Value;
        shipment.Lines.AddRange(lines);
        return Result.Success();
    }

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
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == s.OrderId, cancellationToken);
        var partner = await customers.FindCustomerAsync(s.CompanyId, s.PartnerId, cancellationToken);
        var lines = new List<ShipmentLineSummary>(s.Lines.Count);
        foreach (var l in s.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var uom = (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            lines.Add(new ShipmentLineSummary(l.Id, l.LineNo, l.OrderLineId, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.VariantId, l.Quantity, l.UomId, uom?.UomCode ?? string.Empty, l.BinId, l.LotNumber, l.CogsAmount));
        }

        return new ShipmentSummary(s.Id, s.CompanyId, s.Number, s.OrderId, order.Number, s.PartnerId, partner?.PartnerCode ?? string.Empty, partner?.PartnerName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            s.WarehouseId, s.PostingDate, s.Status, s.Carrier, s.TrackingNumber, lines.Sum(static l => l.CogsAmount), s.Notes, Shared.Parse(s.CustomFields), s.ReversalReason, s.PostedAt, lines, s.UpdatedAt);
    }
}

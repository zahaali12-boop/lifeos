using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>What the tracking rules made of one stock line: its lot, and one serial per unit when the item is serialised.</summary>
public sealed record TrackedLine(Guid? LotId, IReadOnlyList<Guid> SerialIds);

/// <summary>
/// The tracking rules of the stock engine (roadmap 3.5): a lot-tracked item moves by lot (created on receipt with
/// its expiry, refused when quarantined, recalled or expired on the way out, FEFO suggested when none is named), a
/// serialised item moves one serial at a time (unique per item, created on receipt, on hand and in the right
/// warehouse on the way out); after posting, serial statuses, positions and events are written and blocked lots keep
/// their stock on quality hold.
/// </summary>
public sealed class TrackingResolver(InventoryDbContext db, IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, IClock clock)
{
    public async Task<Result<TrackedLine>> ResolveAsync(ItemInfo item, WarehouseInfo warehouse, StockLine line, decimal baseQuantity, DateOnly postingDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(warehouse);
        ArgumentNullException.ThrowIfNull(line);
        var lotTracked = item.Tracking is "lot" or "lot_and_serial";
        var serialTracked = item.Tracking is "serial" or "lot_and_serial";
        var inbound = baseQuantity > 0m;
        if (!lotTracked && (line.LotId is not null || !string.IsNullOrWhiteSpace(line.LotNumber)))
        {
            return Error.Validation("stock.tracking_not_used", "The item is not lot-tracked; the line names a lot.").WithWhy(("item", item.Code), ("tracking", item.Tracking));
        }

        if (!serialTracked && (line.SerialId is not null || line.SerialNumbers is { Count: > 0 }))
        {
            return Error.Validation("stock.tracking_not_used", "The item is not serialised; the line names serial numbers.").WithWhy(("item", item.Code), ("tracking", item.Tracking));
        }

        Lot? lot = null;
        if (lotTracked)
        {
            var number = line.LotNumber?.Trim();
            lot = line.LotId is { } lotId
                ? await db.Lots.SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken)
                : string.IsNullOrEmpty(number) ? null : await db.Lots.SingleOrDefaultAsync(l => l.ItemId == item.Id && l.LotNumber == number, cancellationToken);
            if (lot is null && line.LotId is not null)
            {
                return Error.NotFound("lot", line.LotId);
            }

            if (lot is null)
            {
                if (!inbound || string.IsNullOrEmpty(number))
                {
                    var why = new List<(string, object?)> { ("item", item.Code), ("warehouse", warehouse.Code) };
                    if (!inbound)
                    {
                        why.Add(("suggestion", await SuggestAsync(warehouse.CompanyId, item.Id, warehouse.Id, Math.Abs(baseQuantity), postingDate, cancellationToken)));
                    }

                    return Error.Validation("stock.lot_required", inbound ? "The item is lot-tracked; the line names the lot it receives." : "The item is lot-tracked; the line names the lot it takes (the suggestion follows first-expiry-first-out).").WithWhy([.. why]);
                }

                var expires = line.ExpiresOn ?? (item.ShelfLifeDays is { } days ? postingDate.AddDays(days) : null);
                if (item.ExpiryRequired && expires is null)
                {
                    return Error.Validation("stock.lot_expiry_required", "The item requires an expiry date on every lot.").WithWhy(("item", item.Code), ("lotNumber", number));
                }

                lot = new Lot
                {
                    Id = Guid.CreateVersion7(),
                    ItemId = item.Id,
                    LotNumber = number,
                    ManufacturedOn = line.ManufacturedOn,
                    ExpiresOn = expires,
                    SupplierLot = string.IsNullOrWhiteSpace(line.SupplierLot) ? null : line.SupplierLot.Trim(),
                    SupplierPartnerId = line.EntryType == StockEntryTypes.PurchaseReceipt ? line.PartnerId : null,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                };
                db.Lots.Add(lot);
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (lot.ItemId != item.Id)
            {
                return Error.Validation("stock.lot_wrong_item", "The lot belongs to another item.").WithWhy(("item", item.Code), ("lotNumber", lot.LotNumber));
            }

            if (!inbound && line.EntryType is StockEntryTypes.SaleShipment or StockEntryTypes.TransferOut or StockEntryTypes.AssemblyConsumption or StockEntryTypes.ConsignmentOut)
            {
                if (LotStatuses.Blocks(lot.Status))
                {
                    return Error.Conflict("stock.lot_blocked", $"Lot {lot.LotNumber} is {lot.Status}; its stock cannot be shipped, transferred or consumed.").WithWhy(("item", item.Code), ("lotNumber", lot.LotNumber), ("status", lot.Status), ("recallReference", lot.RecallReference));
                }

                if (lot.ExpiresOn is { } expiry && expiry < postingDate)
                {
                    return Error.Conflict("stock.lot_expired", $"Lot {lot.LotNumber} expired on {expiry:yyyy-MM-dd}.").WithWhy(("item", item.Code), ("lotNumber", lot.LotNumber), ("expiresOn", expiry), ("postingDate", postingDate));
                }
            }
        }

        var serialIds = new List<Guid>();
        if (serialTracked)
        {
            var quantity = Math.Abs(baseQuantity);
            if (quantity != decimal.Truncate(quantity))
            {
                return Error.Validation("stock.serial_whole_units", "A serialised item moves in whole units.").WithWhy(("item", item.Code), ("quantity", quantity));
            }

            var numbers = line.SerialNumbers?.Select(static n => n.Trim()).Where(static n => n.Length > 0).ToList() ?? [];
            if (numbers.Count == 0 && line.SerialId is { } serialId)
            {
                var named = await db.Serials.SingleOrDefaultAsync(s => s.Id == serialId, cancellationToken);
                if (named is null)
                {
                    return Error.NotFound("serial", serialId);
                }

                numbers.Add(named.SerialNumber);
            }

            if (numbers.Count != (int)quantity)
            {
                return Error.Validation("stock.serial_count", "A serialised item names one serial number per unit moved.").WithWhy(("item", item.Code), ("quantity", quantity), ("serialNumbers", numbers.Count));
            }

            if (numbers.Distinct(StringComparer.Ordinal).Count() != numbers.Count)
            {
                return Error.Validation("stock.serial_duplicate", "A serial number appears twice on the line.").WithWhy(("item", item.Code), ("serialNumbers", numbers));
            }

            var existing = await db.Serials.Where(s => s.ItemId == item.Id && numbers.Contains(s.SerialNumber)).ToDictionaryAsync(static s => s.SerialNumber, StringComparer.Ordinal, cancellationToken);
            foreach (var number in numbers)
            {
                var serial = existing.GetValueOrDefault(number);
                if (inbound)
                {
                    if (serial is null)
                    {
                        serial = new Serial { Id = Guid.CreateVersion7(), ItemId = item.Id, SerialNumber = number, LotId = lot?.Id, Status = SerialStatuses.InStock, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
                        db.Serials.Add(serial);
                    }
                    else if (SerialStatuses.IsOnHand(serial.Status) && line.EntryType != StockEntryTypes.TransferIn)
                    {
                        // A transfer's inbound half moves a serial that is on hand at the source; anything else receiving it again is a duplicate.
                        return Error.Conflict("stock.serial_in_stock", $"Serial {number} is already in stock.").WithWhy(("item", item.Code), ("serialNumber", number), ("status", serial.Status), ("warehouseId", serial.CurrentWarehouseId));
                    }
                    else if (lot is not null)
                    {
                        serial.LotId = lot.Id;
                    }
                }
                else
                {
                    if (serial is null)
                    {
                        return Error.Validation("stock.serial_unknown", $"Serial {number} does not exist for this item.").WithWhy(("item", item.Code), ("serialNumber", number));
                    }

                    if (!SerialStatuses.IsOnHand(serial.Status))
                    {
                        return Error.Conflict("stock.serial_not_on_hand", $"Serial {number} is {serial.Status}.").WithWhy(("item", item.Code), ("serialNumber", number), ("status", serial.Status));
                    }

                    if (serial.CurrentWarehouseId != warehouse.Id)
                    {
                        return Error.Conflict("stock.serial_not_in_warehouse", $"Serial {number} is not in warehouse {warehouse.Code}.").WithWhy(("item", item.Code), ("serialNumber", number), ("warehouse", warehouse.Code), ("currentWarehouseId", serial.CurrentWarehouseId));
                    }

                    if (serial.Status == SerialStatuses.InRepair && line.EntryType is StockEntryTypes.SaleShipment or StockEntryTypes.TransferOut)
                    {
                        return Error.Conflict("stock.serial_in_repair", $"Serial {number} is being repaired.").WithWhy(("item", item.Code), ("serialNumber", number));
                    }

                    if (lot is not null && serial.LotId is { } serialLot && serialLot != lot.Id)
                    {
                        return Error.Validation("stock.serial_lot_mismatch", $"Serial {number} belongs to another lot.").WithWhy(("item", item.Code), ("serialNumber", number), ("lotNumber", lot.LotNumber));
                    }

                    lot ??= serial.LotId is { } lid ? await db.Lots.SingleOrDefaultAsync(l => l.Id == lid, cancellationToken) : null;
                }

                serialIds.Add(serial.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new TrackedLine(lot?.Id, serialIds);
    }

    /// <summary>After the entries are written: serial statuses, positions and events; quality hold on the stock of blocked lots.</summary>
    public async Task AfterPostingAsync(IReadOnlyList<StockLedgerEntry> entries, IReadOnlyList<WarehouseInfo> warehousesOfEntries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(warehousesOfEntries);
        var actor = principal.Principal?.UserId.Value;
        var index = 0;
        foreach (var entry in entries)
        {
            var warehouse = warehousesOfEntries[index++];
            if (entry.SerialId is not { } serialId)
            {
                continue;
            }

            var serial = await db.Serials.SingleAsync(s => s.Id == serialId, cancellationToken);
            var from = serial.Status;
            string? to = entry.Quantity > 0m
                ? warehouse.Kind == "in_transit" ? SerialStatuses.InTransit : entry.EntryType == StockEntryTypes.SaleReturn ? SerialStatuses.Returned : SerialStatuses.InStock
                : entry.EntryType switch
                {
                    StockEntryTypes.SaleShipment => SerialStatuses.Sold,
                    StockEntryTypes.PurchaseReturn => SerialStatuses.ReturnedToSupplier,
                    StockEntryTypes.Scrap or StockEntryTypes.NegativeAdjustment or StockEntryTypes.CountVariance => SerialStatuses.Scrapped,
                    StockEntryTypes.AssemblyConsumption => SerialStatuses.Consumed,
                    StockEntryTypes.ConsignmentOut => SerialStatuses.Consigned,
                    _ => null,
                };
            if (entry.Quantity > 0m)
            {
                serial.CurrentWarehouseId = entry.WarehouseId;
                serial.CurrentBinId = entry.BinId;
                if (entry.PartnerId is not null)
                {
                    serial.CurrentPartnerId = entry.PartnerId;
                }
            }
            else if (to is not null)
            {
                serial.CurrentWarehouseId = null;
                serial.CurrentBinId = null;
                serial.CurrentPartnerId = entry.PartnerId ?? serial.CurrentPartnerId;
            }

            if (to is not null)
            {
                serial.Status = to;
            }

            serial.UpdatedAt = clock.UtcNow;
            db.SerialEvents.Add(new SerialEvent
            {
                Id = Guid.CreateVersion7(),
                SerialId = serial.Id,
                At = clock.UtcNow,
                PostingDate = entry.PostingDate,
                Kind = "movement",
                EntryType = entry.EntryType,
                SleId = entry.Id,
                FromStatus = from,
                ToStatus = serial.Status,
                WarehouseId = entry.WarehouseId,
                PartnerId = entry.PartnerId,
                SourceDocumentType = entry.SourceDocumentType,
                SourceDocumentId = entry.SourceDocumentId,
                ActorUserId = actor,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var lots = entries.Where(static e => e.LotId is not null).Select(static e => e.LotId!.Value).Distinct().ToList();
        if (lots.Count > 0)
        {
            var blocked = await db.Lots.Where(l => lots.Contains(l.Id) && (l.Status == LotStatuses.Quarantine || l.Status == LotStatuses.Recalled || l.Status == LotStatuses.Expired)).Select(static l => l.Id).ToListAsync(cancellationToken);
            foreach (var lot in blocked)
            {
                await HoldLotAsync(lot, hold: true, cancellationToken);
            }
        }
    }

    /// <summary>Puts a lot's stock on quality hold (or releases it) on every balance row of the lot.</summary>
    public async Task HoldLotAsync(Guid lotId, bool hold, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition(hold
            ? "UPDATE app.inv_stock_balances SET quality_hold = greatest(on_hand, 0), updated_at = now() WHERE tenant_id = @tenant AND lot_id = @lot"
            : "UPDATE app.inv_stock_balances SET quality_hold = 0, updated_at = now() WHERE tenant_id = @tenant AND lot_id = @lot", new { tenant = uow.Context.TenantId.Value, lot = lotId }, uow.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>First expiry, first out: the lots with available stock in the warehouse, earliest expiry first, and how much to take from each.</summary>
    public async Task<IReadOnlyList<FefoSuggestion>> SuggestAsync(Guid companyId, Guid itemId, Guid warehouseId, decimal quantity, DateOnly asOf, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<FefoRow>(new CommandDefinition("""
            SELECT l.id AS lot_id, l.lot_number, l.expires_on, sum(b.on_hand - b.reserved - b.quality_hold) AS available
            FROM app.inv_stock_balances b
            JOIN app.inv_lots l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
            WHERE b.company_id = @company AND b.item_id = @item AND b.warehouse_id = @warehouse AND l.status = 'active' AND (l.expires_on IS NULL OR l.expires_on >= @asOf)
            GROUP BY l.id, l.lot_number, l.expires_on
            HAVING sum(b.on_hand - b.reserved - b.quality_hold) > 0
            ORDER BY l.expires_on NULLS LAST, l.lot_number
            """, new { company = companyId, item = itemId, warehouse = warehouseId, asOf }, uow.Transaction, cancellationToken: cancellationToken));
        var remaining = quantity;
        var plan = new List<FefoSuggestion>();
        foreach (var row in rows)
        {
            var take = remaining <= 0m ? 0m : Math.Min(remaining, row.Available);
            remaining -= take;
            plan.Add(new FefoSuggestion(row.LotId, row.LotNumber, row.ExpiresOn, ItemUomMath.Normalize(row.Available), ItemUomMath.Normalize(take)));
        }

        return plan;
    }

    private sealed class FefoRow
    {
        public Guid LotId { get; set; }

        public string LotNumber { get; set; } = string.Empty;

        public DateOnly? ExpiresOn { get; set; }

        public decimal Available { get; set; }
    }
}

public sealed record FefoSuggestion(Guid LotId, string LotNumber, DateOnly? ExpiresOn, decimal Available, decimal Take);

// ------------------------------------------------------------------ lots

public sealed record SaveLotRequest(Guid ItemId, string LotNumber, DateOnly? ManufacturedOn = null, DateOnly? ExpiresOn = null, string? SupplierLot = null, Guid? SupplierPartnerId = null, JsonElement? CustomFields = null);

public sealed record LotStatusRequest(string Status, string? Reason = null, string? RecallReference = null);

public sealed record LotStatusResult(LotInfo Lot, int ReservationsReleased, LotTrace? Impact);

public sealed record LotTraceMovement(Guid SleId, DateOnly PostingDate, string EntryType, decimal Quantity, Guid WarehouseId, string WarehouseCode, string SourceDocumentType, Guid SourceDocumentId, Guid? PartnerId, Guid? SerialId, string? SerialNumber);

public sealed record LotTraceBalance(Guid WarehouseId, string WarehouseCode, decimal OnHand, decimal Reserved, decimal QualityHold);

public sealed record LotTracePartner(Guid PartnerId, decimal Quantity, int Shipments, DateOnly LastShippedOn);

/// <summary>Where a lot came from, where it went, where it is, and who received it (hard scenario 12).</summary>
public sealed record LotTrace(LotInfo Lot, IReadOnlyList<LotTraceMovement> Inbound, IReadOnlyList<LotTraceMovement> Outbound, IReadOnlyList<LotTraceBalance> OnHand, IReadOnlyList<LotTracePartner> ShippedTo, IReadOnlyList<SerialInfo> Serials);

public sealed class LotService(InventoryDbContext db, IUnitOfWorkAccessor unitOfWork, TrackingResolver tracking, ReservationService reservations, IItemDirectory items, IWarehouseDirectory warehouses, ICustomFieldValidator customFields, IAuditSink audit, IClock clock)
{
    public async Task<IReadOnlyList<LotInfo>> ListAsync(Guid? itemId, string? status, DateOnly? expiringBefore, string? q, CancellationToken cancellationToken)
    {
        var query = db.Lots.AsQueryable();
        if (itemId is { } i)
        {
            query = query.Where(l => l.ItemId == i);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(l => l.Status == s);
        }

        if (expiringBefore is { } before)
        {
            query = query.Where(l => l.ExpiresOn != null && l.ExpiresOn <= before);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            query = query.Where(l => EF.Functions.ILike(l.LotNumber, pattern) || (l.SupplierLot != null && EF.Functions.ILike(l.SupplierLot, pattern)));
        }

        var result = new List<LotInfo>();
        foreach (var lot in await query.OrderBy(static l => l.ExpiresOn).ThenBy(static l => l.LotNumber).Take(500).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(lot, cancellationToken));
        }

        return result;
    }

    public async Task<LotInfo?> GetAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await db.Lots.SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken);
        return lot is null ? null : await MapAsync(lot, cancellationToken);
    }

    public async Task<Result<LotInfo>> CreateAsync(SaveLotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await items.FindAsync(request.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", request.ItemId);
        }

        if (item.Tracking is not ("lot" or "lot_and_serial"))
        {
            return Error.Validation("lot.item_not_lot_tracked", "The item is not lot-tracked.").WithWhy(("item", item.Code), ("tracking", item.Tracking));
        }

        var number = request.LotNumber?.Trim();
        if (string.IsNullOrEmpty(number))
        {
            return Error.Validation("lot.number_required", "A lot has a number.");
        }

        if (await db.Lots.AnyAsync(l => l.ItemId == item.Id && l.LotNumber == number, cancellationToken))
        {
            return Error.Conflict("lot.number_taken", "The item already has a lot with this number.").WithWhy(("item", item.Code), ("lotNumber", number));
        }

        if (item.ExpiryRequired && request.ExpiresOn is null)
        {
            return Error.Validation("lot.expiry_required", "The item requires an expiry date on every lot.").WithWhy(("item", item.Code));
        }

        var validated = await customFields.ValidateAsync("lot", request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var lot = new Lot
        {
            Id = Guid.CreateVersion7(),
            ItemId = item.Id,
            LotNumber = number,
            ManufacturedOn = request.ManufacturedOn,
            ExpiresOn = request.ExpiresOn,
            SupplierLot = string.IsNullOrWhiteSpace(request.SupplierLot) ? null : request.SupplierLot.Trim(),
            SupplierPartnerId = request.SupplierPartnerId,
            CustomFields = validated.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        db.Lots.Add(lot);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(lot, cancellationToken);
    }

    public async Task<Result<LotInfo>> UpdateAsync(Guid lotId, SaveLotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lot = await db.Lots.SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken);
        if (lot is null)
        {
            return Error.NotFound("lot", lotId);
        }

        var item = (await items.FindAsync(lot.ItemId, cancellationToken))!;
        if (item.ExpiryRequired && request.ExpiresOn is null)
        {
            return Error.Validation("lot.expiry_required", "The item requires an expiry date on every lot.").WithWhy(("item", item.Code));
        }

        var validated = await customFields.ValidateAsync("lot", request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        lot.ManufacturedOn = request.ManufacturedOn;
        lot.ExpiresOn = request.ExpiresOn;
        lot.SupplierLot = string.IsNullOrWhiteSpace(request.SupplierLot) ? null : request.SupplierLot.Trim();
        lot.SupplierPartnerId = request.SupplierPartnerId;
        lot.CustomFields = validated.Value;
        lot.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(lot, cancellationToken);
    }

    /// <summary>
    /// Active, quarantine, recalled or expired. Quarantine, recall and expiry put the lot's stock on hold everywhere
    /// and release its open reservations; a recall names its reference and answers with the impact: what is on hand
    /// where, and which customers received the lot (hard scenario 12).
    /// </summary>
    public async Task<Result<LotStatusResult>> SetStatusAsync(Guid lotId, LotStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lot = await db.Lots.SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken);
        if (lot is null)
        {
            return Error.NotFound("lot", lotId);
        }

        var status = Validation.OneOf(request.Status, "lot.status", LotStatuses.All);
        if (status.IsFailure)
        {
            return status.Error!;
        }

        if (status.Value == LotStatuses.Recalled && string.IsNullOrWhiteSpace(request.RecallReference))
        {
            return Error.Validation("lot.recall_reference_required", "A recall names its reference (the notice, the supplier's bulletin).");
        }

        if (status.Value == LotStatuses.Consumed)
        {
            return Error.Validation("lot.status_derived", "A lot becomes consumed when its stock is gone; it is not set by hand.");
        }

        var previous = lot.Status;
        lot.Status = status.Value;
        lot.StatusReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        lot.RecallReference = status.Value == LotStatuses.Recalled ? request.RecallReference!.Trim() : lot.RecallReference;
        lot.StatusChangedAt = clock.UtcNow;
        lot.UpdatedAt = clock.UtcNow;
        var released = 0;
        if (LotStatuses.Blocks(status.Value))
        {
            await tracking.HoldLotAsync(lot.Id, hold: true, cancellationToken);
            foreach (var reservation in await db.Reservations.Where(r => r.LotId == lot.Id && r.Status == "active").Select(static r => r.Id).ToListAsync(cancellationToken))
            {
                var release = await reservations.ReleaseAsync(reservation, $"lot {lot.LotNumber} {status.Value}", cancellationToken);
                if (release.IsSuccess)
                {
                    released++;
                }
            }
        }
        else
        {
            await tracking.HoldLotAsync(lot.Id, hold: false, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("lot", lot.Id, lot.LotNumber, AuditActions.StateChanged, Before: new { status = previous }, After: new { status = lot.Status, reason = lot.StatusReason, recallReference = lot.RecallReference, reservationsReleased = released }), cancellationToken);
        var info = await MapAsync(lot, cancellationToken);
        return new LotStatusResult(info, released, status.Value == LotStatuses.Recalled ? await TraceAsync(lot.Id, cancellationToken) : null);
    }

    public async Task<LotTrace?> TraceAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await db.Lots.SingleOrDefaultAsync(l => l.Id == lotId, cancellationToken);
        if (lot is null)
        {
            return null;
        }

        var uow = unitOfWork.Current;
        var movements = (await uow.Connection.QueryAsync<MovementRow>(new CommandDefinition("""
            SELECT e.id AS sle_id, e.posting_date, e.entry_type, e.quantity, e.warehouse_id, w.code AS warehouse_code, e.source_document_type, e.source_document_id, e.partner_id, e.serial_id, s.serial_number
            FROM app.inv_stock_ledger_entries e
            JOIN app.inv_warehouses w ON w.tenant_id = e.tenant_id AND w.id = e.warehouse_id
            LEFT JOIN app.inv_serials s ON s.tenant_id = e.tenant_id AND s.id = e.serial_id
            WHERE e.lot_id = @lot
            ORDER BY e.posting_date, e.sequence
            """, new { lot = lotId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var balances = (await uow.Connection.QueryAsync<BalanceRow>(new CommandDefinition("""
            SELECT b.warehouse_id, w.code AS warehouse_code, sum(b.on_hand) AS on_hand, sum(b.reserved) AS reserved, sum(b.quality_hold) AS quality_hold
            FROM app.inv_stock_balances b JOIN app.inv_warehouses w ON w.tenant_id = b.tenant_id AND w.id = b.warehouse_id
            WHERE b.lot_id = @lot
            GROUP BY b.warehouse_id, w.code
            HAVING sum(b.on_hand) <> 0 OR sum(b.reserved) <> 0
            ORDER BY w.code
            """, new { lot = lotId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var inbound = movements.Where(static m => m.Quantity > 0m).Select(Map).ToList();
        var outbound = movements.Where(static m => m.Quantity < 0m).Select(Map).ToList();
        var partners = movements.Where(static m => m.Quantity < 0m && m.EntryType == StockEntryTypes.SaleShipment && m.PartnerId is not null)
            .GroupBy(static m => m.PartnerId!.Value)
            .Select(static g => new LotTracePartner(g.Key, ItemUomMath.Normalize(-g.Sum(static m => m.Quantity)), g.Count(), g.Max(static m => m.PostingDate)))
            .OrderByDescending(static p => p.Quantity).ToList();
        var serials = new List<SerialInfo>();
        foreach (var serial in await db.Serials.Where(s => s.LotId == lotId).OrderBy(static s => s.SerialNumber).ToListAsync(cancellationToken))
        {
            serials.Add(await SerialService.MapAsync(serial, items, warehouses, lot.LotNumber, cancellationToken));
        }

        return new LotTrace(await MapAsync(lot, cancellationToken), inbound, outbound, balances.Select(static b => new LotTraceBalance(b.WarehouseId, b.WarehouseCode, ItemUomMath.Normalize(b.OnHand), ItemUomMath.Normalize(b.Reserved), ItemUomMath.Normalize(b.QualityHold))).ToList(), partners, serials);
    }

    public Task<IReadOnlyList<FefoSuggestion>> SuggestAsync(Guid companyId, Guid itemId, Guid warehouseId, decimal quantity, DateOnly? asOf, CancellationToken cancellationToken) =>
        tracking.SuggestAsync(companyId, itemId, warehouseId, quantity, asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), cancellationToken);

    /// <summary>Lots past their expiry date become expired and their stock goes on hold; returns how many.</summary>
    public async Task<int> ExpireAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var expired = await db.Lots.Where(l => l.Status == LotStatuses.Active && l.ExpiresOn != null && l.ExpiresOn < today).ToListAsync(cancellationToken);
        foreach (var lot in expired)
        {
            lot.Status = LotStatuses.Expired;
            lot.StatusReason = "expired on " + lot.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            lot.StatusChangedAt = clock.UtcNow;
            lot.UpdatedAt = clock.UtcNow;
            await tracking.HoldLotAsync(lot.Id, hold: true, cancellationToken);
            foreach (var reservation in await db.Reservations.Where(r => r.LotId == lot.Id && r.Status == "active").Select(static r => r.Id).ToListAsync(cancellationToken))
            {
                await reservations.ReleaseAsync(reservation, $"lot {lot.LotNumber} expired", cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private async Task<LotInfo> MapAsync(Lot lot, CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(lot.ItemId, cancellationToken);
        return new LotInfo(lot.Id, lot.ItemId, item?.Code ?? string.Empty, lot.LotNumber, lot.ManufacturedOn, lot.ExpiresOn, lot.SupplierLot, lot.SupplierPartnerId, lot.Status, lot.StatusReason, lot.RecallReference, lot.StatusChangedAt, JsonDocument.Parse(lot.CustomFields).RootElement.Clone(), lot.UpdatedAt);
    }

    private static LotTraceMovement Map(MovementRow m) => new(m.SleId, m.PostingDate, m.EntryType, ItemUomMath.Normalize(m.Quantity), m.WarehouseId, m.WarehouseCode, m.SourceDocumentType, m.SourceDocumentId, m.PartnerId, m.SerialId, m.SerialNumber);

    private sealed class MovementRow
    {
        public Guid SleId { get; set; }

        public DateOnly PostingDate { get; set; }

        public string EntryType { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public string SourceDocumentType { get; set; } = string.Empty;

        public Guid SourceDocumentId { get; set; }

        public Guid? PartnerId { get; set; }

        public Guid? SerialId { get; set; }

        public string? SerialNumber { get; set; }
    }

    private sealed class BalanceRow
    {
        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal QualityHold { get; set; }
    }
}

// ------------------------------------------------------------------ serials

public sealed record SerialStatusRequest(string Status, string? Note = null);

public sealed record SerialHistoryEvent(Guid Id, DateTimeOffset At, DateOnly? PostingDate, string Kind, string? EntryType, Guid? SleId, string? FromStatus, string ToStatus, Guid? WarehouseId, string? WarehouseCode, Guid? PartnerId, string? SourceDocumentType, Guid? SourceDocumentId, decimal? Quantity, decimal? CostAmount, string? Note, Guid? ActorUserId);

/// <summary>The serial and everything that happened to it, oldest first (hard scenario 13: one screen, one query).</summary>
public sealed record SerialHistory(SerialInfo Serial, IReadOnlyList<SerialHistoryEvent> Events);

public sealed class SerialService(InventoryDbContext db, IUnitOfWorkAccessor unitOfWork, IItemDirectory items, IWarehouseDirectory warehouses, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public async Task<IReadOnlyList<SerialInfo>> ListAsync(Guid? itemId, string? status, Guid? warehouseId, Guid? lotId, string? q, CancellationToken cancellationToken)
    {
        var query = db.Serials.AsQueryable();
        if (itemId is { } i)
        {
            query = query.Where(s => s.ItemId == i);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var st = status.Trim().ToLowerInvariant();
            query = query.Where(s => s.Status == st);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(s => s.CurrentWarehouseId == w);
        }

        if (lotId is { } l)
        {
            query = query.Where(s => s.LotId == l);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            query = query.Where(s => EF.Functions.ILike(s.SerialNumber, pattern));
        }

        var result = new List<SerialInfo>();
        foreach (var serial in await query.OrderBy(static s => s.SerialNumber).Take(500).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(serial, items, warehouses, null, cancellationToken));
        }

        return result;
    }

    public async Task<SerialInfo?> GetAsync(Guid serialId, CancellationToken cancellationToken)
    {
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.Id == serialId, cancellationToken);
        return serial is null ? null : await MapAsync(serial, items, warehouses, null, cancellationToken);
    }

    public async Task<SerialInfo?> FindAsync(Guid itemId, string serialNumber, CancellationToken cancellationToken)
    {
        var number = serialNumber.Trim();
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.ItemId == itemId && s.SerialNumber == number, cancellationToken);
        return serial is null ? null : await MapAsync(serial, items, warehouses, null, cancellationToken);
    }

    /// <summary>A unit on hand goes into repair and comes back; while in repair its stock is on hold and it cannot be shipped.</summary>
    public async Task<Result<SerialInfo>> SetStatusAsync(Guid serialId, SerialStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.Id == serialId, cancellationToken);
        if (serial is null)
        {
            return Error.NotFound("serial", serialId);
        }

        var status = Validation.OneOf(request.Status, "serial.status", [SerialStatuses.InRepair, SerialStatuses.InStock]);
        if (status.IsFailure)
        {
            return status.Error!;
        }

        var allowed = (serial.Status, status.Value) switch
        {
            (SerialStatuses.Returned or SerialStatuses.InStock, SerialStatuses.InRepair) => true,
            (SerialStatuses.InRepair or SerialStatuses.Returned, SerialStatuses.InStock) => true,
            _ => false,
        };
        if (!allowed)
        {
            return Error.Conflict("serial.transition_invalid", $"A serial goes from {serial.Status} to {status.Value} only through a stock movement.").WithWhy(("serialNumber", serial.SerialNumber), ("from", serial.Status), ("to", status.Value));
        }

        var previous = serial.Status;
        serial.Status = status.Value;
        serial.UpdatedAt = clock.UtcNow;
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition(status.Value == SerialStatuses.InRepair
            ? "UPDATE app.inv_stock_balances SET quality_hold = greatest(on_hand, 0), updated_at = now() WHERE tenant_id = @tenant AND serial_id = @serial"
            : "UPDATE app.inv_stock_balances SET quality_hold = 0, updated_at = now() WHERE tenant_id = @tenant AND serial_id = @serial", new { tenant = uow.Context.TenantId.Value, serial = serial.Id }, uow.Transaction, cancellationToken: cancellationToken));
        db.SerialEvents.Add(new SerialEvent
        {
            Id = Guid.CreateVersion7(),
            SerialId = serial.Id,
            At = clock.UtcNow,
            Kind = "status",
            FromStatus = previous,
            ToStatus = serial.Status,
            WarehouseId = serial.CurrentWarehouseId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            ActorUserId = principal.Principal?.UserId.Value,
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("serial", serial.Id, serial.SerialNumber, AuditActions.StateChanged, Before: new { status = previous }, After: new { status = serial.Status, note = request.Note }), cancellationToken);
        return await MapAsync(serial, items, warehouses, null, cancellationToken);
    }

    public async Task<SerialHistory?> HistoryAsync(Guid serialId, CancellationToken cancellationToken)
    {
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.Id == serialId, cancellationToken);
        if (serial is null)
        {
            return null;
        }

        var uow = unitOfWork.Current;

        // In the order the events were written: one posting can write several in the same instant (a transfer ships
        // out of one warehouse into transit), and ids made in the same millisecond are not ordered.
        var rows = await uow.Connection.QueryAsync<HistoryRow>(new CommandDefinition("""
            SELECT ev.id, ev.at, ev.posting_date, ev.kind, ev.entry_type, ev.sle_id, ev.from_status, ev.to_status, ev.warehouse_id, w.code AS warehouse_code, ev.partner_id, ev.source_document_type, ev.source_document_id, ev.note, ev.actor_user_id,
                   e.quantity, (SELECT sum(v.cost_amount_actual + v.cost_amount_expected) FROM app.inv_stock_value_entries v WHERE v.tenant_id = ev.tenant_id AND v.sle_id = ev.sle_id AND v.account_role IN ('Inventory', 'InventoryInTransit')) AS cost_amount
            FROM app.inv_serial_events ev
            LEFT JOIN app.inv_warehouses w ON w.tenant_id = ev.tenant_id AND w.id = ev.warehouse_id
            LEFT JOIN app.inv_stock_ledger_entries e ON e.tenant_id = ev.tenant_id AND e.id = ev.sle_id
            WHERE ev.serial_id = @serial
            ORDER BY ev.seq
            """, new { serial = serialId }, uow.Transaction, cancellationToken: cancellationToken));
        var events = rows.Select(static r => new SerialHistoryEvent(r.Id, r.At, r.PostingDate, r.Kind, r.EntryType, r.SleId, r.FromStatus, r.ToStatus, r.WarehouseId, r.WarehouseCode, r.PartnerId, r.SourceDocumentType, r.SourceDocumentId,
            r.Quantity is { } q ? ItemUomMath.Normalize(q) : null, r.CostAmount, r.Note, r.ActorUserId)).ToList();
        return new SerialHistory(await MapAsync(serial, items, warehouses, null, cancellationToken), events);
    }

    internal static async Task<SerialInfo> MapAsync(Serial serial, IItemDirectory items, IWarehouseDirectory warehouses, string? lotNumber, CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(serial.ItemId, cancellationToken);
        var warehouse = serial.CurrentWarehouseId is { } w ? await warehouses.FindAsync(w, cancellationToken) : null;
        return new SerialInfo(serial.Id, serial.ItemId, item?.Code ?? string.Empty, serial.SerialNumber, serial.LotId, lotNumber, serial.Status, serial.CurrentWarehouseId, warehouse?.Code, serial.CurrentBinId, serial.CurrentPartnerId, serial.WarrantyUntil, JsonDocument.Parse(serial.CustomFields).RootElement.Clone(), serial.UpdatedAt);
    }

    private sealed class HistoryRow
    {
        public Guid Id { get; set; }

        public DateTimeOffset At { get; set; }

        public DateOnly? PostingDate { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string? EntryType { get; set; }

        public Guid? SleId { get; set; }

        public string? FromStatus { get; set; }

        public string ToStatus { get; set; } = string.Empty;

        public Guid? WarehouseId { get; set; }

        public string? WarehouseCode { get; set; }

        public Guid? PartnerId { get; set; }

        public string? SourceDocumentType { get; set; }

        public Guid? SourceDocumentId { get; set; }

        public string? Note { get; set; }

        public Guid? ActorUserId { get; set; }

        public decimal? Quantity { get; set; }

        public decimal? CostAmount { get; set; }
    }
}

public sealed record LotExpiryPayload(DateOnly? AsOf = null);

/// <summary>Marks lots past their expiry date as expired, across tenants when run by the platform schedule.</summary>
public sealed class LotExpiryJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, LotService lots, IClock clock) : IJobHandler<LotExpiryPayload>
{
    public static string JobType => "inventory.lots.expire";

    public async Task<object?> ExecuteAsync(LotExpiryPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var today = payload?.AsOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        if (context.TenantId is not null)
        {
            return new { expired = await lots.ExpireAsync(today, cancellationToken) };
        }

        var expired = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            expired += await InTenantAsync(tenant.Id.Value, (sp, ct) => sp.GetRequiredService<LotService>().ExpireAsync(today, ct), cancellationToken);
        }

        return new { tenants = tenants.Count, expired };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new Kernel.Ids.TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

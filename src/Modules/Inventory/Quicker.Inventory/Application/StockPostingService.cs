using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging.Outbox;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Inventory.Application;

/// <summary>
/// The stock ledger engine, quantity side (ADR-0008). Every document movement comes through here: the lines are
/// validated (company, inventory period and posting window, warehouse and bin, item and variant, unit converted
/// exactly to the base unit), the balance rows they touch are locked in a fixed order, availability is checked under
/// the lock against the negative-stock policy (item setting, then warehouse, then company), reservations are
/// consumed, and the entries, balances, audit event and integration event are written in the caller's unit of work.
/// Two users taking the last unit at the same moment serialise on the row lock and exactly one succeeds (scenario 4).
/// </summary>
public sealed class StockPostingService(
    InventoryDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IFiscalPeriodResolver periods,
    IPostingWindows windows,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IOutbox outbox,
    IClock clock) : IInventoryPosting
{
    private sealed record BalanceKey(Guid CompanyId, Guid ItemId, Guid VariantId, Guid WarehouseId, Guid BinId, Guid LotId, Guid SerialId)
    {
        /// <summary>A fixed lock order across postings, so two of them touching the same rows never deadlock.</summary>
        public string LockOrder => $"{CompanyId:N}{ItemId:N}{VariantId:N}{WarehouseId:N}{BinId:N}{LotId:N}{SerialId:N}";
    }

    private sealed record ResolvedLine(StockLine Source, ItemInfo Item, WarehouseInfo Warehouse, decimal BaseQuantity, Guid EnteredUomId, string EnteredUomCode, Reservation? Reservation, BalanceKey Key);

    private sealed record LockedBalance(decimal OnHand, decimal Reserved, decimal QualityHold);

    public async Task<Result<StockPostingResult>> PostAsync(StockPostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("stock.lines_required", "A stock posting has at least one line.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceDocumentType) || request.SourceDocumentId == Guid.Empty)
        {
            return Error.Validation("stock.source_required", "A stock posting names its source document.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (key is not null)
        {
            var existing = await db.Postings.Include(static p => p.Entries).SingleOrDefaultAsync(p => p.IdempotencyKey == key, cancellationToken);
            if (existing is not null)
            {
                return await ToResultAsync(existing, replayed: true, cancellationToken);
            }
        }

        var period = await PeriodForAsync(company, request.PostingDate, cancellationToken);
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var resolved = new List<ResolvedLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var result = await ResolveAsync(company, line, cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            resolved.Add(result.Value);
        }

        // Reservations consumed by this posting, so the availability check counts them as ours.
        var consumedByKey = resolved.Where(static l => l.Reservation is not null).GroupBy(static l => l.Key).ToDictionary(static g => g.Key, static g => g.Sum(static l => -l.BaseQuantity));
        var deltas = resolved.GroupBy(static l => l.Key).ToDictionary(static g => g.Key, static g => g.Sum(static l => l.BaseQuantity));
        var now = clock.UtcNow;
        var uow = unitOfWork.Current;
        foreach (var balanceKey in deltas.Keys.OrderBy(static k => k.LockOrder, StringComparer.Ordinal))
        {
            var delta = deltas[balanceKey];
            var locked = await LockAsync(uow, balanceKey, cancellationToken);
            var consumed = consumedByKey.GetValueOrDefault(balanceKey);
            var availableForThis = locked.OnHand - locked.Reserved - locked.QualityHold + consumed;
            if (delta < 0m && availableForThis + delta < 0m)
            {
                var sample = resolved.First(l => l.Key == balanceKey);
                var allowNegative = await AllowsNegativeAsync(company, sample.Item, sample.Warehouse, cancellationToken);
                if (!allowNegative)
                {
                    return Error.Conflict("stock.insufficient", $"Not enough {sample.Item.Code} in {sample.Warehouse.Code}: {N(availableForThis)} {sample.Item.BaseUomCode} available, {N(-delta)} requested.")
                        .WithWhy(("item", sample.Item.Code), ("warehouse", sample.Warehouse.Code), ("onHand", N(locked.OnHand)), ("reserved", N(locked.Reserved)), ("qualityHold", N(locked.QualityHold)),
                            ("available", N(availableForThis)), ("requested", N(-delta)), ("baseUom", sample.Item.BaseUomCode), ("negativeStockAllowed", allowNegative));
                }
            }

            await uow.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE app.inv_stock_balances
                SET on_hand = on_hand + @delta, reserved = reserved - @consumed, last_movement_at = @now, updated_at = now()
                WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND variant_id = @variant AND warehouse_id = @warehouse AND bin_id = @bin AND lot_id = @lot AND serial_id = @serial
                """, new
            {
                delta,
                consumed,
                now,
                tenant = uow.Context.TenantId.Value,
                company = balanceKey.CompanyId,
                item = balanceKey.ItemId,
                variant = balanceKey.VariantId,
                warehouse = balanceKey.WarehouseId,
                bin = balanceKey.BinId,
                lot = balanceKey.LotId,
                serial = balanceKey.SerialId,
            }, uow.Transaction, cancellationToken: cancellationToken));
        }

        var posting = new StockPosting
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            PostingDate = request.PostingDate,
            FiscalPeriodId = period.Value.Period.PeriodId,
            SourceDocumentType = request.SourceDocumentType.Trim(),
            SourceDocumentId = request.SourceDocumentId,
            EntryCount = resolved.Count,
            IdempotencyKey = key,
            PostedBy = principal.Principal?.UserId.Value,
            PostedAt = now,
        };
        foreach (var line in resolved)
        {
            posting.Entries.Add(new StockLedgerEntry
            {
                Id = Guid.CreateVersion7(),
                PostingId = posting.Id,
                CompanyId = posting.CompanyId,
                ItemId = line.Item.Id,
                VariantId = line.Source.VariantId,
                WarehouseId = line.Warehouse.Id,
                BinId = line.Source.BinId,
                LotId = line.Source.LotId,
                SerialId = line.Source.SerialId,
                EntryType = line.Source.EntryType,
                Quantity = line.BaseQuantity,
                EnteredUomId = line.EnteredUomId,
                EnteredQuantity = line.Source.Quantity,
                PostingDate = posting.PostingDate,
                FiscalPeriodId = posting.FiscalPeriodId,
                SourceDocumentType = posting.SourceDocumentType,
                SourceDocumentId = posting.SourceDocumentId,
                SourceLineId = line.Source.SourceLineId,
                Ownership = line.Source.Ownership,
                OwnerPartnerId = line.Source.OwnerPartnerId,
                RemainingQuantity = line.BaseQuantity > 0m ? line.BaseQuantity : 0m,
                TransferPairId = line.Source.TransferPairId,
                ReservationId = line.Reservation?.Id,
                PostedBy = posting.PostedBy,
                PostedAt = now,
            });

            if (line.Reservation is { } reservation)
            {
                reservation.ConsumedQuantity += -line.BaseQuantity;
                if (reservation.ConsumedQuantity >= reservation.Quantity)
                {
                    reservation.Status = "consumed";
                    reservation.ClosedAt = now;
                }
            }
        }

        db.Postings.Add(posting);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("stock_posting", posting.Id, $"{posting.SourceDocumentType}/{posting.SourceDocumentId:N}", AuditActions.Posted, After: new
        {
            company = company.Code,
            postingDate = posting.PostingDate,
            source = new { type = posting.SourceDocumentType, id = posting.SourceDocumentId },
            entries = resolved.Select(static l => new { item = l.Item.Code, warehouse = l.Warehouse.Code, type = l.Source.EntryType, quantity = l.BaseQuantity, uom = l.Item.BaseUomCode }),
        }, CompanyId: company.Id.Value), cancellationToken);
        await outbox.PublishAsync(new StockPosted(posting.Id, posting.CompanyId, posting.PostingDate, posting.SourceDocumentType, posting.SourceDocumentId, posting.EntryCount), cancellationToken);
        return await ToResultAsync(posting, replayed: false, cancellationToken);
    }

    // ------------------------------------------------------------------ resolution

    private async Task<Result<ResolvedLine>> ResolveAsync(CompanyInfo company, StockLine line, CancellationToken cancellationToken)
    {
        if (!StockEntryTypes.All.Contains(line.EntryType, StringComparer.Ordinal))
        {
            return Error.Validation("stock.entry_type_invalid", "Unknown stock entry type.").WithWhy(("entryType", line.EntryType), ("allowed", StockEntryTypes.All));
        }

        var sign = StockEntryTypes.SignOf(line.EntryType);
        if (line.Quantity == 0m || (sign != 0 && line.Quantity < 0m))
        {
            return Error.Validation("stock.quantity_invalid", "Quantities are positive; the entry type gives the direction.").WithWhy(("entryType", line.EntryType), ("quantity", line.Quantity));
        }

        var item = await items.FindAsync(line.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", line.ItemId);
        }

        if (!item.IsActive)
        {
            return Error.Conflict("stock.item_inactive", "The item is inactive.").WithWhy(("item", item.Code));
        }

        if (item.Type is not ("stock" or "assembly"))
        {
            return Error.Validation("stock.item_not_stocked", "Only stock and assembly items hold stock; kits, non-stock and service items do not.").WithWhy(("item", item.Code), ("type", item.Type));
        }

        if (line.VariantId is { } variantId)
        {
            var variant = await items.FindVariantAsync(variantId, cancellationToken);
            if (variant is null || variant.ItemId != item.Id)
            {
                return Error.Validation("stock.variant_invalid", "The variant must belong to the item.").WithWhy(("item", item.Code), ("variantId", variantId));
            }
        }
        else if (item.HasVariants)
        {
            return Error.Validation("stock.variant_required", "The item has variants; name the one that moves.").WithWhy(("item", item.Code));
        }

        var warehouse = await warehouses.FindAsync(line.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value)
        {
            return Error.Validation("stock.warehouse_invalid", "The warehouse must belong to the posting's company.").WithWhy(("warehouseId", line.WarehouseId), ("company", company.Code));
        }

        if (!warehouse.IsActive)
        {
            return Error.Conflict("stock.warehouse_inactive", "The warehouse is inactive.").WithWhy(("warehouse", warehouse.Code));
        }

        if (warehouse.BinsEnabled)
        {
            if (line.BinId is not { } binId)
            {
                return Error.Validation("stock.bin_required", "The warehouse uses bins; name the bin.").WithWhy(("warehouse", warehouse.Code));
            }

            var bin = await warehouses.FindBinAsync(binId, cancellationToken);
            if (bin is null || bin.WarehouseId != warehouse.Id)
            {
                return Error.Validation("stock.bin_invalid", "The bin must belong to the warehouse.").WithWhy(("warehouse", warehouse.Code), ("binId", binId));
            }

            if (!bin.IsActive)
            {
                return Error.Conflict("stock.bin_inactive", "The bin is inactive.").WithWhy(("bin", bin.Code));
            }
        }
        else if (line.BinId is not null)
        {
            return Error.Validation("stock.bin_not_used", "The warehouse does not use bins.").WithWhy(("warehouse", warehouse.Code));
        }

        var uomId = line.UomId ?? item.BaseUomId;
        var converted = await items.ToBaseAsync(item.Id, uomId, Math.Abs(line.Quantity), cancellationToken);
        if (converted.IsFailure)
        {
            return converted.Error!;
        }

        var baseQuantity = sign == 0 ? (line.Quantity < 0m ? -converted.Value.Quantity : converted.Value.Quantity) : sign * converted.Value.Quantity;
        Reservation? reservation = null;
        if (line.ReservationId is { } reservationId)
        {
            reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
            if (reservation is null || reservation.Status != "active")
            {
                return Error.Conflict("stock.reservation_invalid", "The reservation does not exist or is no longer active.").WithWhy(("reservationId", reservationId));
            }

            if (baseQuantity >= 0m)
            {
                return Error.Validation("stock.reservation_on_inbound", "Only an outbound movement consumes a reservation.").WithWhy(("reservationId", reservationId));
            }

            if (reservation.CompanyId != company.Id.Value || reservation.ItemId != item.Id || reservation.WarehouseId != warehouse.Id || reservation.VariantId != line.VariantId
                || (reservation.BinId is not null && reservation.BinId != line.BinId) || (reservation.LotId is not null && reservation.LotId != line.LotId) || (reservation.SerialId is not null && reservation.SerialId != line.SerialId))
            {
                return Error.Conflict("stock.reservation_mismatch", "The reservation is for other stock than the line moves.").WithWhy(("reservationId", reservationId), ("item", item.Code), ("warehouse", warehouse.Code));
            }

            if (reservation.Quantity - reservation.ConsumedQuantity < -baseQuantity)
            {
                return Error.Conflict("stock.reservation_exceeded", "The line takes more than the reservation still holds.").WithWhy(("reservationId", reservationId), ("remaining", N(reservation.Quantity - reservation.ConsumedQuantity)), ("requested", N(-baseQuantity)));
            }
        }

        var key = new BalanceKey(company.Id.Value, item.Id, line.VariantId ?? Guid.Empty, warehouse.Id, line.BinId ?? Guid.Empty, line.LotId ?? Guid.Empty, line.SerialId ?? Guid.Empty);
        return new ResolvedLine(line, item, warehouse, baseQuantity, converted.Value.EnteredUomId, converted.Value.EnteredUomCode, reservation, key);
    }

    private async Task<Result<PeriodState>> PeriodForAsync(CompanyInfo company, DateOnly date, CancellationToken cancellationToken)
    {
        var resolved = await periods.ResolveAsync(company.Id, date, PostingModules.Inventory, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var state = resolved.Value;
        var mayPostInSoftClosed = principal.Principal?.Has(InventoryPermissions.PostInSoftClosed) ?? true;
        if (state.AllowsPosting(mayPostInSoftClosed))
        {
            if (principal.Principal is { } actor)
            {
                var window = await windows.EffectiveAsync(company.Id, actor.Roles, cancellationToken);
                if (window is not null && !window.Allows(date))
                {
                    return Error.Conflict("posting.outside_window", $"Posting dated {date:yyyy-MM-dd} is outside the allowed window of company {company.Code}.")
                        .WithWhy(("company", company.Code), ("date", date), ("allowFrom", window.AllowFrom), ("allowTo", window.AllowTo), ("roleId", window.RoleId), ("requiredPermission", "accounting.period.manage"));
                }
            }

            return state;
        }

        var code = state.State == PeriodStates.SoftClosed ? "period.soft_closed" : "period.closed";
        return Error.Conflict(code, $"Period {state.Period.Number} of {state.Period.FiscalYearCode} is {state.State} for inventory in company {company.Code}.")
            .WithWhy(("company", company.Code), ("date", date), ("periodId", state.Period.PeriodId), ("period", state.Period.Number), ("fiscalYear", state.Period.FiscalYearCode), ("state", state.State), ("module", PostingModules.Inventory),
                ("requiredPermission", state.State == PeriodStates.SoftClosed ? InventoryPermissions.PostInSoftClosed : "accounting.period.reopen"));
    }

    /// <summary>Item setting for the company, else the warehouse, else the company policy ("approve" counts as blocked until the approval workflow exists).</summary>
    private async Task<bool> AllowsNegativeAsync(CompanyInfo company, ItemInfo item, WarehouseInfo warehouse, CancellationToken cancellationToken)
    {
        var policy = await items.CompanyPolicyAsync(item.Id, company.Id.Value, cancellationToken);
        return policy?.AllowNegativeStock ?? warehouse.AllowNegativeStock ?? string.Equals(company.NegativeStockPolicy, "allow", StringComparison.Ordinal);
    }

    /// <summary>Creates the balance row when missing and locks it; the lock holds until the unit of work commits.</summary>
    private static async Task<LockedBalance> LockAsync(IUnitOfWork uow, BalanceKey key, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            tenant = uow.Context.TenantId.Value,
            company = key.CompanyId,
            item = key.ItemId,
            variant = key.VariantId,
            warehouse = key.WarehouseId,
            bin = key.BinId,
            lot = key.LotId,
            serial = key.SerialId,
        };
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app.inv_stock_balances (tenant_id, company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id)
            VALUES (@tenant, @company, @item, @variant, @warehouse, @bin, @lot, @serial)
            ON CONFLICT DO NOTHING
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        return await uow.Connection.QuerySingleAsync<LockedBalance>(new CommandDefinition("""
            SELECT on_hand AS OnHand, reserved AS Reserved, quality_hold AS QualityHold
            FROM app.inv_stock_balances
            WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND variant_id = @variant AND warehouse_id = @warehouse AND bin_id = @bin AND lot_id = @lot AND serial_id = @serial
            FOR UPDATE
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
    }

    private async Task<StockPostingResult> ToResultAsync(StockPosting posting, bool replayed, CancellationToken cancellationToken)
    {
        var entries = new List<StockEntryInfo>(posting.Entries.Count);
        foreach (var e in posting.Entries.OrderBy(static e => e.Sequence))
        {
            var units = await items.UomsAsync(e.ItemId, cancellationToken);
            var code = units.FirstOrDefault(u => u.UomId == e.EnteredUomId)?.UomCode ?? string.Empty;
            entries.Add(new StockEntryInfo(e.Id, e.Sequence, e.ItemId, e.VariantId, e.WarehouseId, e.BinId, e.LotId, e.SerialId, e.EntryType, ItemUomMath.Normalize(e.Quantity), e.EnteredUomId, code, ItemUomMath.Normalize(e.EnteredQuantity), e.PostingDate, e.SourceLineId, e.TransferPairId, e.ReservationId));
        }

        return new StockPostingResult(posting.Id, posting.CompanyId, posting.PostingDate, posting.FiscalPeriodId, entries, replayed);
    }

    private static decimal N(decimal value) => ItemUomMath.Normalize(value);
}

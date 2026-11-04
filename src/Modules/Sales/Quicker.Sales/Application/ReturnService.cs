using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
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
/// Sales returns, the RMA of roadmap 5.8 (DOMAIN_MODEL §11 "sls_returns", POSTING_RULES §2 "Sales return", A-163). A
/// customer asks to send back goods of posted shipment lines (<c>requested</c>); the request is <c>authorised</c> or
/// <c>rejected</c>; the goods are <c>received</c> into stock through the stock engine as
/// <see cref="StockEntryTypes.SaleReturn"/> applied to the shipment's own ledger entries, so under the default cost
/// policy they come back at exactly the cost they left with (Dr Inventory / Cr Cogs, the costing engine's mapping) --
/// a serial comes back as <c>returned</c>, a recalled, quarantined or expired lot comes back on hold and the line
/// records the lot's status; <c>inspected</c> settles each line's disposition (restock, quarantine, scrap or repair:
/// scrap leaves again at the return's cost, repair puts a serial in repair, restock puts a returned serial back in
/// stock); <c>credited</c> issues one credit note per invoice that billed the returned lines, at the invoice's own
/// price and tax on the SalesReturns role, applied to the invoice as it posts; what was never invoiced simply reduces
/// what the shipment line can still be invoiced; <c>closed</c> ends it. A received return is reversed as a whole while
/// nothing was scrapped: its credit notes are reversed and the goods leave again against the return's own entries.
/// </summary>
public sealed class ReturnService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    ICustomerDirectory customers,
    IPartnerDirectory partners,
    ITrackingDirectory tracking,
    IReasonCodeDirectory reasons,
    IInventoryPosting inventory,
    InvoiceService invoices,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "sales_return";

    public static readonly IReadOnlyList<string> CostPolicies = ["original", "current", "specified"];

    public static readonly IReadOnlyList<string> Dispositions = ["restock", "quarantine", "scrap", "repair"];

    /// <summary>Statuses in which the goods are in stock through this return (what counts as returned on the shipment line).</summary>
    private static readonly string[] ReceivedStatuses = ["received", "inspected", "credited", "closed"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SalesReturnSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Returns.AsNoTracking().Include(static r => r.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (partnerId is { } p)
        {
            query = query.Where(r => r.PartnerId == p);
        }

        var returns = await query.OrderByDescending(static r => r.RequestDate).ThenByDescending(static r => r.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<SalesReturnSummary>(returns.Count);
        foreach (var r in returns)
        {
            result.Add(await MapAsync(r, cancellationToken));
        }

        return result;
    }

    /// <summary>A customer's posted shipment lines with goods not yet returned (optionally of one shipment), with the serials that can still come back.</summary>
    public async Task<IReadOnlyList<ReturnableSalesLine>> ReturnableAsync(Guid companyId, Guid partnerId, Guid? shipmentId, CancellationToken cancellationToken)
    {
        var query = from l in db.ShipmentLines.AsNoTracking()
                    join s in db.Shipments.AsNoTracking() on new { l.TenantId, Id = l.ShipmentId } equals new { s.TenantId, s.Id }
                    join ol in db.OrderLines.AsNoTracking() on new { l.TenantId, Id = l.OrderLineId } equals new { ol.TenantId, ol.Id }
                    join o in db.Orders.AsNoTracking() on new { s.TenantId, Id = s.OrderId } equals new { o.TenantId, o.Id }
                    where s.CompanyId == companyId && s.PartnerId == partnerId && s.Status == "posted" && l.QtyReturned < l.QuantityBase
                    select new { Line = l, Shipment = s, OrderLine = ol, Order = o };
        if (shipmentId is { } sh)
        {
            query = query.Where(x => x.Shipment.Id == sh);
        }

        var rows = await query.OrderByDescending(static x => x.Shipment.PostingDate).ThenBy(static x => x.Shipment.Number).ThenBy(static x => x.Line.LineNo).Take(500).ToListAsync(cancellationToken);
        var result = new List<ReturnableSalesLine>(rows.Count);
        foreach (var row in rows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(row.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == item.BaseUomId);
            var returnedSerials = await ReturnedSerialsAsync(row.Line.Id, null, cancellationToken);
            result.Add(new ReturnableSalesLine(row.Line.Id, row.Shipment.Id, row.Shipment.Number, row.Shipment.PostingDate, row.Shipment.WarehouseId, row.OrderLine.Id, row.Order.Number, row.Line.LineNo,
                row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), item?.Tracking ?? "none", item?.BaseUomId ?? row.Line.UomId, uom?.UomCode ?? string.Empty,
                row.Line.QuantityBase, row.Line.QtyReturned, row.Line.QuantityBase - row.Line.QtyReturned, row.Line.QtyInvoiced, row.Line.LotNumber,
                Serials(row.Line.SerialNumbers).Where(s => !returnedSerials.Contains(s)).ToList()));
        }

        return result;
    }

    public async Task<Result<SalesReturnSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var r = await db.Returns.AsNoTracking().Include(static x => x.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return r is null ? Error.NotFound(DocumentType, id) : await MapAsync(r, cancellationToken);
    }

    public async Task<Result<SalesReturnSummary>> CreateAsync(SaveSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = new SalesReturn { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, PartnerId = request.PartnerId, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(ret, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(ret.CompanyId), "RMA", "RMA-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(ret.CompanyId), null, ret.RequestDate, ret.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        ret.Number = number.Value.Text;
        db.Returns.Add(ret);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Created, After: new { ret.Number, ret.PartnerId, lines = ret.Lines.Count }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result<SalesReturnSummary>> UpdateAsync(Guid id, SaveSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = await db.Returns.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("requested" or "authorised"))
        {
            return Error.Conflict("return.not_open", "Only a requested or authorised return is edited.").WithWhy(("status", ret.Status));
        }

        if (request.CompanyId != ret.CompanyId || request.PartnerId != ret.PartnerId)
        {
            return Error.Validation("return.identity_fixed", "A return keeps its company and customer.").WithWhy(("companyId", ret.CompanyId), ("partnerId", ret.PartnerId));
        }

        db.ReturnLines.RemoveRange(ret.Lines);
        ret.Lines.Clear();
        var applied = await ApplyAsync(ret, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Updated, After: new { lines = ret.Lines.Count }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var ret = await db.Returns.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("requested" or "authorised"))
        {
            return Error.Conflict("return.not_open", "Only a requested or authorised return is deleted; a received one is reversed.").WithWhy(("status", ret.Status));
        }

        db.Returns.Remove(ret);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Deleted, CompanyId: ret.CompanyId), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<SalesReturnSummary>> AuthoriseAsync(Guid id, CancellationToken cancellationToken)
    {
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "requested")
        {
            return Error.Conflict("return.not_requested", "Only a requested return is authorised.").WithWhy(("status", ret.Status));
        }

        ret.Status = "authorised";
        ret.AuthorisedAt = clock.UtcNow;
        ret.AuthorisedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Approved, After: new { status = ret.Status }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result<SalesReturnSummary>> RejectAsync(Guid id, RejectSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("return.reason_required", "A rejection names its reason.");
        }

        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("requested" or "authorised"))
        {
            return Error.Conflict("return.not_open", "Only a requested or authorised return is rejected.").WithWhy(("status", ret.Status));
        }

        ret.Status = "rejected";
        ret.RejectionReason = reason;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Rejected, After: new { status = ret.Status }, Reason: reason, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>The goods arrive: the lines are checked again against the shipment lines as they stand, each part of
    /// each line is applied to the shipment's own ledger entries (serials to their entry, quantities to the entries in
    /// the shipment's order, skipping what earlier returns took) and the stock engine brings them back in at the
    /// return's cost policy; the lot's status at that moment is recorded and a blocked lot forces quarantine.</summary>
    public async Task<Result<SalesReturnSummary>> ReceiveAsync(Guid id, ReceiveSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "authorised")
        {
            return Error.Conflict("return.not_authorised", "Only an authorised return is received.").WithWhy(("status", ret.Status));
        }

        var company = await companies.FindAsync(new CompanyId(ret.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", ret.CompanyId);
        }

        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        if (postingDate < ret.RequestDate)
        {
            return Error.Validation("return.before_request", "Goods are received on or after the day the return was requested.").WithWhy(("requestDate", ret.RequestDate), ("postingDate", postingDate));
        }

        // Check the lines again against the shipment lines as they stand now, keeping the line ids.
        var saved = new SaveSalesReturnRequest(ret.CompanyId, ret.PartnerId, ret.Lines.Select(static l => new SaveSalesReturnLineRequest(l.ShipmentLineId, l.Quantity, Serials(l.SerialNumbers), l.ReasonCode, l.Reason, l.Disposition, l.BinId, l.UnitCost)).ToList(),
            ret.WarehouseId, ret.RequestDate, ret.CostPolicy, ret.Reason, ret.CustomerReference, ret.Notes, ret.BranchId, Shared.Parse(ret.CustomFields));
        var kept = ret.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
        db.ReturnLines.RemoveRange(ret.Lines);
        ret.Lines.Clear();
        var applied = await ApplyAsync(ret, saved, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        foreach (var line in ret.Lines)
        {
            if (kept.TryGetValue(line.LineNo, out var lineId))
            {
                line.Id = lineId;
            }
        }

        var shipmentLineIds = ret.Lines.Select(static l => l.ShipmentLineId).Distinct().ToList();
        var shipmentLines = await db.ShipmentLines.Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var shipmentIds = shipmentLines.Values.Select(static l => l.ShipmentId).Distinct().ToList();
        var shipments = await db.Shipments.AsNoTracking().Where(s => shipmentIds.Contains(s.Id)).ToDictionaryAsync(static s => s.Id, cancellationToken);
        foreach (var shipment in shipments.Values)
        {
            if (postingDate < shipment.PostingDate)
            {
                return Error.Validation("return.before_shipment", "Goods are received on or after the day they were shipped.").WithWhy(("shipment", shipment.Number), ("shipmentDate", shipment.PostingDate), ("postingDate", postingDate));
            }
        }

        var stockLines = new List<StockLine>();
        var appliedByLine = new Dictionary<Guid, List<(SalesReturnAppliedEntry Entry, Guid? LotId, Guid? BinId)>>();
        foreach (var line in ret.Lines)
        {
            var shipmentLine = shipmentLines[line.ShipmentLineId];
            var taken = await TakenAsync(shipmentLine.Id, ret.Id, cancellationToken);
            var serials = Serials(line.SerialNumbers);
            var parts = new List<(SalesReturnAppliedEntry Entry, Guid? LotId, Guid? BinId)>();
            var remaining = line.Quantity;
            foreach (var allocation in Allocations(shipmentLine.Allocations))
            {
                foreach (var entry in allocation.Entries ?? [])
                {
                    if (remaining <= 0m)
                    {
                        break;
                    }

                    if (entry.SerialNumber is { } serial)
                    {
                        if (serials.Count > 0 && !serials.Contains(serial, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        if (taken.TryGetValue(entry.SleId, out var already) && already >= entry.Quantity)
                        {
                            continue;
                        }

                        parts.Add((new SalesReturnAppliedEntry(entry.SleId, serial, entry.Quantity), allocation.LotId, allocation.BinId));
                        remaining -= entry.Quantity;
                    }
                    else
                    {
                        var left = entry.Quantity - taken.GetValueOrDefault(entry.SleId);
                        if (left <= 0m)
                        {
                            continue;
                        }

                        var take = Math.Min(left, remaining);
                        parts.Add((new SalesReturnAppliedEntry(entry.SleId, null, take), allocation.LotId, allocation.BinId));
                        remaining -= take;
                    }
                }
            }

            if (remaining > 0m)
            {
                return Error.Conflict("return.over_return", "More than was shipped would come back.").WithWhy(("lineNo", line.LineNo), ("shortBy", remaining));
            }

            appliedByLine[line.Id] = parts;
            foreach (var (entry, lotId, binId) in parts)
            {
                stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.SaleReturn, entry.Quantity, ret.WarehouseId, VariantId: line.VariantId, BinId: line.BinId ?? binId, LotId: lotId,
                    SourceLineId: line.Id, AppliesToSleId: ret.CostPolicy == "original" ? entry.SleId : null, UnitCost: ret.CostPolicy == "specified" ? line.UnitCost : null,
                    PartnerId: ret.PartnerId, SerialNumbers: entry.SerialNumber is { } sn ? [sn] : null, DimensionSetId: shipmentLine.DimensionSetId));
            }
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(ret.CompanyId, postingDate, DocumentType, ret.Id, stockLines, IdempotencyKey: $"sales_return:{ret.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var entries = posted.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.OrderBy(static e => e.Sequence).ToList());
        var lotStatuses = new Dictionary<Guid, LotInfo?>();
        foreach (var line in ret.Lines)
        {
            if (!entries.TryGetValue(line.Id, out var own))
            {
                continue;
            }

            // Each applied part remembers the return's own inbound entry (by serial, else by bin, lot and quantity), for scrap and reversal.
            var unmatched = own.ToList();
            var parts = new List<SalesReturnAppliedEntry>();
            foreach (var (entry, lotId, binId) in appliedByLine[line.Id])
            {
                var match = entry.SerialNumber is { } sn
                    ? unmatched.FirstOrDefault(e => string.Equals(e.SerialNumber, sn, StringComparison.Ordinal))
                    : unmatched.FirstOrDefault(e => e.LotId == lotId && e.BinId == (line.BinId ?? binId) && e.Quantity == entry.Quantity) ?? unmatched.FirstOrDefault(e => e.LotId == lotId && e.Quantity == entry.Quantity);
                if (match is not null)
                {
                    unmatched.Remove(match);
                }

                parts.Add(entry with { ReturnSleId = match?.Id, BinId = match?.BinId ?? line.BinId ?? binId, LotId = match?.LotId ?? lotId });
            }

            line.Applied = JsonSerializer.Serialize(parts, Json);
            line.SleId = own[0].Id;
            line.SleIds = JsonSerializer.Serialize(own.Select(static e => e.Id).ToList());
            line.CostAmount = own.Sum(static e => e.CostAmount);
            line.BinId ??= own[0].BinId;
            var lots = own.Where(static e => e.LotId is not null).Select(static e => e.LotId!.Value).Distinct().ToList();
            if (lots.Count == 1)
            {
                line.LotId = lots[0];
                line.LotNumber = own[0].LotNumber ?? line.LotNumber;
            }

            foreach (var lotId in lots)
            {
                if (!lotStatuses.TryGetValue(lotId, out var lot))
                {
                    lot = await tracking.FindLotAsync(lotId, cancellationToken);
                    lotStatuses[lotId] = lot;
                }

                if (lot is null)
                {
                    continue;
                }

                // A blocked lot's stock comes back on hold (the stock engine's rule); the line records it and quarantine is the only way forward but scrap.
                if (LotStatuses.Blocks(lot.Status) || line.LotStatus is null)
                {
                    line.LotStatus = lot.Status;
                }

                if (LotStatuses.Blocks(lot.Status) && line.Disposition is not "scrap")
                {
                    line.Disposition = "quarantine";
                }
            }

            var shipmentLine = shipmentLines[line.ShipmentLineId];
            shipmentLine.QtyReturned += line.Quantity;
        }

        ret.TotalCost = ret.Lines.Sum(static l => l.CostAmount);
        ret.PostingDate = postingDate;
        ret.StockPostingId = posted.Value.PostingId;
        ret.JournalEntryId = posted.Value.JournalEntryId;
        ret.Status = "received";
        ret.ReceivedAt = clock.UtcNow;
        ret.ReceivedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Posted, After: new { status = ret.Status, postingDate, ret.TotalCost, journal = posted.Value.JournalNumber, blockedLots = ret.Lines.Where(static l => l.LotStatus is { } s && LotStatuses.Blocks(s)).Select(static l => l.LotNumber) }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>Inspection settles each line's disposition: restock (a returned serial goes back in stock), quarantine
    /// (the goods stay where they were put, on hold when their lot is blocked), scrap (the goods leave again at the
    /// return's cost against the return's own entries, Dr Scrap / Cr Inventory) or repair (a serial goes into repair;
    /// it comes back in stock through the serial itself). A blocked lot is quarantined or scrapped, never restocked.</summary>
    public async Task<Result<SalesReturnSummary>> InspectAsync(Guid id, InspectSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "received")
        {
            return Error.Conflict("return.not_received", "Only a received return is inspected.").WithWhy(("status", ret.Status));
        }

        var company = await companies.FindAsync(new CompanyId(ret.CompanyId), cancellationToken);
        var postingDate = request.PostingDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (postingDate < ret.PostingDate)
        {
            return Error.Validation("return.before_receipt", "Goods are inspected on or after the day they were received.").WithWhy(("receivedOn", ret.PostingDate), ("postingDate", postingDate));
        }

        foreach (var verdict in request.Lines ?? [])
        {
            var line = ret.Lines.SingleOrDefault(l => l.Id == verdict.LineId);
            if (line is null)
            {
                return Error.Validation("return.line_unknown", "The line does not belong to the return.").WithWhy(("lineId", verdict.LineId));
            }

            var disposition = (verdict.Disposition ?? string.Empty).Trim().ToLowerInvariant();
            if (!Dispositions.Contains(disposition, StringComparer.Ordinal))
            {
                return Error.Validation("return.disposition_invalid", "The disposition is restock, quarantine, scrap or repair.").WithWhy(("lineNo", line.LineNo), ("disposition", verdict.Disposition));
            }

            line.Disposition = disposition;
            line.DispositionNote = Shared.Trim(verdict.Note);
        }

        var scrapLines = new List<StockLine>();
        foreach (var line in ret.Lines)
        {
            if (line.Disposition is null)
            {
                return Error.Validation("return.disposition_required", "Every line gets a disposition at inspection.").WithWhy(("lineNo", line.LineNo));
            }

            if (line.LotStatus is { } lotStatus && LotStatuses.Blocks(lotStatus) && line.Disposition is "restock" or "repair")
            {
                return Error.Conflict("return.lot_blocked", $"The lot is {lotStatus}; its goods are quarantined or scrapped, never put back on sale.").WithWhy(("lineNo", line.LineNo), ("lotNumber", line.LotNumber), ("status", lotStatus));
            }

            var item = await items.FindAsync(line.ItemId, cancellationToken);
            var serialTracked = item?.Tracking is "serial" or "lot_and_serial";
            switch (line.Disposition)
            {
                case "scrap":
                    foreach (var applied in Applied(line))
                    {
                        scrapLines.Add(new StockLine(line.ItemId, StockEntryTypes.Scrap, applied.Quantity, ret.WarehouseId, VariantId: line.VariantId, BinId: applied.BinId ?? line.BinId, LotId: applied.LotId ?? line.LotId,
                            SourceLineId: line.Id, AppliesToSleId: applied.ReturnSleId, SerialNumbers: applied.SerialNumber is { } sn ? [sn] : null));
                    }

                    break;
                case "repair" when serialTracked:
                    foreach (var serial in Serials(line.SerialNumbers))
                    {
                        var moved = await SetSerialAsync(line.ItemId, serial, SerialStatuses.InRepair, line.DispositionNote ?? $"Return {ret.Number}: repair", cancellationToken);
                        if (moved.IsFailure)
                        {
                            return moved.Error!.WithWhy(("lineNo", line.LineNo));
                        }
                    }

                    break;
                case "restock" when serialTracked:
                    foreach (var serial in Serials(line.SerialNumbers))
                    {
                        var moved = await SetSerialAsync(line.ItemId, serial, SerialStatuses.InStock, line.DispositionNote ?? $"Return {ret.Number}: restocked", cancellationToken);
                        if (moved.IsFailure)
                        {
                            return moved.Error!.WithWhy(("lineNo", line.LineNo));
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        string? scrapJournal = null;
        if (scrapLines.Count > 0)
        {
            var scrapped = await inventory.PostAsync(new StockPostingRequest(ret.CompanyId, postingDate, DocumentType, ret.Id, scrapLines, IdempotencyKey: $"sales_return_scrap:{ret.Id}"), cancellationToken);
            if (scrapped.IsFailure)
            {
                return scrapped.Error!;
            }

            scrapJournal = scrapped.Value.JournalNumber;
            foreach (var group in scrapped.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value))
            {
                var line = ret.Lines.Single(l => l.Id == group.Key);
                var own = group.OrderBy(static e => e.Sequence).ToList();
                line.ScrapSleId = own[0].Id;
                line.ScrapCostAmount = Math.Abs(own.Sum(static e => e.CostAmount));
            }
        }

        ret.Status = "inspected";
        ret.InspectedAt = clock.UtcNow;
        ret.InspectedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.StateChanged, After: new { status = ret.Status, dispositions = ret.Lines.Select(static l => new { l.LineNo, l.Disposition }), scrapJournal }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>Credits what the returned lines were invoiced: one credit note per invoice that billed them, each line
    /// crediting the invoice line's quantity at that line's own price, discount and tax on the SalesReturns role, posted
    /// at once and applied to its invoice. Returned goods that were never invoiced need no credit: they reduce what
    /// the shipment line can still be invoiced.</summary>
    public async Task<Result<SalesReturnSummary>> CreditAsync(Guid id, CreditSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("received" or "inspected"))
        {
            return Error.Conflict("return.not_received", "Only a received or inspected return is credited.").WithWhy(("status", ret.Status));
        }

        var company = await companies.FindAsync(new CompanyId(ret.CompanyId), cancellationToken);
        var documentDate = request.DocumentDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (documentDate < ret.PostingDate)
        {
            return Error.Validation("return.before_receipt", "A return is credited on or after the day its goods were received.").WithWhy(("receivedOn", ret.PostingDate), ("documentDate", documentDate));
        }

        // What each returned line was invoiced, oldest invoice first, less what credit notes already credited.
        var shipmentLineIds = ret.Lines.Select(static l => l.ShipmentLineId).Distinct().ToList();
        var invoiced = await (from il in db.InvoiceLines.AsNoTracking()
                              join i in db.Invoices.AsNoTracking() on new { il.TenantId, Id = il.InvoiceId } equals new { i.TenantId, i.Id }
                              where il.ShipmentLineId != null && shipmentLineIds.Contains(il.ShipmentLineId.Value) && i.Kind == "invoice" && i.Status == "posted"
                              orderby i.PostingDate, i.Number, il.LineNo
                              select new { Line = il, Invoice = i }).ToListAsync(cancellationToken);
        var creditable = invoiced.ToDictionary(static x => x.Line.Id, static x => x.Line.Quantity - x.Line.QtyCredited);
        var allocations = new List<(SalesReturnLine ReturnLine, Guid InvoiceId, Guid InvoiceLineId, decimal Quantity)>();
        foreach (var line in ret.Lines)
        {
            var toCredit = line.Quantity - line.QtyCredited;
            foreach (var row in invoiced.Where(x => x.Line.ShipmentLineId == line.ShipmentLineId))
            {
                if (toCredit <= 0m)
                {
                    break;
                }

                var take = Math.Min(toCredit, creditable[row.Line.Id]);
                if (take <= 0m)
                {
                    continue;
                }

                creditable[row.Line.Id] -= take;
                toCredit -= take;
                allocations.Add((line, row.Invoice.Id, row.Line.Id, take));
            }
        }

        if (allocations.Count == 0)
        {
            return Error.Conflict("return.nothing_to_credit", "Nothing on the return was invoiced, or it was credited already; what came back uninvoiced reduces what the shipment can still be invoiced.");
        }

        var shipmentLines = await db.ShipmentLines.Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var noteIds = CreditNoteIds(ret);
        var notes = new List<string>();
        string? currency = null;
        foreach (var byInvoice in allocations.GroupBy(static a => a.InvoiceId))
        {
            var invoice = invoiced.First(x => x.Invoice.Id == byInvoice.Key).Invoice;
            var creditLines = byInvoice.Select(a => new SaveSalesInvoiceLineRequest("invoice", a.Quantity, InvoiceLineId: a.InvoiceLineId, AccountRole: AccountRoles.SalesReturns)).ToList();
            var created = await invoices.CreateAsync(new SaveSalesInvoiceRequest(ret.CompanyId, ret.PartnerId, creditLines, "credit_note", ret.CustomerReference, invoice.Id, documentDate, documentDate, invoice.Currency,
                Notes: ret.Reason is null ? $"Return {ret.Number}" : $"Return {ret.Number}: {ret.Reason}", BranchId: ret.BranchId), null, ret.Id, cancellationToken);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            var postedNote = await invoices.PostAsync(created.Value.Id, cancellationToken);
            if (postedNote.IsFailure)
            {
                return postedNote.Error!;
            }

            var index = 0;
            foreach (var allocation in byInvoice)
            {
                var noteLine = postedNote.Value.Lines[index++];
                allocation.ReturnLine.QtyCredited += allocation.Quantity;
                allocation.ReturnLine.NetCredited += noteLine.NetAmount;
                shipmentLines[allocation.ReturnLine.ShipmentLineId].QtyReturnCredited += allocation.Quantity;
            }

            noteIds.Add(postedNote.Value.Id);
            notes.Add(postedNote.Value.Number);
            currency ??= postedNote.Value.Currency;
            ret.TotalCredited += postedNote.Value.TotalNet;
        }

        ret.CreditNoteIds = JsonSerializer.Serialize(noteIds);
        ret.Currency ??= currency;
        ret.Status = "credited";
        ret.CreditedAt = clock.UtcNow;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.StateChanged, After: new { status = ret.Status, creditNotes = notes, ret.TotalCredited }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result<SalesReturnSummary>> CloseAsync(Guid id, CancellationToken cancellationToken)
    {
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("received" or "inspected" or "credited"))
        {
            return Error.Conflict("return.not_received", "A return is closed once its goods were received.").WithWhy(("status", ret.Status));
        }

        ret.Status = "closed";
        ret.ClosedAt = clock.UtcNow;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.StateChanged, After: new { status = ret.Status }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>Undoes a received return as a whole: its credit notes are reversed (refused while one is settled) and
    /// the goods leave again against the return's own entries at the same cost; refused once anything was scrapped.</summary>
    public async Task<Result<SalesReturnSummary>> ReverseAsync(Guid id, ReverseSalesReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("return.reason_required", "A reversal names its reason.");
        }

        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status is not ("received" or "inspected" or "credited" or "closed"))
        {
            return Error.Conflict("return.not_received", "Only a received return is reversed; a requested one is rejected or deleted.").WithWhy(("status", ret.Status));
        }

        if (ret.Lines.Any(static l => l.ScrapSleId is not null))
        {
            return Error.Conflict("return.disposed", "Goods of the return were scrapped; they cannot come back, so the return stands.").WithWhy(("lines", ret.Lines.Where(static l => l.ScrapSleId is not null).Select(static l => l.LineNo)));
        }

        var company = await companies.FindAsync(new CompanyId(ret.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < ret.PostingDate)
        {
            return Error.Validation("return.reversal_date_invalid", "A return is reversed on or after the day its goods were received.").WithWhy(("receivedOn", ret.PostingDate), ("reversalDate", reversalDate));
        }

        foreach (var noteId in CreditNoteIds(ret))
        {
            var reversedNote = await invoices.ReverseAsync(noteId, new ReverseSalesInvoiceRequest(reason, reversalDate), viaReturn: true, cancellationToken);
            if (reversedNote.IsFailure)
            {
                return reversedNote.Error!;
            }
        }

        var stockLines = new List<StockLine>();
        foreach (var line in ret.Lines)
        {
            foreach (var applied in Applied(line))
            {
                stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.SaleShipment, applied.Quantity, ret.WarehouseId, VariantId: line.VariantId, BinId: applied.BinId ?? line.BinId, LotId: applied.LotId ?? line.LotId,
                    SourceLineId: line.Id, AppliesToSleId: applied.ReturnSleId, PartnerId: ret.PartnerId, SerialNumbers: applied.SerialNumber is { } sn ? [sn] : null));
            }
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(ret.CompanyId, reversalDate, DocumentType, ret.Id, stockLines, IdempotencyKey: $"sales_return_reversal:{ret.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var shipmentLineIds = ret.Lines.Select(static l => l.ShipmentLineId).Distinct().ToList();
        var shipmentLines = await db.ShipmentLines.Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        foreach (var line in ret.Lines)
        {
            var shipmentLine = shipmentLines[line.ShipmentLineId];
            shipmentLine.QtyReturned = Math.Max(0m, shipmentLine.QtyReturned - line.Quantity);
            shipmentLine.QtyReturnCredited = Math.Max(0m, shipmentLine.QtyReturnCredited - line.QtyCredited);
            line.QtyCredited = 0m;
            line.NetCredited = 0m;
        }

        ret.TotalCredited = 0m;
        ret.Status = "reversed";
        ret.ReversalPostingId = posted.Value.PostingId;
        ret.ReversalReason = reason;
        ret.ReversedAt = clock.UtcNow;
        ret.ReversedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Reversed, After: new { status = ret.Status, reversalDate, journal = posted.Value.JournalNumber }, Reason: reason, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result> ApplyAsync(SalesReturn ret, SaveSalesReturnRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("return.lines_required", "A return has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var customer = await customers.FindCustomerAsync(request.CompanyId, request.PartnerId, cancellationToken);
        if (customer is null)
        {
            return Error.Validation("return.customer_unknown", "The customer has no account in this company.").WithWhy(("partnerId", request.PartnerId), ("companyId", request.CompanyId));
        }

        var policy = (request.CostPolicy ?? "original").Trim().ToLowerInvariant();
        if (!CostPolicies.Contains(policy, StringComparer.Ordinal))
        {
            return Error.Validation("return.cost_policy_invalid", "The cost policy is original, current or specified.").WithWhy(("costPolicy", request.CostPolicy));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var requestDate = request.RequestDate ?? clock.TodayIn(company.TimeZone);
        var lines = new List<SalesReturnLine>();
        var requestingNow = new Dictionary<Guid, decimal>();
        var serialsNow = new HashSet<string>(StringComparer.Ordinal);
        Guid? warehouseId = request.WarehouseId;
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            var shipmentLine = await db.ShipmentLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.ShipmentLineId, cancellationToken);
            var shipment = shipmentLine is null ? null : await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shipmentLine.ShipmentId, cancellationToken);
            if (shipmentLine is null || shipment is null || shipment.CompanyId != request.CompanyId || shipment.PartnerId != request.PartnerId)
            {
                return Error.Validation("return.shipment_line_unknown", "The shipment line does not belong to this customer and company.").WithWhy(("lineNo", lineNo), ("shipmentLineId", l.ShipmentLineId));
            }

            if (shipment.Status != "posted")
            {
                return Error.Conflict("return.shipment_not_posted", "Goods come back from a posted shipment.").WithWhy(("lineNo", lineNo), ("shipment", shipment.Number), ("status", shipment.Status));
            }

            warehouseId ??= shipment.WarehouseId;
            var item = await items.FindAsync(shipmentLine.ItemId, cancellationToken);
            if (item is null)
            {
                return Error.NotFound("item", shipmentLine.ItemId);
            }

            if (l.Quantity <= 0m)
            {
                return Error.Validation("return.quantity_invalid", "Quantities are positive.").WithWhy(("lineNo", lineNo));
            }

            var already = requestingNow.GetValueOrDefault(shipmentLine.Id);
            if (shipmentLine.QtyReturned + already + l.Quantity > shipmentLine.QuantityBase)
            {
                return Error.Conflict("return.over_return", "More than was shipped would come back.").WithWhy(("lineNo", lineNo), ("shipment", shipment.Number), ("shipped", shipmentLine.QuantityBase), ("returned", shipmentLine.QtyReturned + already), ("requested", l.Quantity));
            }

            requestingNow[shipmentLine.Id] = already + l.Quantity;
            var serials = l.SerialNumbers?.Select(static s => s.Trim()).Where(static s => s.Length > 0).ToList() ?? [];
            if (item.Tracking is "serial" or "lot_and_serial")
            {
                if (l.Quantity != decimal.Truncate(l.Quantity) || serials.Count != (int)l.Quantity)
                {
                    return Error.Validation("return.serials_required", "A serialised item names one serial number per unit coming back.").WithWhy(("lineNo", lineNo), ("item", item.Code), ("quantity", l.Quantity), ("serialNumbers", serials.Count));
                }

                var returned = await ReturnedSerialsAsync(shipmentLine.Id, ret.Id, cancellationToken);
                var shipped = Serials(shipmentLine.SerialNumbers);
                foreach (var serial in serials)
                {
                    if (!shipped.Contains(serial, StringComparer.Ordinal) || returned.Contains(serial) || !serialsNow.Add(serial))
                    {
                        return Error.Validation("return.serial_not_shipped", $"Serial {serial} was not shipped on this line, or came back already.").WithWhy(("lineNo", lineNo), ("item", item.Code), ("serialNumber", serial));
                    }
                }
            }
            else if (serials.Count > 0)
            {
                return Error.Validation("return.serials_not_tracked", "The item is not serial-tracked.").WithWhy(("lineNo", lineNo), ("item", item.Code));
            }

            Guid? reasonCodeId = null;
            string? reasonCode = null;
            if (!string.IsNullOrWhiteSpace(l.ReasonCode))
            {
                var reason = await reasons.FindReasonAsync(l.ReasonCode, cancellationToken);
                if (reason is null || !reason.IsActive || reason.AppliesTo != "return")
                {
                    return Error.Validation("return.reason_code_invalid", "The reason code is not an active return reason.").WithWhy(("lineNo", lineNo), ("reasonCode", l.ReasonCode));
                }

                if (reason.RequiresNote && Shared.Trim(l.Reason) is null)
                {
                    return Error.Validation("return.reason_note_required", "The reason code asks for a note.").WithWhy(("lineNo", lineNo), ("reasonCode", reason.Code));
                }

                reasonCodeId = reason.Id;
                reasonCode = reason.Code;
            }

            string? disposition = null;
            if (!string.IsNullOrWhiteSpace(l.Disposition))
            {
                disposition = l.Disposition.Trim().ToLowerInvariant();
                if (!Dispositions.Contains(disposition, StringComparer.Ordinal))
                {
                    return Error.Validation("return.disposition_invalid", "The disposition is restock, quarantine, scrap or repair.").WithWhy(("lineNo", lineNo), ("disposition", l.Disposition));
                }
            }

            if (policy == "specified" && l.UnitCost is not { } unitCost)
            {
                return Error.Validation("return.unit_cost_required", "Under the specified cost policy every line names its unit cost.").WithWhy(("lineNo", lineNo));
            }

            if (l.UnitCost is < 0m)
            {
                return Error.Validation("return.unit_cost_invalid", "A unit cost is zero or more.").WithWhy(("lineNo", lineNo));
            }

            lines.Add(new SalesReturnLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = ret.TenantId,
                ReturnId = ret.Id,
                LineNo = lineNo,
                ShipmentLineId = shipmentLine.Id,
                OrderLineId = shipmentLine.OrderLineId,
                ItemId = shipmentLine.ItemId,
                VariantId = shipmentLine.VariantId,
                Quantity = l.Quantity,
                UomId = item.BaseUomId,
                BinId = l.BinId,
                LotNumber = shipmentLine.LotNumber,
                SerialNumbers = JsonSerializer.Serialize(serials),
                ReasonCodeId = reasonCodeId,
                ReasonCode = reasonCode,
                Reason = Shared.Trim(l.Reason),
                Disposition = disposition,
                UnitCost = policy == "specified" ? l.UnitCost : null,
                CreatedAt = clock.UtcNow,
            });
        }

        var warehouse = await warehouses.FindAsync(warehouseId!.Value, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != request.CompanyId || !warehouse.IsActive)
        {
            return Error.Validation("return.warehouse_invalid", "The goods come back to an active warehouse of the company.").WithWhy(("warehouseId", warehouseId));
        }

        foreach (var line in lines.Where(static l => l.BinId is not null))
        {
            var bin = await warehouses.FindBinAsync(line.BinId!.Value, cancellationToken);
            if (bin is null || bin.WarehouseId != warehouse.Id || !bin.IsActive)
            {
                return Error.Validation("return.bin_invalid", "The bin is not an active bin of the warehouse the goods come back to.").WithWhy(("lineNo", line.LineNo), ("binId", line.BinId), ("warehouse", warehouse.Code));
            }
        }

        ret.WarehouseId = warehouse.Id;
        ret.RequestDate = requestDate;
        ret.CostPolicy = policy;
        ret.Reason = Shared.Trim(request.Reason);
        ret.CustomerReference = Shared.Trim(request.CustomerReference);
        ret.Notes = Shared.Trim(request.Notes);
        ret.BranchId = request.BranchId;
        ret.CustomFields = custom.Value;
        ret.Lines.AddRange(lines);
        return Result.Success();
    }

    /// <summary>How much of each of a shipment line's ledger entries earlier returns (other than this one) already took back.</summary>
    private async Task<Dictionary<Guid, decimal>> TakenAsync(Guid shipmentLineId, Guid exceptReturnId, CancellationToken cancellationToken)
    {
        var rows = await (from l in db.ReturnLines.AsNoTracking()
                          join r in db.Returns.AsNoTracking() on new { l.TenantId, Id = l.ReturnId } equals new { r.TenantId, r.Id }
                          where l.ShipmentLineId == shipmentLineId && r.Id != exceptReturnId && ReceivedStatuses.Contains(r.Status)
                          select l.Applied).ToListAsync(cancellationToken);
        var taken = new Dictionary<Guid, decimal>();
        foreach (var json in rows)
        {
            foreach (var entry in JsonSerializer.Deserialize<List<SalesReturnAppliedEntry>>(json, Json) ?? [])
            {
                taken[entry.SleId] = taken.GetValueOrDefault(entry.SleId) + entry.Quantity;
            }
        }

        return taken;
    }

    /// <summary>The serials of a shipment line that came back on returns in stock (other than the one given).</summary>
    private async Task<HashSet<string>> ReturnedSerialsAsync(Guid shipmentLineId, Guid? exceptReturnId, CancellationToken cancellationToken)
    {
        var rows = await (from l in db.ReturnLines.AsNoTracking()
                          join r in db.Returns.AsNoTracking() on new { l.TenantId, Id = l.ReturnId } equals new { r.TenantId, r.Id }
                          where l.ShipmentLineId == shipmentLineId && (exceptReturnId == null || r.Id != exceptReturnId) && ReceivedStatuses.Contains(r.Status)
                          select l.SerialNumbers).ToListAsync(cancellationToken);
        return rows.SelectMany(Serials).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Result> SetSerialAsync(Guid itemId, string serialNumber, string status, string note, CancellationToken cancellationToken)
    {
        var serial = await tracking.FindSerialAsync(itemId, serialNumber, cancellationToken);
        if (serial is null)
        {
            return Error.NotFound("serial", serialNumber);
        }

        if (serial.Status == status)
        {
            return Result.Success();
        }

        var moved = await tracking.SetSerialStatusAsync(serial.Id, status, note, cancellationToken);
        return moved.IsFailure ? moved.Error! : Result.Success();
    }

    private static IReadOnlyList<ShipmentAllocation> Allocations(string json) => JsonSerializer.Deserialize<List<ShipmentAllocation>>(json, Json) ?? [];

    private static IReadOnlyList<SalesReturnAppliedEntry> Applied(SalesReturnLine line) => JsonSerializer.Deserialize<List<SalesReturnAppliedEntry>>(line.Applied, Json) ?? [];

    private static List<Guid> CreditNoteIds(SalesReturn ret) => JsonSerializer.Deserialize<List<Guid>>(ret.CreditNoteIds) ?? [];

    private static IReadOnlyList<string> Serials(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private async Task<SalesReturnSummary> MapAsync(SalesReturn r, CancellationToken cancellationToken)
    {
        var partner = await customers.FindCustomerAsync(r.CompanyId, r.PartnerId, cancellationToken) is { } account
            ? (account.PartnerCode, account.PartnerName.Values)
            : (await partners.FindAsync(r.PartnerId, cancellationToken) is { } p ? (p.Code, p.LegalName.Values) : (string.Empty, Empty()));
        var warehouse = await warehouses.FindAsync(r.WarehouseId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(r.CompanyId), cancellationToken);
        var shipmentLineIds = r.Lines.Select(static l => l.ShipmentLineId).Distinct().ToList();
        var shipmentLines = await db.ShipmentLines.AsNoTracking().Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var shipmentIds = shipmentLines.Values.Select(static l => l.ShipmentId).Distinct().ToList();
        var shipments = await db.Shipments.AsNoTracking().Where(s => shipmentIds.Contains(s.Id)).ToDictionaryAsync(static s => s.Id, cancellationToken);
        var lines = new List<SalesReturnLineSummary>(r.Lines.Count);
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            var shipmentLine = shipmentLines.GetValueOrDefault(l.ShipmentLineId);
            var shipment = shipmentLine is null ? null : shipments.GetValueOrDefault(shipmentLine.ShipmentId);
            lines.Add(new SalesReturnLineSummary(l.Id, l.LineNo, l.ShipmentLineId, shipment?.Id ?? Guid.Empty, shipment?.Number ?? string.Empty, shipmentLine?.LineNo ?? 0, l.OrderLineId, l.ItemId, item?.Code ?? string.Empty,
                item?.Name.Values ?? Empty(), item?.Tracking ?? "none", l.VariantId, l.Quantity, l.UomId, uom?.UomCode ?? string.Empty, l.BinId, l.LotNumber, l.LotStatus, l.LotStatus is { } s && LotStatuses.Blocks(s),
                Serials(l.SerialNumbers), l.ReasonCodeId, l.ReasonCode, l.Reason, l.Disposition, l.DispositionNote, l.UnitCost, Applied(l), l.CostAmount, l.SleId, l.ScrapSleId, l.ScrapCostAmount, l.QtyCredited, l.NetCredited));
        }

        var noteIds = CreditNoteIds(r);
        var notes = noteIds.Count == 0
            ? []
            : (await db.Invoices.AsNoTracking().Where(i => noteIds.Contains(i.Id)).OrderBy(static i => i.Number).ToListAsync(cancellationToken))
                .Select(static i => new SalesReturnCreditNoteRef(i.Id, i.Number, i.Status, i.TotalNet, i.TotalGross, i.Currency)).ToList();
        return new SalesReturnSummary(r.Id, r.CompanyId, r.Number, r.Status, r.PartnerId, partner.Item1, partner.Item2, r.WarehouseId, warehouse?.Code, r.RequestDate, r.PostingDate, r.CostPolicy, r.Reason, r.CustomerReference,
            r.Currency, company?.FunctionalCurrency.Code ?? string.Empty, r.TotalCost, r.TotalCredited, notes, r.StockPostingId, r.JournalEntryId, r.ReversalReason, r.RejectionReason, r.Notes, Shared.Parse(r.CustomFields), lines,
            r.AuthorisedAt, r.ReceivedAt, r.InspectedAt, r.CreditedAt, r.ClosedAt, r.UpdatedAt);
    }
}

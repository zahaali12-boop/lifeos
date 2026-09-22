using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>
/// Two-step stock transfers: a draft names the source, destination and lines; shipping moves the stock out of the
/// source into the company's in-transit warehouse (transfer_out / transfer_in pairs), receiving moves it from transit
/// into the destination, in full or in parts. Every movement goes through the stock posting engine, so the last unit
/// shipped twice at the same moment leaves exactly one transfer shipped (hard scenario 4).
/// </summary>
public sealed class TransferService(
    InventoryDbContext db,
    IInventoryPosting posting,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IUomDirectory uoms,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "stock_transfer";

    public async Task<IReadOnlyList<TransferSummary>> ListAsync(Guid? companyId, string? status, Guid? warehouseId, CancellationToken cancellationToken)
    {
        var query = db.Transfers.Include(static t => t.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(t => t.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(t => t.Status == s);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(t => t.FromWarehouseId == w || t.ToWarehouseId == w);
        }

        var transfers = await query.OrderByDescending(static t => t.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<TransferSummary>(transfers.Count);
        foreach (var transfer in transfers)
        {
            result.Add(await MapAsync(transfer, cancellationToken));
        }

        return result;
    }

    public async Task<TransferSummary?> GetAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var transfer = await db.Transfers.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
        return transfer is null ? null : await MapAsync(transfer, cancellationToken);
    }

    public async Task<Result<TransferSummary>> CreateAsync(SaveTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Validation.Scope(principal, InventoryPermissions.TransferManage, request.CompanyId, request.FromWarehouseId, request.ToWarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var transfer = new Transfer { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(transfer, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(transfer, cancellationToken);
    }

    public async Task<Result<TransferSummary>> UpdateAsync(Guid transferId, SaveTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transfer = await db.Transfers.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
        if (transfer is null)
        {
            return Error.NotFound("transfer", transferId);
        }

        if (transfer.Status != "draft")
        {
            return Error.Conflict("transfer.not_draft", "Only a draft transfer can be edited.").WithWhy(("status", transfer.Status));
        }

        if (transfer.CompanyId != request.CompanyId)
        {
            return Error.Conflict("transfer.company_locked", "A transfer cannot move to another company.");
        }

        var scope = Validation.Scope(principal, InventoryPermissions.TransferManage, request.CompanyId, request.FromWarehouseId, request.ToWarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var applied = await ApplyAsync(transfer, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(transfer, cancellationToken);
    }

    public async Task<Result<TransferSummary>> CancelAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var transfer = await db.Transfers.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
        if (transfer is null)
        {
            return Error.NotFound("transfer", transferId);
        }

        if (transfer.Status != "draft")
        {
            return Error.Conflict("transfer.not_draft", "Only a draft transfer can be cancelled; a shipped one is received (in full or with a shortage).").WithWhy(("status", transfer.Status));
        }

        transfer.Status = "cancelled";
        transfer.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(transfer, cancellationToken);
    }

    /// <summary>Ships the outstanding quantities (or the ones given) from the source to the transit warehouse and numbers the transfer.</summary>
    public async Task<Result<TransferSummary>> ShipAsync(Guid transferId, ShipTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transfer = await db.Transfers.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
        if (transfer is null)
        {
            return Error.NotFound("transfer", transferId);
        }

        if (transfer.Status != "draft")
        {
            return Error.Conflict("transfer.not_draft", "The transfer has already shipped.").WithWhy(("status", transfer.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.TransferShip, transfer.CompanyId, transfer.FromWarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var transit = await TransitWarehouseAsync(transfer, cancellationToken);
        if (transit.IsFailure)
        {
            return transit.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(transfer.CompanyId), cancellationToken))!;
        var date = request.ShipDate ?? clock.TodayIn(company.TimeZone);
        var quantities = request.Lines?.ToDictionary(static l => l.LineId, static l => l.Quantity) ?? new Dictionary<Guid, decimal>();
        var lines = new List<StockLine>();
        foreach (var line in transfer.Lines.OrderBy(static l => l.LineNo))
        {
            var quantity = quantities.TryGetValue(line.Id, out var q) ? q : line.QtyRequested;
            if (quantity < 0m || quantity > line.QtyRequested)
            {
                return Error.Validation("transfer.ship_quantity_invalid", "A line ships between nothing and its requested quantity.").WithWhy(("lineNo", line.LineNo), ("requested", line.QtyRequested), ("quantity", quantity));
            }

            if (quantity == 0m)
            {
                continue;
            }

            var pair = Guid.CreateVersion7();
            lines.Add(new StockLine(line.ItemId, StockEntryTypes.TransferOut, quantity, transfer.FromWarehouseId, line.UomId, line.VariantId, line.FromBinId, SourceLineId: line.Id, TransferPairId: pair));
            lines.Add(new StockLine(line.ItemId, StockEntryTypes.TransferIn, quantity, transit.Value.Id, line.UomId, line.VariantId, null, SourceLineId: line.Id, TransferPairId: pair));
            line.QtyShipped = quantity;
        }

        if (lines.Count == 0)
        {
            return Error.Validation("transfer.nothing_to_ship", "Every line ships nothing.");
        }

        var posted = await posting.PostAsync(new StockPostingRequest(transfer.CompanyId, date, DocumentType, transfer.Id, lines, $"{DocumentType}:{transfer.Id:N}:ship"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (transfer.Number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "TRF-" + company.Code, "TRF-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, date, transfer.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            transfer.Number = allocated.Value.Text;
        }

        transfer.TransitWarehouseId = transit.Value.Id;
        transfer.ShipDate = date;
        transfer.ShipPostingId = posted.Value.PostingId;
        transfer.Status = "shipped";
        transfer.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("stock_transfer", transfer.Id, transfer.Number, "shipped", After: new { date, postingId = posted.Value.PostingId, lines = transfer.Lines.Select(static l => new { l.LineNo, shipped = l.QtyShipped }) }, CompanyId: transfer.CompanyId), cancellationToken);
        return await MapAsync(transfer, cancellationToken);
    }

    /// <summary>Receives outstanding quantities (or the ones given) from transit into the destination; the transfer stays partially received until everything shipped has arrived.</summary>
    public async Task<Result<TransferSummary>> ReceiveAsync(Guid transferId, ReceiveTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transfer = await db.Transfers.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
        if (transfer is null)
        {
            return Error.NotFound("transfer", transferId);
        }

        if (transfer.Status is not ("shipped" or "partially_received"))
        {
            return Error.Conflict("transfer.not_shipped", "Only a shipped transfer can be received.").WithWhy(("status", transfer.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.TransferReceive, transfer.CompanyId, transfer.ToWarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(transfer.CompanyId), cancellationToken))!;
        var date = request.ReceiveDate ?? clock.TodayIn(company.TimeZone);
        if (transfer.ShipDate is { } shipped && date < shipped)
        {
            return Error.Validation("transfer.receive_before_ship", "A transfer cannot be received before it shipped.").WithWhy(("shipDate", shipped), ("receiveDate", date));
        }

        var requested = request.Lines?.ToDictionary(static l => l.LineId) ?? new Dictionary<Guid, TransferQuantityRequest>();
        var lines = new List<StockLine>();
        foreach (var line in transfer.Lines.OrderBy(static l => l.LineNo))
        {
            var outstanding = line.QtyShipped - line.QtyReceived;
            var quantity = requested.TryGetValue(line.Id, out var r) ? r.Quantity : outstanding;
            if (quantity < 0m || quantity > outstanding)
            {
                return Error.Validation("transfer.receive_quantity_invalid", "A line receives between nothing and what is still in transit.").WithWhy(("lineNo", line.LineNo), ("outstanding", outstanding), ("quantity", quantity));
            }

            if (quantity == 0m)
            {
                continue;
            }

            var toBin = requested.TryGetValue(line.Id, out var rq) && rq.ToBinId is not null ? rq.ToBinId : line.ToBinId;
            var pair = Guid.CreateVersion7();
            lines.Add(new StockLine(line.ItemId, StockEntryTypes.TransferOut, quantity, transfer.TransitWarehouseId!.Value, line.UomId, line.VariantId, null, SourceLineId: line.Id, TransferPairId: pair));
            lines.Add(new StockLine(line.ItemId, StockEntryTypes.TransferIn, quantity, transfer.ToWarehouseId, line.UomId, line.VariantId, toBin, SourceLineId: line.Id, TransferPairId: pair));
            line.QtyReceived += quantity;
            line.ToBinId = toBin;
        }

        if (lines.Count == 0)
        {
            return Error.Validation("transfer.nothing_to_receive", "Nothing is left in transit for this transfer.");
        }

        var receipts = await db.Postings.CountAsync(p => p.SourceDocumentType == DocumentType && p.SourceDocumentId == transfer.Id, cancellationToken);
        var posted = await posting.PostAsync(new StockPostingRequest(transfer.CompanyId, date, DocumentType, transfer.Id, lines, $"{DocumentType}:{transfer.Id:N}:receive:{receipts}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        transfer.ReceiveDate = date;
        transfer.ReceivePostingId = posted.Value.PostingId;
        transfer.Status = transfer.Lines.All(static l => l.QtyReceived >= l.QtyShipped) ? "received" : "partially_received";
        transfer.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("stock_transfer", transfer.Id, transfer.Number ?? transfer.Id.ToString("N"), "received", After: new { date, postingId = posted.Value.PostingId, status = transfer.Status, lines = transfer.Lines.Select(static l => new { l.LineNo, received = l.QtyReceived }) }, CompanyId: transfer.CompanyId), cancellationToken);
        return await MapAsync(transfer, cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Result> ApplyAsync(Transfer transfer, SaveTransferRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.FromWarehouseId == request.ToWarehouseId)
        {
            return Error.Validation("transfer.same_warehouse", "The source and destination differ.");
        }

        foreach (var (id, role) in new[] { (request.FromWarehouseId, "from"), (request.ToWarehouseId, "to") })
        {
            var warehouse = await warehouses.FindAsync(id, cancellationToken);
            if (warehouse is null || warehouse.CompanyId != company.Id.Value || !warehouse.IsActive)
            {
                return Error.Validation($"transfer.{role}_warehouse_invalid", "The warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", id));
            }

            if (warehouse.Kind == "in_transit")
            {
                return Error.Validation($"transfer.{role}_warehouse_invalid", "A transfer does not start or end in an in-transit warehouse.").WithWhy(("warehouse", warehouse.Code));
            }
        }

        if (request.TransitWarehouseId is { } transitId)
        {
            var transit = await warehouses.FindAsync(transitId, cancellationToken);
            if (transit is null || transit.CompanyId != company.Id.Value || transit.Kind != "in_transit" || !transit.IsActive)
            {
                return Error.Validation("transfer.transit_warehouse_invalid", "The transit warehouse must be an active in-transit warehouse of the company.").WithWhy(("warehouseId", transitId));
            }
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("transfer.lines_required", "A transfer has at least one line.");
        }

        var validated = await customFields.ValidateAsync("stock_transfer", request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var byId = (await uoms.ListAsync(cancellationToken)).ToDictionary(static u => u.Id);
        var lines = new List<TransferLine>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            var code = line.ItemCode?.Trim();
            var item = line.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : code is null ? null : await items.FindByCodeAsync(code, cancellationToken);
            if (item is null)
            {
                return Error.Validation("transfer.item_unknown", "The item does not exist.").WithWhy(("item", line.ItemCode ?? line.ItemId?.ToString()));
            }

            if (item.Type is not ("stock" or "assembly"))
            {
                return Error.Validation("transfer.item_not_stocked", "Only stock and assembly items move.").WithWhy(("item", item.Code), ("type", item.Type));
            }

            if (line.Quantity <= 0m)
            {
                return Error.Validation("transfer.quantity_invalid", "Quantities are positive.").WithWhy(("item", item.Code));
            }

            var units = await items.UomsAsync(item.Id, cancellationToken);
            ItemUomInfo? unit;
            if (line.UomId is null && string.IsNullOrWhiteSpace(line.Uom))
            {
                unit = units.First(static u => u.IsBase);
            }
            else
            {
                var wanted = line.Uom?.Trim().ToUpperInvariant();
                unit = units.FirstOrDefault(u => line.UomId is { } uid ? u.UomId == uid : u.UomCode == wanted);
                if (unit is null)
                {
                    return Error.Validation("transfer.uom_not_item_uom", "The unit must be one of the item's units.").WithWhy(("item", item.Code), ("uom", line.Uom ?? line.UomId?.ToString()), ("itemUoms", units.Select(static u => u.UomCode)));
                }
            }

            var exact = await items.ToBaseAsync(item.Id, unit.UomId, line.Quantity, cancellationToken);
            if (exact.IsFailure)
            {
                return exact.Error!;
            }

            if (line.VariantId is { } variantId)
            {
                var variant = await items.FindVariantAsync(variantId, cancellationToken);
                if (variant is null || variant.ItemId != item.Id)
                {
                    return Error.Validation("transfer.variant_invalid", "The variant must belong to the item.").WithWhy(("item", item.Code), ("variantId", variantId));
                }
            }
            else if (item.HasVariants)
            {
                return Error.Validation("transfer.variant_required", "The item has variants; name the one that moves.").WithWhy(("item", item.Code));
            }

            _ = byId;
            lines.Add(new TransferLine { Id = Guid.CreateVersion7(), TransferId = transfer.Id, LineNo = ++lineNo, ItemId = item.Id, VariantId = line.VariantId, QtyRequested = line.Quantity, UomId = unit.UomId, FromBinId = line.FromBinId, ToBinId = line.ToBinId });
        }

        transfer.FromWarehouseId = request.FromWarehouseId;
        transfer.ToWarehouseId = request.ToWarehouseId;
        transfer.TransitWarehouseId = request.TransitWarehouseId;
        transfer.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        transfer.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        transfer.CustomFields = validated.Value;
        transfer.Lines.Clear();
        transfer.Lines.AddRange(lines);
        transfer.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<Result<WarehouseInfo>> TransitWarehouseAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        if (transfer.TransitWarehouseId is { } id)
        {
            var chosen = await warehouses.FindAsync(id, cancellationToken);
            return chosen is { Kind: "in_transit", IsActive: true } ? chosen : Error.Validation("transfer.transit_warehouse_invalid", "The transit warehouse is no longer an active in-transit warehouse.").WithWhy(("warehouseId", id));
        }

        var candidates = (await warehouses.ListAsync(transfer.CompanyId, cancellationToken)).Where(static w => w.Kind == "in_transit" && w.IsActive).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => Error.Conflict("transfer.transit_warehouse_required", "The company has no in-transit warehouse; create one (kind in_transit) or name it on the transfer."),
            _ => Error.Conflict("transfer.transit_warehouse_ambiguous", "The company has several in-transit warehouses; name the one to use on the transfer.").WithWhy(("candidates", candidates.Select(static c => c.Code))),
        };
    }

    private async Task<TransferSummary> MapAsync(Transfer t, CancellationToken cancellationToken)
    {
        var codes = new Dictionary<Guid, string>();
        foreach (var id in new[] { t.FromWarehouseId, t.ToWarehouseId, t.TransitWarehouseId ?? Guid.Empty }.Where(static id => id != Guid.Empty).Distinct())
        {
            codes[id] = (await warehouses.FindAsync(id, cancellationToken))?.Code ?? string.Empty;
        }

        var lines = new List<TransferLineSummary>(t.Lines.Count);
        foreach (var l in t.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ItemId, cancellationToken))!;
            var units = await items.UomsAsync(l.ItemId, cancellationToken);
            var unit = units.First(u => u.UomId == l.UomId);
            var variant = l.VariantId is { } v ? await items.FindVariantAsync(v, cancellationToken) : null;
            var baseQuantity = ItemUomMath.ToBase(l.QtyRequested, unit.Numerator, unit.Denominator, item.BasePrecision);
            lines.Add(new TransferLineSummary(l.Id, l.LineNo, item.Id, item.Code, item.Name.Values, l.VariantId, variant?.Sku, ItemUomMath.Normalize(l.QtyRequested), ItemUomMath.Normalize(l.QtyShipped), ItemUomMath.Normalize(l.QtyReceived),
                l.UomId, unit.UomCode, baseQuantity.IsSuccess ? ItemUomMath.Normalize(baseQuantity.Value) : 0m, item.BaseUomCode, l.FromBinId, l.ToBinId));
        }

        return new TransferSummary(t.Id, t.CompanyId, t.Number ?? DraftIdentifiers.For(t.Id), t.Status, t.FromWarehouseId, codes[t.FromWarehouseId], t.ToWarehouseId, codes[t.ToWarehouseId], t.TransitWarehouseId, t.TransitWarehouseId is { } tw ? codes.GetValueOrDefault(tw) : null,
            t.ShipDate, t.ReceiveDate, t.ShipPostingId, t.ReceivePostingId, t.Reference, t.Notes, JsonDocument.Parse(t.CustomFields).RootElement.Clone(), lines, t.UpdatedAt);
    }
}

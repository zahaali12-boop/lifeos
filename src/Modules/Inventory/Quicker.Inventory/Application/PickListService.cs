using System.Text.Json;
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
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Inventory.Application;

/// <summary>What a picker confirms for one line: the quantity taken, and where from when it differs from the plan
/// (another bin, another lot); a serial-tracked item names the serials scanned. Less than planned is a short pick and
/// says why; zero is "nothing there".</summary>
public sealed record PickLineRequest(decimal Quantity, Guid? BinId = null, string? LotNumber = null, IReadOnlyList<string>? SerialNumbers = null, string? ShortReason = null);

public sealed record AssignPickListRequest(Guid? MembershipId = null);

public sealed record CancelPickListRequest(string Reason);

/// <summary>A pick list in a work queue: its progress without its lines.</summary>
public sealed record PickListRow(
    Guid Id,
    Guid CompanyId,
    Guid WarehouseId,
    string WarehouseCode,
    string Number,
    string SourceDocumentType,
    Guid SourceDocumentId,
    string SourceNumber,
    string Status,
    Guid? AssignedTo,
    int LinesTotal,
    int LinesDone,
    decimal QtyToPick,
    decimal QtyPicked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? AssignedName = null);

/// <summary>
/// Pick lists (roadmap 5.5b, DOMAIN_MODEL §9, A-154). The planner locates a document's lines in the warehouse: lots
/// first expiry first out (first received first out for an item that does not use FEFO), then the bins in their pick
/// sequence so the list reads as one walk through the warehouse, serials oldest received first; blocked or expired
/// lots, serials not in stock, bins not meant for picking (receiving, quarantine, returns) and stock that another live
/// pick list already claims are left alone, and another document's reservation on a bin or lot is respected, while the
/// document's own reservations count as available to it. Releasing keeps the plan as a numbered list under a
/// per-item lock, so two releases never plan the same shelf twice; the picker confirms each line (or short-picks it
/// with a reason), and the source document posts what was picked and closes the list. No stock moves here.
/// </summary>
public sealed class PickListService(
    InventoryDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IMemberDirectory members,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock) : IPickLists
{
    public const string DocumentType = "stock_pick_list";

    /// <summary>Bins stock is picked from; receiving, quarantine and returns bins hold stock not yet fit to ship.</summary>
    private static readonly string[] PickableBinKinds = ["storage", "shipping"];

    private static readonly string[] LiveStatuses = [.. PickListStatuses.Live];

    // ------------------------------------------------------------------ planning (IPickLists)

    public async Task<Result<IReadOnlyList<PickPlanLine>>> PlanAsync(PickPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warehouse = await WarehouseForAsync(request, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error!;
        }

        var planned = await PlanLinesAsync(request, warehouse.Value, excludePickListId: null, cancellationToken);
        return planned.IsFailure ? planned.Error! : Result<IReadOnlyList<PickPlanLine>>.Success(planned.Value.Select(static p => p.Line).ToList());
    }

    public async Task<Result<PickListInfo>> ReleaseAsync(PickReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceDocumentType) || request.SourceDocumentId == Guid.Empty)
        {
            return Error.Validation("pick.source_required", "A pick list names the document it picks for.");
        }

        var plan = request.Plan;
        var warehouse = await WarehouseForAsync(plan, cancellationToken);
        if (warehouse.IsFailure)
        {
            return warehouse.Error!;
        }

        var sourceType = request.SourceDocumentType.Trim();
        if (await db.PickLists.AnyAsync(p => p.SourceDocumentType == sourceType && p.SourceDocumentId == request.SourceDocumentId && p.Status != PickListStatuses.Cancelled, cancellationToken))
        {
            return Error.Conflict("pick.already_released", "The document already has a pick list; cancel it before releasing again.").WithWhy(("source", request.SourceNumber));
        }

        // One release at a time per item and warehouse: the plan reads what other live lists claim, so two releases
        // planning at once would both see the same shelf free. Taken in key order so two releases never deadlock.
        var uow = unitOfWork.Current;
        foreach (var key in plan.Lines.Select(l => $"pick:{uow.Context.TenantId.Value:N}:{plan.WarehouseId:N}:{l.ItemId:N}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await uow.Connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", new { key }, uow.Transaction, cancellationToken: cancellationToken));
        }

        var planned = await PlanLinesAsync(plan, warehouse.Value, excludePickListId: null, cancellationToken);
        if (planned.IsFailure)
        {
            return planned.Error!;
        }

        var unlocated = planned.Value.Where(static p => p.Line.Located < p.Line.Requested).ToList();
        if (unlocated.Count > 0)
        {
            return Error.Conflict("pick.not_enough_located", "Not all of the quantity can be located in the warehouse; ship less, or wait for stock.")
                .WithWhy(("warehouse", warehouse.Value.Code), ("lines", unlocated.Select(static p => new { sourceLineId = p.Line.SourceLineId, item = p.Item.Code, requested = p.Line.Requested, located = p.Line.Located }).ToList()));
        }

        var list = new PickList
        {
            Id = Guid.CreateVersion7(),
            CompanyId = plan.CompanyId,
            WarehouseId = plan.WarehouseId,
            SourceDocumentType = sourceType,
            SourceDocumentId = request.SourceDocumentId,
            SourceNumber = request.SourceNumber.Trim(),
            ReservedForType = plan.ReservedForDocumentType.Trim(),
            ReservedForId = plan.ReservedForDocumentId,
            Status = PickListStatuses.Released,
            ReleasedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

        // The walking order: bin pick sequence, then bin, then item, then lot; one line per place to go.
        var stops = planned.Value
            .SelectMany(p => p.Stops.Select(s => (Planned: p, Stop: s)))
            .OrderBy(static x => x.Stop.PickSequence).ThenBy(static x => x.Stop.Allocation.BinCode, StringComparer.Ordinal)
            .ThenBy(static x => x.Planned.Item.Code, StringComparer.Ordinal).ThenBy(static x => x.Stop.Allocation.ExpiresOn ?? DateOnly.MaxValue).ThenBy(static x => x.Stop.Allocation.LotNumber, StringComparer.Ordinal)
            .ToList();
        var lineNo = 0;
        foreach (var (p, stop) in stops)
        {
            list.Lines.Add(new PickLine
            {
                Id = Guid.CreateVersion7(),
                PickListId = list.Id,
                LineNo = ++lineNo,
                SourceLineId = p.Line.SourceLineId,
                ItemId = p.Item.Id,
                VariantId = p.VariantId,
                BinId = stop.Allocation.BinId,
                LotId = stop.Allocation.LotId,
                SerialNumbers = JsonSerializer.Serialize(stop.Allocation.SerialNumbers),
                QtyToPick = stop.Allocation.Quantity,
            });
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(plan.CompanyId), "PCK", "PCK-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(plan.CompanyId), null, plan.AsOf, list.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        list.Number = number.Value.Text;
        db.PickLists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, AuditActions.Created, After: new { source = new { type = list.SourceDocumentType, number = list.SourceNumber }, warehouse = warehouse.Value.Code, lines = list.Lines.Count }, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    public async Task<PickListInfo?> FindAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        var list = await db.PickLists.AsNoTracking().Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == pickListId, cancellationToken);
        return list is null ? null : await InfoAsync(list, cancellationToken);
    }

    public async Task<Result<PickListInfo>> CancelAsync(Guid pickListId, string reason, CancellationToken cancellationToken = default)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is null)
        {
            return Error.Validation("pick.reason_required", "A cancellation names its reason.");
        }

        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        if (!LiveStatuses.Contains(list.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("pick.not_open", $"The pick list is {list.Status}.").WithWhy(("pickList", list.Number), ("status", list.Status));
        }

        list.Status = PickListStatuses.Cancelled;
        list.CancelledAt = clock.UtcNow;
        list.CancelReason = why;
        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, AuditActions.StateChanged, After: new { status = list.Status }, Reason: why, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    public async Task<Result<PickListInfo>> CloseAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        if (list.Status != PickListStatuses.Picked)
        {
            return Error.Conflict("pick.not_picked", "Only a pick list with every line picked or short-picked is closed.").WithWhy(("pickList", list.Number), ("status", list.Status), ("open", list.Lines.Count(static l => l.Status == PickLineStatuses.Open)));
        }

        list.Status = PickListStatuses.Closed;
        list.ClosedAt = clock.UtcNow;
        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, AuditActions.StateChanged, After: new { status = list.Status }, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    // ------------------------------------------------------------------ the work queue and the handheld

    public async Task<IReadOnlyList<PickListRow>> ListAsync(Guid? companyId, Guid? warehouseId, string? status, Guid? assignedTo, bool mine, CancellationToken cancellationToken)
    {
        var query = db.PickLists.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(p => p.CompanyId == c);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(p => p.WarehouseId == w);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var st = status.Trim();
            query = st == "live" ? query.Where(p => LiveStatuses.Contains(p.Status)) : query.Where(p => p.Status == st);
        }

        var me = principal.Principal?.MembershipId.Value;
        if (mine)
        {
            query = query.Where(p => p.AssignedTo == me);
        }
        else if (assignedTo is { } a)
        {
            query = query.Where(p => p.AssignedTo == a);
        }

        var lists = await query.OrderByDescending(static p => p.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var ids = lists.Select(static p => p.Id).ToList();
        var progress = await db.PickLines.AsNoTracking().Where(l => ids.Contains(l.PickListId))
            .GroupBy(static l => l.PickListId)
            .Select(static g => new { Id = g.Key, Total = g.Count(), Done = g.Count(static l => l.Status != PickLineStatuses.Open), ToPick = g.Sum(static l => l.QtyToPick), Picked = g.Sum(static l => l.QtyPicked) })
            .ToDictionaryAsync(static g => g.Id, cancellationToken);
        var codes = new Dictionary<Guid, string>();
        var names = new Dictionary<Guid, string?>();
        var rows = new List<PickListRow>(lists.Count);
        foreach (var p in lists)
        {
            if (!codes.TryGetValue(p.WarehouseId, out var code))
            {
                code = (await warehouses.FindAsync(p.WarehouseId, cancellationToken))?.Code ?? string.Empty;
                codes[p.WarehouseId] = code;
            }

            var g = progress.GetValueOrDefault(p.Id);
            rows.Add(new PickListRow(p.Id, p.CompanyId, p.WarehouseId, code, p.Number, p.SourceDocumentType, p.SourceDocumentId, p.SourceNumber, p.Status, p.AssignedTo,
                g?.Total ?? 0, g?.Done ?? 0, ItemUomMath.Normalize(g?.ToPick ?? 0m), ItemUomMath.Normalize(g?.Picked ?? 0m), p.CreatedAt, p.UpdatedAt, await NameAsync(p.AssignedTo, names, cancellationToken)));
        }

        return rows;
    }

    /// <summary>Takes an unassigned list (or confirms one already assigned to the caller).</summary>
    public async Task<Result<PickListInfo>> ClaimAsync(Guid pickListId, CancellationToken cancellationToken)
    {
        var me = principal.Principal?.MembershipId.Value;
        if (me is null)
        {
            return Error.Forbidden("pick.member_required", "Only a member of the workspace takes a pick list.");
        }

        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        if (!LiveStatuses.Contains(list.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("pick.not_open", $"The pick list is {list.Status}.").WithWhy(("pickList", list.Number), ("status", list.Status));
        }

        if (list.AssignedTo is { } other && other != me)
        {
            return Error.Conflict("pick.assigned_to_other", "Another picker has this list.").WithWhy(("pickList", list.Number), ("assignedTo", other));
        }

        if (list.AssignedTo is null)
        {
            list.AssignedTo = me;
            list.AssignedAt = clock.UtcNow;
            list.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, AuditActions.Updated, After: new { assignedTo = me }, CompanyId: list.CompanyId), cancellationToken);
        }

        return await InfoAsync(list, cancellationToken);
    }

    /// <summary>Hands a list to a picker, or back to the queue (no member).</summary>
    public async Task<Result<PickListInfo>> AssignAsync(Guid pickListId, AssignPickListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        if (!LiveStatuses.Contains(list.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("pick.not_open", $"The pick list is {list.Status}.").WithWhy(("pickList", list.Number), ("status", list.Status));
        }

        if (request.MembershipId is { } membershipId)
        {
            var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
            if (member is null || !member.IsActive)
            {
                return Error.Validation("pick.member_invalid", "The picker must be an active member of the workspace.").WithWhy(("membershipId", membershipId));
            }
        }

        var before = list.AssignedTo;
        list.AssignedTo = request.MembershipId;
        list.AssignedAt = request.MembershipId is null ? null : clock.UtcNow;
        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, AuditActions.Updated, Before: new { assignedTo = before }, After: new { assignedTo = list.AssignedTo }, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    /// <summary>
    /// Confirms one line: the quantity and where it came from. The same call again replaces the confirmation (a
    /// handheld that retries, or a picker correcting a line). An unassigned list becomes the picker's; a list assigned to
    /// someone else is only picked by a pick manager. A bin, lot or serials other than planned are checked against what
    /// is there now, net of every other live list's claims; the stock engine checks once more when the document posts.
    /// </summary>
    public async Task<Result<PickListInfo>> PickAsync(Guid pickListId, Guid lineId, PickLineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var me = principal.Principal?.MembershipId.Value;
        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        var access = MayWork(list, me);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var line = list.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("pick_line", lineId);
        }

        var item = await items.FindAsync(line.ItemId, cancellationToken);
        var warehouse = await warehouses.FindAsync(list.WarehouseId, cancellationToken);
        if (item is null || warehouse is null)
        {
            return Error.NotFound(item is null ? "item" : "warehouse", item is null ? line.ItemId : list.WarehouseId);
        }

        if (request.Quantity < 0m || request.Quantity > line.QtyToPick)
        {
            return Error.Validation("pick.quantity_invalid", "A line is picked for at most what it asks, and never less than nothing.").WithWhy(("lineNo", line.LineNo), ("qtyToPick", N(line.QtyToPick)), ("quantity", request.Quantity));
        }

        var shortReason = string.IsNullOrWhiteSpace(request.ShortReason) ? null : request.ShortReason.Trim();
        if (request.Quantity < line.QtyToPick && shortReason is null)
        {
            return Error.Validation("pick.short_reason_required", "A short pick says why (nothing on the shelf, damaged, ...).").WithWhy(("lineNo", line.LineNo), ("qtyToPick", N(line.QtyToPick)), ("quantity", request.Quantity));
        }

        var lotTracked = item.Tracking is "lot" or "lot_and_serial";
        var serialTracked = item.Tracking is "serial" or "lot_and_serial";
        if (!lotTracked && !string.IsNullOrWhiteSpace(request.LotNumber))
        {
            return Error.Validation("pick.tracking_not_used", "The item is not lot-tracked; the pick names a lot.").WithWhy(("item", item.Code));
        }

        if (!serialTracked && request.SerialNumbers is { Count: > 0 })
        {
            return Error.Validation("pick.tracking_not_used", "The item is not serialised; the pick names serial numbers.").WithWhy(("item", item.Code));
        }

        if (request.Quantity == 0m)
        {
            Record(line, 0m, null, null, [], shortReason, me);
        }
        else
        {
            var located = await LocateAsync(list, line, item, warehouse, request, cancellationToken);
            if (located.IsFailure)
            {
                return located.Error!;
            }

            var (binId, lot, serials) = located.Value;
            Record(line, request.Quantity, binId, lot?.Id, serials, request.Quantity < line.QtyToPick ? shortReason : null, me);
        }

        if (list.AssignedTo is null && me is not null)
        {
            list.AssignedTo = me;
            list.AssignedAt = clock.UtcNow;
        }

        Progress(list);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, "picked", After: new { line = line.LineNo, item = item.Code, quantity = N(line.QtyPicked), status = line.Status, binId = line.PickedBinId, lotId = line.PickedLotId, serials = Serials(line.PickedSerialNumbers), shortReason = line.ShortReason, listStatus = list.Status }, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    /// <summary>Takes a confirmation back: the line is open again.</summary>
    public async Task<Result<PickListInfo>> ResetLineAsync(Guid pickListId, Guid lineId, CancellationToken cancellationToken)
    {
        var me = principal.Principal?.MembershipId.Value;
        var list = await LockedAsync(pickListId, cancellationToken);
        if (list is null)
        {
            return Error.NotFound("pick_list", pickListId);
        }

        var access = MayWork(list, me);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var line = list.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("pick_line", lineId);
        }

        line.Status = PickLineStatuses.Open;
        line.QtyPicked = 0m;
        line.PickedBinId = null;
        line.PickedLotId = null;
        line.PickedSerialNumbers = "[]";
        line.ShortReason = null;
        line.PickedBy = null;
        line.PickedAt = null;
        Progress(list);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, list.Id, list.Number, "unpicked", After: new { line = line.LineNo, listStatus = list.Status }, CompanyId: list.CompanyId), cancellationToken);
        return await InfoAsync(list, cancellationToken);
    }

    public async Task<Result<PickListInfo>> GetAsync(Guid pickListId, CancellationToken cancellationToken)
    {
        var info = await FindAsync(pickListId, cancellationToken);
        return info is null ? Error.NotFound("pick_list", pickListId) : info;
    }

    // ------------------------------------------------------------------ internals: planning

    private sealed record Stop(PickAllocation Allocation, int PickSequence);

    private sealed record PlannedLine(PickPlanLine Line, ItemInfo Item, Guid? VariantId, IReadOnlyList<Stop> Stops);

    private sealed class CandidateRow
    {
        public Guid BinId { get; set; }

        public string? BinCode { get; set; }

        public int PickSequence { get; set; }

        public string? BinKind { get; set; }

        public bool? BinActive { get; set; }

        public Guid LotId { get; set; }

        public string? LotNumber { get; set; }

        public DateOnly? ExpiresOn { get; set; }

        public string? LotStatus { get; set; }

        public Guid SerialId { get; set; }

        public string? SerialNumber { get; set; }

        public string? SerialStatus { get; set; }

        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal QualityHold { get; set; }

        public decimal OwnReserved { get; set; }
    }

    /// <summary>What live pick lists (other than one) claim of an item in a warehouse: quantities per variant, bin and lot, and serial numbers.</summary>
    private sealed record Claims(Dictionary<(Guid Variant, Guid Bin, Guid Lot), decimal> Quantities, HashSet<string> Serials);

    private async Task<Result<WarehouseInfo>> WarehouseForAsync(PickPlanRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("pick.lines_required", "There is at least one line to pick.");
        }

        if (string.IsNullOrWhiteSpace(request.ReservedForDocumentType))
        {
            return Error.Validation("pick.reserved_for_required", "A pick names the document whose reservations hold its stock.");
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != request.CompanyId)
        {
            return Error.Validation("pick.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        return warehouse;
    }

    private async Task<Result<List<PlannedLine>>> PlanLinesAsync(PickPlanRequest request, WarehouseInfo warehouse, Guid? excludePickListId, CancellationToken cancellationToken)
    {
        // What this request itself takes line by line, so two lines of the same item never plan the same stock.
        var takenHere = new Dictionary<(Guid Variant, Guid Bin, Guid Lot), decimal>();
        var serialsHere = new HashSet<string>(StringComparer.Ordinal);
        var claimsByItem = new Dictionary<Guid, Claims>();
        var result = new List<PlannedLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var item = await items.FindAsync(line.ItemId, cancellationToken);
            if (item is null)
            {
                return Error.NotFound("item", line.ItemId);
            }

            if (item.Type is not ("stock" or "assembly"))
            {
                return Error.Validation("pick.item_not_stocked", "Only stock and assembly items are picked.").WithWhy(("item", item.Code), ("type", item.Type));
            }

            if (line.VariantId is null && item.HasVariants)
            {
                return Error.Validation("pick.variant_required", "The item has variants; the line names the one to pick.").WithWhy(("item", item.Code));
            }

            if (line.Quantity <= 0m)
            {
                return Error.Validation("pick.quantity_invalid", "Quantities are positive.").WithWhy(("item", item.Code), ("quantity", line.Quantity));
            }

            var lotTracked = item.Tracking is "lot" or "lot_and_serial";
            var serialTracked = item.Tracking is "serial" or "lot_and_serial";
            if (serialTracked && line.Quantity != decimal.Truncate(line.Quantity))
            {
                return Error.Validation("pick.serial_quantity_not_whole", "A serial-tracked item is picked in whole units.").WithWhy(("item", item.Code), ("quantity", line.Quantity));
            }

            if (line.BinId is { } narrowed)
            {
                var bin = await warehouses.FindBinAsync(narrowed, cancellationToken);
                if (!warehouse.BinsEnabled || bin is null || bin.WarehouseId != warehouse.Id)
                {
                    return Error.Validation("pick.bin_invalid", "The bin must belong to the warehouse, and the warehouse must use bins.").WithWhy(("warehouse", warehouse.Code), ("binId", narrowed));
                }
            }

            if (!warehouse.BinsEnabled && !lotTracked && !serialTracked)
            {
                // One place to take it from: the warehouse itself. The order's reservation already vouches for the
                // quantity and the stock engine checks it again when the document posts.
                var only = new PickAllocation(null, null, null, null, null, [], line.Quantity);
                result.Add(new PlannedLine(new PickPlanLine(line.SourceLineId, line.Quantity, line.Quantity, [only]), item, line.VariantId, [new Stop(only, 0)]));
                continue;
            }

            if (!claimsByItem.TryGetValue(item.Id, out var claims))
            {
                claims = await ClaimsAsync(warehouse.Id, item.Id, excludePickListId, exceptLineId: null, cancellationToken);
                claimsByItem[item.Id] = claims;
            }

            var rows = await CandidatesAsync(request.CompanyId, warehouse.Id, item.Id, line.VariantId, request.ReservedForDocumentType.Trim(), request.ReservedForDocumentId, cancellationToken);
            var eligible = rows.Where(r => Eligible(r, warehouse, lotTracked, serialTracked, request.AsOf, line.BinId)).ToList();
            // "First received" is the creation instant in the id (UUIDv7: its first 48 bits are the millisecond), and
            // within one instant -- the serials or lots of one receipt line -- their own number, since the rest of a
            // UUIDv7 is random and would order a single receipt's serials by chance.
            IEnumerable<CandidateRow> ordered = lotTracked
                ? item.Fefo
                    ? eligible.OrderBy(static r => r.ExpiresOn ?? DateOnly.MaxValue).ThenBy(static r => r.LotNumber, StringComparer.Ordinal).ThenBy(static r => r.PickSequence).ThenBy(static r => r.BinCode, StringComparer.Ordinal).ThenBy(static r => Received(r.SerialId), StringComparer.Ordinal).ThenBy(static r => r.SerialNumber, StringComparer.Ordinal)
                    : eligible.OrderBy(static r => Received(r.LotId), StringComparer.Ordinal).ThenBy(static r => r.LotNumber, StringComparer.Ordinal).ThenBy(static r => r.PickSequence).ThenBy(static r => r.BinCode, StringComparer.Ordinal).ThenBy(static r => Received(r.SerialId), StringComparer.Ordinal).ThenBy(static r => r.SerialNumber, StringComparer.Ordinal)
                : eligible.OrderBy(static r => r.PickSequence).ThenBy(static r => r.BinCode, StringComparer.Ordinal).ThenBy(static r => Received(r.SerialId), StringComparer.Ordinal).ThenBy(static r => r.SerialNumber, StringComparer.Ordinal);

            var variant = line.VariantId ?? Guid.Empty;
            var remaining = line.Quantity;
            var stops = new List<(CandidateRow First, List<string> Serials, decimal Quantity)>();
            foreach (var row in ordered)
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var key = (variant, row.BinId, row.LotId);
                decimal take;
                if (serialTracked)
                {
                    if (row.SerialNumber is null || claims.Serials.Contains(row.SerialNumber) || serialsHere.Contains(row.SerialNumber) || Available(row, 0m) < 1m)
                    {
                        continue;
                    }

                    take = 1m;
                    serialsHere.Add(row.SerialNumber);
                }
                else
                {
                    var available = Available(row, claims.Quantities.GetValueOrDefault(key) + takenHere.GetValueOrDefault(key));
                    if (available <= 0m)
                    {
                        continue;
                    }

                    take = Math.Min(remaining, available);
                }

                takenHere[key] = takenHere.GetValueOrDefault(key) + take;
                remaining -= take;
                var last = stops.Count > 0 ? stops[^1] : default;
                if (stops.Count > 0 && last.First.BinId == row.BinId && last.First.LotId == row.LotId)
                {
                    if (row.SerialNumber is not null)
                    {
                        last.Serials.Add(row.SerialNumber);
                    }

                    stops[^1] = (last.First, last.Serials, last.Quantity + take);
                }
                else
                {
                    stops.Add((row, row.SerialNumber is null ? [] : [row.SerialNumber], take));
                }
            }

            var planned = stops.Select(static s => new Stop(new PickAllocation(
                s.First.BinId == Guid.Empty ? null : s.First.BinId,
                s.First.BinCode,
                s.First.LotId == Guid.Empty ? null : s.First.LotId,
                s.First.LotNumber,
                s.First.ExpiresOn,
                s.Serials,
                ItemUomMath.Normalize(s.Quantity)), s.First.PickSequence)).ToList();
            var located = ItemUomMath.Normalize(line.Quantity - Math.Max(0m, remaining));
            result.Add(new PlannedLine(new PickPlanLine(line.SourceLineId, line.Quantity, located, planned.Select(static s => s.Allocation).ToList()), item, line.VariantId, planned));
        }

        return result;
    }

    /// <summary>The millisecond a UUIDv7 was made, as sortable text.</summary>
    private static string Received(Guid id) => id.ToString("N")[..12];

    private static bool Eligible(CandidateRow row, WarehouseInfo warehouse, bool lotTracked, bool serialTracked, DateOnly asOf, Guid? onlyBin)
    {
        if (warehouse.BinsEnabled)
        {
            if (row.BinId == Guid.Empty || row.BinActive != true || !PickableBinKinds.Contains(row.BinKind, StringComparer.Ordinal))
            {
                return false;
            }

            if (onlyBin is { } bin && row.BinId != bin)
            {
                return false;
            }
        }
        else if (row.BinId != Guid.Empty)
        {
            return false;
        }

        if (lotTracked != (row.LotId != Guid.Empty) || serialTracked != (row.SerialId != Guid.Empty))
        {
            return false;
        }

        if (lotTracked && (row.LotStatus != LotStatuses.Active || (row.ExpiresOn is { } expiry && expiry < asOf)))
        {
            return false;
        }

        return !serialTracked || row.SerialStatus == SerialStatuses.InStock;
    }

    /// <summary>What the document may take from a row: on hand, less quality hold, less what other documents reserved on it, less what is claimed or taken already.</summary>
    private static decimal Available(CandidateRow row, decimal claimed) =>
        row.OnHand - row.QualityHold - Math.Max(0m, row.Reserved - row.OwnReserved) - claimed;

    private async Task<List<CandidateRow>> CandidatesAsync(Guid companyId, Guid warehouseId, Guid itemId, Guid? variantId, string reservedForType, Guid reservedForId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<CandidateRow>(new CommandDefinition("""
            SELECT b.bin_id, bn.code AS bin_code, coalesce(bn.pick_sequence, 0) AS pick_sequence, bn.kind AS bin_kind, bn.is_active AS bin_active,
                   b.lot_id, l.lot_number, l.expires_on, l.status AS lot_status,
                   b.serial_id, s.serial_number, s.status AS serial_status,
                   b.on_hand, b.reserved, b.quality_hold, coalesce(own.reserved, 0) AS own_reserved
            FROM app.inv_stock_balances b
            LEFT JOIN app.inv_bins bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
            LEFT JOIN app.inv_lots l ON l.tenant_id = b.tenant_id AND l.id = b.lot_id
            LEFT JOIN app.inv_serials s ON s.tenant_id = b.tenant_id AND s.id = b.serial_id
            LEFT JOIN LATERAL (
              SELECT sum(r.quantity - r.consumed_quantity) AS reserved
              FROM app.inv_reservations r
              WHERE r.tenant_id = b.tenant_id AND r.status = 'active' AND r.source_document_type = @reservedForType AND r.source_document_id = @reservedForId
                AND r.company_id = b.company_id AND r.item_id = b.item_id AND r.warehouse_id = b.warehouse_id
                AND coalesce(r.variant_id, '00000000-0000-0000-0000-000000000000'::uuid) = b.variant_id
                AND coalesce(r.bin_id, '00000000-0000-0000-0000-000000000000'::uuid) = b.bin_id
                AND coalesce(r.lot_id, '00000000-0000-0000-0000-000000000000'::uuid) = b.lot_id
                AND coalesce(r.serial_id, '00000000-0000-0000-0000-000000000000'::uuid) = b.serial_id
            ) own ON true
            WHERE b.tenant_id = @tenant AND b.company_id = @company AND b.item_id = @item AND b.variant_id = @variant AND b.warehouse_id = @warehouse AND b.on_hand > 0
            """, new { tenant = uow.Context.TenantId.Value, company = companyId, item = itemId, variant = variantId ?? Guid.Empty, warehouse = warehouseId, reservedForType, reservedForId }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<Claims> ClaimsAsync(Guid warehouseId, Guid itemId, Guid? excludePickListId, Guid? exceptLineId, CancellationToken cancellationToken)
    {
        var lines = await (from l in db.PickLines.AsNoTracking()
                           join p in db.PickLists.AsNoTracking() on l.PickListId equals p.Id
                           where p.WarehouseId == warehouseId && l.ItemId == itemId && LiveStatuses.Contains(p.Status)
                                 && (excludePickListId == null || p.Id != excludePickListId) && (exceptLineId == null || l.Id != exceptLineId)
                           select l).ToListAsync(cancellationToken);
        var quantities = new Dictionary<(Guid, Guid, Guid), decimal>();
        var serials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            var open = l.Status == PickLineStatuses.Open;
            var key = (l.VariantId ?? Guid.Empty, (open ? l.BinId : l.PickedBinId) ?? Guid.Empty, (open ? l.LotId : l.PickedLotId) ?? Guid.Empty);
            quantities[key] = quantities.GetValueOrDefault(key) + (open ? l.QtyToPick : l.QtyPicked);
            serials.UnionWith(Serials(open ? l.SerialNumbers : l.PickedSerialNumbers));
        }

        return new Claims(quantities, serials);
    }

    // ------------------------------------------------------------------ internals: picking

    private Result MayWork(PickList list, Guid? me)
    {
        if (!LiveStatuses.Contains(list.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("pick.not_open", $"The pick list is {list.Status}.").WithWhy(("pickList", list.Number), ("status", list.Status));
        }

        if (list.AssignedTo is { } assigned && assigned != me && principal.Principal?.Has(InventoryPermissions.PickManage) != true)
        {
            return Error.Forbidden("pick.assigned_to_other", "The pick list is assigned to another picker.").WithWhy(("pickList", list.Number), ("assignedTo", assigned));
        }

        return Result.Success();
    }

    /// <summary>Checks the bin, lot and serials a pick names (the plan's, unless the picker took them elsewhere) against the stock there now.</summary>
    private async Task<Result<(Guid? BinId, Lot? Lot, IReadOnlyList<string> Serials)>> LocateAsync(PickList list, PickLine line, ItemInfo item, WarehouseInfo warehouse, PickLineRequest request, CancellationToken cancellationToken)
    {
        var lotTracked = item.Tracking is "lot" or "lot_and_serial";
        var serialTracked = item.Tracking is "serial" or "lot_and_serial";
        Guid? binId = null;
        if (warehouse.BinsEnabled)
        {
            binId = request.BinId ?? line.BinId;
            var bin = binId is { } b ? await warehouses.FindBinAsync(b, cancellationToken) : null;
            if (bin is null || bin.WarehouseId != warehouse.Id)
            {
                return Error.Validation("pick.bin_invalid", "The bin must belong to the pick list's warehouse.").WithWhy(("warehouse", warehouse.Code), ("binId", binId));
            }

            if (!bin.IsActive)
            {
                return Error.Conflict("pick.bin_inactive", $"Bin {bin.Code} is inactive.").WithWhy(("bin", bin.Code));
            }
        }
        else if (request.BinId is not null)
        {
            return Error.Validation("pick.bin_not_used", "The warehouse does not use bins.").WithWhy(("warehouse", warehouse.Code));
        }

        Lot? lot = null;
        if (lotTracked)
        {
            var number = request.LotNumber?.Trim();
            lot = string.IsNullOrEmpty(number)
                ? line.LotId is { } planned ? await db.Lots.SingleOrDefaultAsync(l => l.Id == planned, cancellationToken) : null
                : await db.Lots.SingleOrDefaultAsync(l => l.ItemId == item.Id && l.LotNumber == number, cancellationToken);
            if (lot is null)
            {
                return Error.Validation("pick.lot_unknown", "The lot does not exist for this item.").WithWhy(("item", item.Code), ("lotNumber", number));
            }

            var company = await companies.FindAsync(new CompanyId(list.CompanyId), cancellationToken);
            var today = clock.TodayIn(company?.TimeZone ?? "UTC");
            if (lot.Status != LotStatuses.Active)
            {
                return Error.Conflict("pick.lot_blocked", $"Lot {lot.LotNumber} is {lot.Status}.").WithWhy(("item", item.Code), ("lotNumber", lot.LotNumber), ("status", lot.Status));
            }

            if (lot.ExpiresOn is { } expiry && expiry < today)
            {
                return Error.Conflict("pick.lot_expired", $"Lot {lot.LotNumber} expired on {expiry:yyyy-MM-dd}.").WithWhy(("item", item.Code), ("lotNumber", lot.LotNumber), ("expiresOn", expiry));
            }
        }

        var others = await ClaimsAsync(warehouse.Id, item.Id, excludePickListId: null, exceptLineId: line.Id, cancellationToken);
        if (serialTracked)
        {
            var numbers = (request.SerialNumbers ?? (request.Quantity == line.QtyToPick ? Serials(line.SerialNumbers) : []))
                .Select(static n => n.Trim()).Where(static n => n.Length > 0).ToList();
            if (numbers.Count != request.Quantity)
            {
                return Error.Validation("pick.serial_count", "A serial-tracked item names one serial per unit picked.").WithWhy(("item", item.Code), ("quantity", request.Quantity), ("serialNumbers", numbers.Count));
            }

            if (numbers.Distinct(StringComparer.Ordinal).Count() != numbers.Count)
            {
                return Error.Validation("pick.serial_duplicate", "A serial number appears twice.").WithWhy(("item", item.Code), ("serialNumbers", numbers));
            }

            var found = await db.Serials.AsNoTracking().Where(s => s.ItemId == item.Id && numbers.Contains(s.SerialNumber)).ToDictionaryAsync(static s => s.SerialNumber, StringComparer.Ordinal, cancellationToken);
            foreach (var number in numbers)
            {
                var serial = found.GetValueOrDefault(number);
                if (serial is null)
                {
                    return Error.Validation("pick.serial_unknown", $"Serial {number} does not exist for this item.").WithWhy(("item", item.Code), ("serialNumber", number));
                }

                if (serial.Status != SerialStatuses.InStock || serial.CurrentWarehouseId != warehouse.Id || (warehouse.BinsEnabled && serial.CurrentBinId != binId))
                {
                    return Error.Conflict("pick.serial_not_here", $"Serial {number} is not in stock at this location.").WithWhy(("item", item.Code), ("serialNumber", number), ("status", serial.Status), ("warehouseId", serial.CurrentWarehouseId), ("binId", serial.CurrentBinId));
                }

                if (lot is not null && serial.LotId != lot.Id)
                {
                    return Error.Validation("pick.serial_lot_mismatch", $"Serial {number} belongs to another lot.").WithWhy(("item", item.Code), ("serialNumber", number), ("lotNumber", lot.LotNumber));
                }

                if (others.Serials.Contains(number))
                {
                    return Error.Conflict("pick.serial_claimed", $"Serial {number} is on another pick list.").WithWhy(("item", item.Code), ("serialNumber", number));
                }
            }

            return Located(binId, lot, numbers);
        }

        if (!warehouse.BinsEnabled && !lotTracked)
        {
            return Located(binId, lot, []);
        }

        // Is the quantity at this bin and lot, net of everyone else's claims and reservations?
        var rows = await CandidatesAsync(list.CompanyId, warehouse.Id, item.Id, line.VariantId, list.ReservedForType, list.ReservedForId, cancellationToken);
        var row = rows.SingleOrDefault(r => r.BinId == (binId ?? Guid.Empty) && r.LotId == (lot?.Id ?? Guid.Empty) && r.SerialId == Guid.Empty);
        var available = row is null ? 0m : Available(row, others.Quantities.GetValueOrDefault((line.VariantId ?? Guid.Empty, binId ?? Guid.Empty, lot?.Id ?? Guid.Empty)));
        if (available < request.Quantity)
        {
            return Error.Conflict("pick.not_enough_here", "There is not that much free stock at this location.").WithWhy(("item", item.Code), ("binId", binId), ("lotNumber", lot?.LotNumber), ("available", N(Math.Max(0m, available))), ("requested", request.Quantity));
        }

        return Located(binId, lot, []);
    }

    private static Result<(Guid? BinId, Lot? Lot, IReadOnlyList<string> Serials)> Located(Guid? binId, Lot? lot, IReadOnlyList<string> serials) => Result<(Guid? BinId, Lot? Lot, IReadOnlyList<string> Serials)>.Success((binId, lot, serials));

    private void Record(PickLine line, decimal quantity, Guid? binId, Guid? lotId, IReadOnlyList<string> serials, string? shortReason, Guid? me)
    {
        line.QtyPicked = quantity;
        line.PickedBinId = binId;
        line.PickedLotId = lotId;
        line.PickedSerialNumbers = JsonSerializer.Serialize(serials);
        line.Status = quantity == line.QtyToPick ? PickLineStatuses.Picked : PickLineStatuses.Short;
        line.ShortReason = line.Status == PickLineStatuses.Short ? shortReason : null;
        line.PickedBy = me;
        line.PickedAt = clock.UtcNow;
    }

    /// <summary>The list follows its lines: every line done is "picked", any progress "in progress".</summary>
    private void Progress(PickList list)
    {
        var done = list.Lines.Count(static l => l.Status != PickLineStatuses.Open);
        list.StartedAt ??= done > 0 ? clock.UtcNow : null;
        if (done == list.Lines.Count)
        {
            list.Status = PickListStatuses.Picked;
            list.CompletedAt = clock.UtcNow;
        }
        else
        {
            list.Status = done > 0 || list.StartedAt is not null ? PickListStatuses.InProgress : PickListStatuses.Released;
            list.CompletedAt = null;
        }

        list.UpdatedAt = clock.UtcNow;
    }

    /// <summary>Locks the list's row first, so two handhelds confirming lines of the same list take turns and the list's status follows both.</summary>
    private async Task<PickList?> LockedAsync(Guid pickListId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        await uow.Connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM app.inv_pick_lists WHERE tenant_id = @tenant AND id = @id FOR UPDATE", new { tenant = uow.Context.TenantId.Value, id = pickListId }, uow.Transaction, cancellationToken: cancellationToken));
        var list = await db.PickLists.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == pickListId, cancellationToken);
        if (list is not null)
        {
            await db.Entry(list).ReloadAsync(cancellationToken);
        }

        return list;
    }

    private async Task<PickListInfo> InfoAsync(PickList list, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.FindAsync(list.WarehouseId, cancellationToken);
        var binIds = list.Lines.SelectMany(static l => new[] { l.BinId, l.PickedBinId }).Where(static b => b is not null).Select(static b => b!.Value).Distinct().ToList();
        var bins = await db.Bins.AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(static b => b.Id, cancellationToken);
        var lotIds = list.Lines.SelectMany(static l => new[] { l.LotId, l.PickedLotId }).Where(static l => l is not null).Select(static l => l!.Value).Distinct().ToList();
        var lots = await db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var itemCache = new Dictionary<Guid, ItemInfo?>();
        var lines = new List<PickListLineInfo>(list.Lines.Count);
        foreach (var l in list.Lines.OrderBy(static l => l.LineNo))
        {
            if (!itemCache.TryGetValue(l.ItemId, out var item))
            {
                item = await items.FindAsync(l.ItemId, cancellationToken);
                itemCache[l.ItemId] = item;
            }

            var bin = l.BinId is { } b ? bins.GetValueOrDefault(b) : null;
            var lot = l.LotId is { } lo ? lots.GetValueOrDefault(lo) : null;
            var pickedBin = l.PickedBinId is { } pb ? bins.GetValueOrDefault(pb) : null;
            var pickedLot = l.PickedLotId is { } pl ? lots.GetValueOrDefault(pl) : null;
            lines.Add(new PickListLineInfo(l.Id, l.LineNo, l.SourceLineId, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.VariantId,
                l.BinId, bin?.Code, bin?.Zone, l.LotId, lot?.LotNumber, lot?.ExpiresOn, Serials(l.SerialNumbers), N(l.QtyToPick), N(l.QtyPicked),
                l.PickedBinId, pickedBin?.Code, l.PickedLotId, pickedLot?.LotNumber, Serials(l.PickedSerialNumbers), l.Status, l.ShortReason, l.PickedBy, l.PickedAt, item?.BaseUomCode ?? string.Empty));
        }

        return new PickListInfo(list.Id, list.CompanyId, list.WarehouseId, warehouse?.Code ?? string.Empty, list.Number, list.SourceDocumentType, list.SourceDocumentId, list.SourceNumber,
            list.Status, list.AssignedTo, list.AssignedAt, list.StartedAt, list.CompletedAt, list.ClosedAt, list.CancelledAt, list.CancelReason,
            lines.Count, lines.Count(static l => l.Status != PickLineStatuses.Open), N(lines.Sum(static l => l.QtyToPick)), N(lines.Sum(static l => l.QtyPicked)), lines, list.CreatedAt, list.UpdatedAt,
            await NameAsync(list.AssignedTo, [], cancellationToken));
    }

    /// <summary>The picker's display name, so a work queue reads by person.</summary>
    private async Task<string?> NameAsync(Guid? membershipId, Dictionary<Guid, string?> cache, CancellationToken cancellationToken)
    {
        if (membershipId is not { } id)
        {
            return null;
        }

        if (!cache.TryGetValue(id, out var name))
        {
            name = (await members.FindAsync(new MembershipId(id), cancellationToken))?.DisplayName;
            cache[id] = name;
        }

        return name;
    }

    private static IReadOnlyList<string> Serials(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static decimal N(decimal value) => ItemUomMath.Normalize(value);
}

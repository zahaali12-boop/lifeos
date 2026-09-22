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

public sealed record SaveCountRequest(
    Guid CompanyId,
    Guid WarehouseId,
    string Scope = "full",
    IReadOnlyList<Guid>? BinIds = null,
    IReadOnlyList<Guid>? ItemIds = null,
    IReadOnlyList<string>? CycleCountClasses = null,
    DateOnly? PostingDate = null,
    bool Blind = false,
    bool BlockMovements = false,
    string? Notes = null);

public sealed record CountEntryRequest(
    Guid? LineId = null,
    Guid? ItemId = null,
    string? ItemCode = null,
    Guid? BinId = null,
    string? LotNumber = null,
    string? SerialNumber = null,
    Guid? VariantId = null,
    decimal CountedQty = 0m,
    string? Note = null);

public sealed record CountEntriesRequest(IReadOnlyList<CountEntryRequest> Entries);

public sealed record CountLineReasonRequest(Guid? ReasonCodeId = null, string? ReasonCode = null, string? Note = null);

public sealed record CountSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid WarehouseId,
    string WarehouseCode,
    string Scope,
    JsonElement ScopeFilter,
    DateOnly PostingDate,
    bool Blind,
    bool BlockMovements,
    DateTimeOffset? FrozenAt,
    long? LastSequence,
    string? Notes,
    int LineCount,
    int CountedLines,
    int VarianceLines,
    decimal VarianceValue,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    Guid? StockPostingId,
    Guid? JournalEntryId,
    Guid? PostedBy,
    DateTimeOffset? PostedAt,
    DateTimeOffset UpdatedAt);

public sealed record CountLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    string BaseUom,
    Guid? VariantId,
    Guid? BinId,
    string? BinCode,
    Guid? LotId,
    string? LotNumber,
    Guid? SerialId,
    string? SerialNumber,
    decimal? ExpectedQty,
    decimal? CountedQty,
    decimal? PreviousCountedQty,
    decimal? MovementSinceFreeze,
    decimal? VarianceQty,
    decimal? VarianceValue,
    Guid? CountedBy,
    DateTimeOffset? CountedAt,
    bool RecountRequested,
    Guid? ReasonCodeId,
    string? ReasonCode,
    string? Note,
    string Status);

public sealed record CountSheet(CountSummary Count, IReadOnlyList<CountLineSummary> Lines);

/// <summary>
/// Stock counts (roadmap 3.6, hard scenario 11): a planned count of a warehouse is frozen (a snapshot of the expected
/// quantities per item, bin, lot and serial, and the ledger sequence at that moment), counted on a sheet (blind or
/// not, with recounts and stock found off the sheet), reviewed (every variance = counted − expected − the movements
/// posted since the freeze, so the warehouse keeps working), approved, and posted as <c>count_variance</c> movements
/// with a reason code per variance. A count may block movements in its warehouse or bins while it runs.
/// </summary>
public sealed class CountService(
    InventoryDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    IInventoryPosting posting,
    CostingService costing,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "stock_count";

    public static readonly IReadOnlyList<string> Scopes = ["full", "cycle", "bins", "items"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ScopeFilter(IReadOnlyList<Guid>? BinIds = null, IReadOnlyList<Guid>? ItemIds = null, IReadOnlyList<string>? CycleCountClasses = null);

    private sealed class BalanceRow
    {
        public Guid ItemId { get; set; }

        public Guid VariantId { get; set; }

        public Guid BinId { get; set; }

        public Guid LotId { get; set; }

        public Guid SerialId { get; set; }

        public decimal OnHand { get; set; }
    }

    private sealed class MovementRow
    {
        public Guid ItemId { get; set; }

        public Guid VariantId { get; set; }

        public Guid BinId { get; set; }

        public Guid LotId { get; set; }

        public Guid SerialId { get; set; }

        public decimal Quantity { get; set; }
    }

    public static IReadOnlyList<Guid> BinsOf(string scopeFilter)
    {
        try
        {
            return JsonSerializer.Deserialize<ScopeFilter>(scopeFilter, Json)?.BinIds ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<CountSummary>> ListAsync(Guid? companyId, Guid? warehouseId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Counts.Include(static c => c.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(x => x.CompanyId == c);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(x => x.WarehouseId == w);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == s);
        }

        var result = new List<CountSummary>();
        foreach (var count in await query.OrderByDescending(static x => x.Id).Take(200).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(count, cancellationToken));
        }

        return result;
    }

    public async Task<CountSummary?> GetAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        return count is null ? null : await MapAsync(count, cancellationToken);
    }

    public async Task<Result<CountSummary>> CreateAsync(SaveCountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Validation.Scope(principal, InventoryPermissions.CountManage, request.CompanyId, request.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var count = new Count { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(count, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Counts.Add(count);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    public async Task<Result<CountSummary>> UpdateAsync(Guid countId, SaveCountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status != "planned")
        {
            return Error.Conflict("count.not_planned", "Only a planned count can be edited; a frozen count is cancelled and planned again.").WithWhy(("status", count.Status));
        }

        if (count.CompanyId != request.CompanyId)
        {
            return Error.Conflict("count.company_locked", "A count cannot move to another company.");
        }

        var applied = await ApplyAsync(count, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    /// <summary>The snapshot: what the balances say now, per key, and the ledger sequence; the sheet is one line per key with stock.</summary>
    public async Task<Result<CountSummary>> FreezeAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status != "planned")
        {
            return Error.Conflict("count.not_planned", "Only a planned count is frozen.").WithWhy(("status", count.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CountManage, count.CompanyId, count.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(count.CompanyId), cancellationToken))!;
        var filter = JsonSerializer.Deserialize<ScopeFilter>(count.ScopeFilter, Json) ?? new ScopeFilter();
        var itemIds = count.Scope switch
        {
            "items" => filter.ItemIds ?? [],
            "cycle" => await items.ItemsForCycleCountAsync(count.WarehouseId, filter.CycleCountClasses ?? [], cancellationToken),
            _ => null,
        };
        if (count.Scope is "items" or "cycle" && (itemIds is null || itemIds.Count == 0))
        {
            return Error.Validation("count.scope_empty", "No item falls in the count's scope.").WithWhy(("scope", count.Scope));
        }

        var uow = unitOfWork.Current;
        // The balances and the sequence are read under a lock on the warehouse's rows, so nothing slips between the two.
        var rows = (await uow.Connection.QueryAsync<BalanceRow>(new CommandDefinition("""
            SELECT item_id, variant_id, bin_id, lot_id, serial_id, on_hand
            FROM app.inv_stock_balances
            WHERE company_id = @company AND warehouse_id = @warehouse AND on_hand <> 0
              AND (@items::uuid[] IS NULL OR item_id = ANY(@items))
              AND (@bins::uuid[] IS NULL OR bin_id = ANY(@bins))
            ORDER BY item_id, bin_id, lot_id, serial_id
            FOR UPDATE
            """, new { company = count.CompanyId, warehouse = count.WarehouseId, items = itemIds?.ToArray(), bins = count.Scope == "bins" ? (filter.BinIds ?? []).ToArray() : null }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var lastSequence = await uow.Connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT max(sequence) FROM app.inv_stock_ledger_entries", transaction: uow.Transaction, cancellationToken: cancellationToken)) ?? 0L;

        var lineNo = 0;
        foreach (var row in rows)
        {
            db.CountSnapshots.Add(new CountSnapshot { CountId = count.Id, ItemId = row.ItemId, VariantId = row.VariantId, BinId = row.BinId, LotId = row.LotId, SerialId = row.SerialId, ExpectedQty = row.OnHand });
            count.Lines.Add(new CountLine
            {
                Id = Guid.CreateVersion7(),
                CountId = count.Id,
                LineNo = ++lineNo,
                ItemId = row.ItemId,
                VariantId = Nullable(row.VariantId),
                BinId = Nullable(row.BinId),
                LotId = Nullable(row.LotId),
                SerialId = Nullable(row.SerialId),
                ExpectedQty = row.OnHand,
            });
        }

        if (count.Number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "CNT-" + company.Code, "CNT-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, count.PostingDate, count.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            count.Number = allocated.Value.Text;
        }

        count.FrozenAt = clock.UtcNow;
        count.LastSequence = lastSequence;
        count.Status = "frozen";
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, count.Id, count.Number ?? DraftIdentifiers.For(count.Id), AuditActions.StateChanged, After: new { status = count.Status, frozenAt = count.FrozenAt, lastSequence, lines = count.Lines.Count, blockMovements = count.BlockMovements }, CompanyId: count.CompanyId), cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    public async Task<Result<CountSheet>> SheetAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        return new CountSheet(await MapAsync(count, cancellationToken), await LinesAsync(count, cancellationToken));
    }

    /// <summary>Counted quantities on sheet lines (by id or by key); stock found off the sheet becomes a new line with nothing expected.</summary>
    public async Task<Result<CountSheet>> EnterAsync(Guid countId, CountEntriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status is not ("frozen" or "counting"))
        {
            return Error.Conflict("count.not_counting", "Counts are entered on a frozen count until it is reviewed.").WithWhy(("status", count.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CountEnter, count.CompanyId, count.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        if (request.Entries is null || request.Entries.Count == 0)
        {
            return Error.Validation("count.entries_required", "At least one counted quantity.");
        }

        var actor = principal.Principal?.UserId.Value;
        var lineNo = count.Lines.Count == 0 ? 0 : count.Lines.Max(static l => l.LineNo);
        foreach (var entry in request.Entries)
        {
            if (entry.CountedQty < 0m)
            {
                return Error.Validation("count.quantity_invalid", "A counted quantity is zero or more.").WithWhy(("lineId", entry.LineId), ("item", entry.ItemCode ?? entry.ItemId?.ToString()));
            }

            CountLine? line;
            if (entry.LineId is { } lineId)
            {
                line = count.Lines.SingleOrDefault(l => l.Id == lineId);
                if (line is null)
                {
                    return Error.NotFound("count_line", lineId);
                }
            }
            else
            {
                var resolved = await ResolveKeyAsync(count, entry, cancellationToken);
                if (resolved.IsFailure)
                {
                    return resolved.Error!;
                }

                var (item, lotId, serialId) = resolved.Value;
                line = count.Lines.SingleOrDefault(l => l.ItemId == item.Id && l.VariantId == entry.VariantId && l.BinId == entry.BinId && l.LotId == lotId && l.SerialId == serialId);
                if (line is null)
                {
                    // Found stock: nothing expected, so the whole count is a variance.
                    line = new CountLine { Id = Guid.CreateVersion7(), CountId = count.Id, LineNo = ++lineNo, ItemId = item.Id, VariantId = entry.VariantId, BinId = entry.BinId, LotId = lotId, SerialId = serialId, ExpectedQty = 0m };
                    count.Lines.Add(line);
                }
            }

            if (line.SerialId is not null && entry.CountedQty is not (0m or 1m))
            {
                return Error.Validation("count.serial_quantity", "A serial is counted as present (1) or missing (0).").WithWhy(("lineId", line.Id), ("countedQty", entry.CountedQty));
            }

            if (line.CountedQty is { } previous && line.RecountRequested)
            {
                line.PreviousCountedQty = previous;
            }

            line.CountedQty = entry.CountedQty;
            line.CountedBy = actor;
            line.CountedAt = clock.UtcNow;
            line.RecountRequested = false;
            line.Status = "counted";
            if (!string.IsNullOrWhiteSpace(entry.Note))
            {
                line.Note = entry.Note.Trim();
            }
        }

        count.Status = "counting";
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new CountSheet(await MapAsync(count, cancellationToken), await LinesAsync(count, cancellationToken));
    }

    public async Task<Result<CountLineSummary>> RecountAsync(Guid countId, Guid lineId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status is not ("frozen" or "counting" or "review"))
        {
            return Error.Conflict("count.not_counting", "A recount is asked while the count is open.").WithWhy(("status", count.Status));
        }

        var line = count.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("count_line", lineId);
        }

        line.RecountRequested = true;
        line.Status = "recount";
        count.Status = "counting";
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await LinesAsync(count, cancellationToken)).Single(l => l.Id == lineId);
    }

    /// <summary>Every line counted (or a recount answered), the movements since the freeze read, the variances computed and valued; then the reviewers decide.</summary>
    public async Task<Result<CountSheet>> ReviewAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status is not ("frozen" or "counting" or "review"))
        {
            return Error.Conflict("count.not_counting", "Only an open count is reviewed.").WithWhy(("status", count.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CountManage, count.CompanyId, count.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var pending = count.Lines.Where(static l => l.CountedQty is null || l.RecountRequested).Select(static l => l.LineNo).ToList();
        if (pending.Count > 0)
        {
            return Error.Conflict("count.lines_uncounted", "Every line is counted (and every recount answered) before the review.").WithWhy(("lineNos", pending));
        }

        await ComputeVariancesAsync(count, cancellationToken);
        count.Status = "review";
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new CountSheet(await MapAsync(count, cancellationToken), await LinesAsync(count, cancellationToken));
    }

    public async Task<Result<CountLineSummary>> SetReasonAsync(Guid countId, Guid lineId, CountLineReasonRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status is not ("review" or "approved"))
        {
            return Error.Conflict("count.not_in_review", "Reasons are given on the variances of a reviewed count.").WithWhy(("status", count.Status));
        }

        var line = count.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("count_line", lineId);
        }

        var code = request.ReasonCode?.Trim().ToUpperInvariant();
        var reason = request.ReasonCodeId is { } rid ? await db.ReasonCodes.SingleOrDefaultAsync(r => r.Id == rid, cancellationToken) : code is null ? null : await db.ReasonCodes.SingleOrDefaultAsync(r => r.Code == code, cancellationToken);
        if (reason is null || !reason.IsActive || reason.AppliesTo != "count")
        {
            return Error.Validation("count.reason_not_applicable", "A count variance takes an active reason code that applies to counts.").WithWhy(("lineId", lineId), ("reasonCode", request.ReasonCode ?? request.ReasonCodeId?.ToString()));
        }

        if (reason.RequiresNote && string.IsNullOrWhiteSpace(request.Note ?? line.Note))
        {
            return Error.Validation("count.note_required", "This reason code requires a note.").WithWhy(("lineId", lineId), ("reasonCode", reason.Code));
        }

        line.ReasonCodeId = reason.Id;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            line.Note = request.Note.Trim();
        }

        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await LinesAsync(count, cancellationToken)).Single(l => l.Id == lineId);
    }

    public async Task<Result<CountSummary>> ApproveAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status != "review")
        {
            return Error.Conflict("count.not_in_review", "Only a reviewed count is approved.").WithWhy(("status", count.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CountApprove, count.CompanyId, count.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        await ComputeVariancesAsync(count, cancellationToken);
        var missing = count.Lines.Where(static l => l.VarianceQty != 0m && l.ReasonCodeId is null).Select(static l => l.LineNo).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation("count.reason_required", "Every variance carries a reason code before approval.").WithWhy(("lineNos", missing));
        }

        count.Status = "approved";
        count.ApprovedBy = principal.Principal?.UserId.Value;
        count.ApprovedAt = clock.UtcNow;
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, count.Id, count.Number ?? DraftIdentifiers.For(count.Id), AuditActions.Approved, After: new { variances = count.Lines.Count(static l => l.VarianceQty != 0m), varianceValue = count.Lines.Sum(static l => l.VarianceValue) }, CompanyId: count.CompanyId), cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    /// <summary>The variances (re-read against the movements since the freeze at this very moment) become count_variance movements with their reasons.</summary>
    public async Task<Result<CountSummary>> PostAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status != "approved")
        {
            return Error.Conflict("count.not_approved", "Only an approved count is posted.").WithWhy(("status", count.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CountPost, count.CompanyId, count.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        await ComputeVariancesAsync(count, cancellationToken);
        var reasonIds = count.Lines.Where(static l => l.ReasonCodeId != null).Select(static l => l.ReasonCodeId!.Value).Distinct().ToList();
        var reasons = await db.ReasonCodes.Where(r => reasonIds.Contains(r.Id)).ToDictionaryAsync(static r => r.Id, cancellationToken);
        var serialIds = count.Lines.Where(static l => l.SerialId != null).Select(static l => l.SerialId!.Value).Distinct().ToList();
        var serials = await db.Serials.Where(s => serialIds.Contains(s.Id)).ToDictionaryAsync(static s => s.Id, static s => s.SerialNumber, cancellationToken);
        var lines = new List<StockLine>();
        foreach (var line in count.Lines.Where(static l => l.VarianceQty != 0m).OrderBy(static l => l.LineNo))
        {
            if (line.ReasonCodeId is null)
            {
                return Error.Validation("count.reason_required", "Every variance carries a reason code.").WithWhy(("lineNo", line.LineNo));
            }

            var item = (await items.FindAsync(line.ItemId, cancellationToken))!;
            lines.Add(new StockLine(line.ItemId, StockEntryTypes.CountVariance, line.VarianceQty, count.WarehouseId, item.BaseUomId, line.VariantId, line.BinId, line.LotId, line.SerialId, SourceLineId: line.Id,
                OffsetRoleOverride: reasons[line.ReasonCodeId.Value].AccountRoleOverride, SerialNumbers: line.SerialId is { } sid ? [serials[sid]] : null));
        }

        if (lines.Count > 0)
        {
            var posted = await posting.PostAsync(new StockPostingRequest(count.CompanyId, count.PostingDate, DocumentType, count.Id, lines, $"{DocumentType}:{count.Id:N}"), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            count.StockPostingId = posted.Value.PostingId;
            count.JournalEntryId = posted.Value.JournalEntryId;
        }

        foreach (var line in count.Lines)
        {
            line.Status = "posted";
        }

        count.Status = "posted";
        count.PostedBy = principal.Principal?.UserId.Value;
        count.PostedAt = clock.UtcNow;
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, count.Id, count.Number ?? DraftIdentifiers.For(count.Id), AuditActions.Posted, After: new { postingDate = count.PostingDate, stockPostingId = count.StockPostingId, journalEntryId = count.JournalEntryId, variances = lines.Select(static l => new { l.ItemId, l.Quantity, l.BinId, l.LotId, l.SerialId }) }, CompanyId: count.CompanyId), cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    public async Task<Result<CountSummary>> CancelAsync(Guid countId, CancellationToken cancellationToken)
    {
        var count = await db.Counts.Include(static c => c.Lines).SingleOrDefaultAsync(c => c.Id == countId, cancellationToken);
        if (count is null)
        {
            return Error.NotFound("count", countId);
        }

        if (count.Status is "posted" or "cancelled")
        {
            return Error.Conflict("count.not_cancellable", "A posted count stays; correct it with an adjustment.").WithWhy(("status", count.Status));
        }

        count.Status = "cancelled";
        count.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, count.Id, count.Number ?? DraftIdentifiers.For(count.Id), AuditActions.StateChanged, After: new { status = count.Status }, CompanyId: count.CompanyId), cancellationToken);
        return await MapAsync(count, cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>movement_since_freeze = Σ entries for the key with sequence after the freeze and posting date on or before the count date; variance = counted − (expected + movement).</summary>
    private async Task ComputeVariancesAsync(Count count, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(count.CompanyId), cancellationToken))!;
        var currency = company.FunctionalCurrency;
        var rounding = new Kernel.Amounts.RoundingPolicy(company.RoundingMode);
        var uow = unitOfWork.Current;
        var movements = (await uow.Connection.QueryAsync<MovementRow>(new CommandDefinition("""
            SELECT item_id, coalesce(variant_id, '00000000-0000-0000-0000-000000000000'::uuid) AS variant_id, coalesce(bin_id, '00000000-0000-0000-0000-000000000000'::uuid) AS bin_id,
                   coalesce(lot_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lot_id, coalesce(serial_id, '00000000-0000-0000-0000-000000000000'::uuid) AS serial_id, sum(quantity) AS quantity
            FROM app.inv_stock_ledger_entries
            WHERE company_id = @company AND warehouse_id = @warehouse AND sequence > @after AND posting_date <= @date
              AND NOT (source_document_type = @docType AND source_document_id = @count)
            GROUP BY 1, 2, 3, 4, 5
            """, new { company = count.CompanyId, warehouse = count.WarehouseId, after = count.LastSequence ?? 0L, date = count.PostingDate, docType = DocumentType, count = count.Id }, uow.Transaction, cancellationToken: cancellationToken)))
            .ToDictionary(static m => (m.ItemId, m.VariantId, m.BinId, m.LotId, m.SerialId), static m => m.Quantity);
        var costs = new Dictionary<Guid, decimal>();
        foreach (var line in count.Lines)
        {
            var moved = movements.GetValueOrDefault((line.ItemId, line.VariantId ?? Guid.Empty, line.BinId ?? Guid.Empty, line.LotId ?? Guid.Empty, line.SerialId ?? Guid.Empty));
            line.MovementSinceFreeze = moved;
            line.VarianceQty = line.CountedQty is { } counted ? counted - (line.ExpectedQty + moved) : 0m;
            if (!costs.TryGetValue(line.ItemId, out var unitCost))
            {
                var cost = await costing.CostAsync(count.CompanyId, line.ItemId, count.WarehouseId, count.PostingDate, cancellationToken);
                unitCost = cost is null ? 0m : cost.AverageUnitCost > 0m ? cost.AverageUnitCost : cost.ExpectedUnitCost;
                costs[line.ItemId] = unitCost;
            }

            line.VarianceValue = rounding.Round(line.VarianceQty * unitCost, currency.MinorUnits);
        }
    }

    private async Task<Result<(ItemInfo Item, Guid? LotId, Guid? SerialId)>> ResolveKeyAsync(Count count, CountEntryRequest entry, CancellationToken cancellationToken)
    {
        var code = entry.ItemCode?.Trim();
        var item = entry.ItemId is { } id ? await items.FindAsync(id, cancellationToken) : code is null ? null : await items.FindByCodeAsync(code, cancellationToken);
        if (item is null)
        {
            return Error.Validation("count.item_unknown", "The item does not exist.").WithWhy(("item", entry.ItemCode ?? entry.ItemId?.ToString()));
        }

        Guid? lotId = null;
        if (!string.IsNullOrWhiteSpace(entry.LotNumber))
        {
            var number = entry.LotNumber.Trim();
            lotId = await db.Lots.Where(l => l.ItemId == item.Id && l.LotNumber == number).Select(static l => (Guid?)l.Id).SingleOrDefaultAsync(cancellationToken);
            if (lotId is null)
            {
                return Error.Validation("count.lot_unknown", "The lot does not exist for this item; receive it first, then count it.").WithWhy(("item", item.Code), ("lotNumber", number));
            }
        }

        Guid? serialId = null;
        if (!string.IsNullOrWhiteSpace(entry.SerialNumber))
        {
            var number = entry.SerialNumber.Trim();
            serialId = await db.Serials.Where(s => s.ItemId == item.Id && s.SerialNumber == number).Select(static s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken);
            if (serialId is null)
            {
                return Error.Validation("count.serial_unknown", "The serial does not exist for this item; receive it first, then count it.").WithWhy(("item", item.Code), ("serialNumber", number));
            }
        }

        if (entry.BinId is { } binId)
        {
            var bin = await warehouses.FindBinAsync(binId, cancellationToken);
            if (bin is null || bin.WarehouseId != count.WarehouseId)
            {
                return Error.Validation("count.bin_invalid", "The bin must belong to the counted warehouse.").WithWhy(("binId", binId));
            }
        }

        return (item, lotId, serialId);
    }

    private async Task<Result> ApplyAsync(Count count, SaveCountRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var scope = Validation.OneOf(request.Scope, "count.scope", Scopes);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value || !warehouse.IsActive)
        {
            return Error.Validation("count.warehouse_invalid", "The warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        if (scope.Value == "bins")
        {
            if (!warehouse.BinsEnabled || request.BinIds is not { Count: > 0 })
            {
                return Error.Validation("count.bins_required", "A count by bins names the bins of a warehouse that uses bins.").WithWhy(("warehouse", warehouse.Code));
            }

            foreach (var binId in request.BinIds)
            {
                var bin = await warehouses.FindBinAsync(binId, cancellationToken);
                if (bin is null || bin.WarehouseId != warehouse.Id)
                {
                    return Error.Validation("count.bin_invalid", "The bin must belong to the counted warehouse.").WithWhy(("binId", binId));
                }
            }
        }

        if (scope.Value == "items" && request.ItemIds is not { Count: > 0 })
        {
            return Error.Validation("count.items_required", "A count by items names the items.");
        }

        if (scope.Value == "cycle" && request.CycleCountClasses is not { Count: > 0 })
        {
            return Error.Validation("count.classes_required", "A cycle count names the classes (A, B, C) it covers.");
        }

        count.WarehouseId = request.WarehouseId;
        count.Scope = scope.Value;
        count.ScopeFilter = JsonSerializer.Serialize(new ScopeFilter(request.BinIds, request.ItemIds, request.CycleCountClasses?.Select(static c => c.Trim().ToUpperInvariant()).ToList()), Json);
        count.PostingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        count.Blind = request.Blind;
        count.BlockMovements = request.BlockMovements;
        count.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        count.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<IReadOnlyList<CountLineSummary>> LinesAsync(Count count, CancellationToken cancellationToken)
    {
        var hide = count.Blind && count.Status is "frozen" or "counting";
        var lotIds = count.Lines.Where(static l => l.LotId is not null).Select(static l => l.LotId!.Value).Distinct().ToList();
        var lots = lotIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Lots.Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, static l => l.LotNumber, cancellationToken);
        var serialIds = count.Lines.Where(static l => l.SerialId is not null).Select(static l => l.SerialId!.Value).Distinct().ToList();
        var serials = serialIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Serials.Where(s => serialIds.Contains(s.Id)).ToDictionaryAsync(static s => s.Id, static s => s.SerialNumber, cancellationToken);
        var reasonIds = count.Lines.Where(static l => l.ReasonCodeId is not null).Select(static l => l.ReasonCodeId!.Value).Distinct().ToList();
        var reasons = reasonIds.Count == 0 ? new Dictionary<Guid, string>() : await db.ReasonCodes.Where(r => reasonIds.Contains(r.Id)).ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        var result = new List<CountLineSummary>(count.Lines.Count);
        foreach (var l in count.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ItemId, cancellationToken))!;
            var bin = l.BinId is { } b ? await warehouses.FindBinAsync(b, cancellationToken) : null;
            result.Add(new CountLineSummary(l.Id, l.LineNo, item.Id, item.Code, item.Name.Values, item.BaseUomCode, l.VariantId, l.BinId, bin?.Code, l.LotId, l.LotId is { } lid ? lots.GetValueOrDefault(lid) : null, l.SerialId, l.SerialId is { } sid ? serials.GetValueOrDefault(sid) : null,
                hide ? null : ItemUomMath.Normalize(l.ExpectedQty), l.CountedQty is { } c ? ItemUomMath.Normalize(c) : null, l.PreviousCountedQty is { } p ? ItemUomMath.Normalize(p) : null,
                hide ? null : ItemUomMath.Normalize(l.MovementSinceFreeze), hide ? null : ItemUomMath.Normalize(l.VarianceQty), hide ? null : l.VarianceValue,
                l.CountedBy, l.CountedAt, l.RecountRequested, l.ReasonCodeId, l.ReasonCodeId is { } rid ? reasons.GetValueOrDefault(rid) : null, l.Note, l.Status));
        }

        return result;
    }

    private async Task<CountSummary> MapAsync(Count c, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.FindAsync(c.WarehouseId, cancellationToken);
        return new CountSummary(c.Id, c.CompanyId, c.Number ?? DraftIdentifiers.For(c.Id), c.Status, c.WarehouseId, warehouse?.Code ?? string.Empty, c.Scope, JsonDocument.Parse(c.ScopeFilter).RootElement.Clone(), c.PostingDate, c.Blind, c.BlockMovements,
            c.FrozenAt, c.LastSequence, c.Notes, c.Lines.Count, c.Lines.Count(static l => l.CountedQty is not null && !l.RecountRequested), c.Lines.Count(static l => l.VarianceQty != 0m), c.Lines.Sum(static l => l.VarianceValue),
            c.ApprovedBy, c.ApprovedAt, c.StockPostingId, c.JournalEntryId, c.PostedBy, c.PostedAt, c.UpdatedAt);
    }

    private static Guid? Nullable(Guid id) => id == Guid.Empty ? null : id;
}

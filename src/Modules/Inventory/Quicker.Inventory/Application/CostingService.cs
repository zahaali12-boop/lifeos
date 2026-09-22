using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Inventory.Application;

/// <summary>Settings of the inventory module (<c>Quicker:Inventory</c>).</summary>
public sealed class InventoryOptions
{
    public const string SectionName = "Quicker:Inventory";

    /// <summary>A re-application that has to walk more entries than this continues in a background job (ADR-0008).</summary>
    public int RecostThreshold { get; set; } = 2000;
}

/// <summary>
/// The costing engine (ADR-0008, value side). Every stock movement is valued when it posts: FIFO applies outbound
/// entries to the oldest inbound layers with quantity left, the daily average values a day's outbound entries at
/// the average of the opening balance and that day's receipts, standard cost values everything at the standard in
/// force and posts the difference as a variance. A value-affecting event at an earlier date (a backdated movement,
/// a late invoice, a landed cost, a new standard) locks the item's cost scope, re-applies from that date forward
/// and writes additive <c>cost_adjustment</c> value entries with the trigger named, never editing a row. Every value
/// entry is posted to the books through the accounting engine in the same unit of work, so the valuation and the
/// inventory accounts agree at every date (hard scenario 15).
/// </summary>
public sealed class CostingService(
    InventoryDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IFiscalPeriodResolver periods,
    IPostingService posting,
    IJobQueue jobs,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    InventoryOptions options) : IInventoryCosting
{
    public const string RunDocumentType = "cost_adjustment_run";

    private static readonly Guid None = Guid.Empty;

    private static readonly string[] AdjustmentKinds = ["invoice", "landed_cost"];

    /// <summary>A cost scope: the company, the item and the warehouse when the company costs per warehouse (nil otherwise).</summary>
    internal sealed record ScopeKey(Guid CompanyId, Guid ItemId, Guid WarehouseId)
    {
        public string LockOrder => $"{CompanyId:N}{ItemId:N}{WarehouseId:N}";
    }

    internal sealed record Trigger(string Kind, string DocumentType, Guid DocumentId, Guid? SleId, string? Note);

    private sealed class Layer
    {
        public required Guid SleId { get; init; }

        public required DateOnly Date { get; init; }

        public required long Sequence { get; init; }

        public required decimal Quantity { get; init; }

        public decimal RemainingQuantity { get; set; }

        public decimal Value { get; set; }

        public decimal RemainingValue { get; set; }

        public decimal UnitCost => Quantity == 0m ? 0m : Value / Quantity;
    }

    private sealed class LayerRow
    {
        public Guid Id { get; set; }

        public DateOnly PostingDate { get; set; }

        public long Sequence { get; set; }

        public decimal Quantity { get; set; }

        public decimal Applied { get; set; }

        public decimal AppliedValue { get; set; }

        public decimal Value { get; set; }
    }

    private sealed class PairRow
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }

        public Guid ItemId { get; set; }

        public Guid WarehouseId { get; set; }

        public DateOnly PostingDate { get; set; }
    }

    /// <summary>What one walk of a scope produced, accumulated across the scopes a call touches and written once.</summary>
    private sealed class RunContext
    {
        public required Trigger Trigger { get; init; }

        public required bool InBackground { get; init; }

        /// <summary>The document the journals name: the stock posting's source, the trigger document, or the run.</summary>
        public required string DocumentType { get; init; }

        public required Guid DocumentId { get; init; }

        public Guid? StockPostingId { get; init; }

        public CostAdjustmentRun? Run { get; set; }

        /// <summary>Value entries computed but not yet posted and written (flushed at the end of every walk).</summary>
        public List<StockValueEntry> PendingValueEntries { get; } = [];

        public List<StockValueEntry> AllValueEntries { get; } = [];

        public List<ItemApplication> PendingApplications { get; } = [];

        public Dictionary<Guid, decimal> PairAmounts { get; } = new();

        public Queue<(ScopeKey Scope, DateOnly From)> Pending { get; } = new();

        public Dictionary<Guid, CompanyInfo> Companies { get; } = new();

        public Dictionary<(Guid Company, DateOnly Date), DateOnly> GlDates { get; } = new();

        public int Flushes { get; set; }

        public Guid? FirstJournalId { get; set; }

        public string? FirstJournalNumber { get; set; }

        public bool Queued { get; set; }

        public int EntriesWalked { get; set; }

        public int EntriesReapplied { get; set; }

        public decimal AmountAdjusted { get; set; }

        public int JournalsPosted { get; set; }
    }

    private sealed class Method
    {
        public required string Costing { get; init; }

        public required string Scope { get; init; }

        public required Currency Currency { get; init; }

        public required RoundingPolicy Rounding { get; init; }

        public required Guid? PostingGroupId { get; init; }

        public required ItemInfo Item { get; init; }

        public required CompanyInfo Company { get; init; }

        public required List<StandardCostVersion> Standards { get; init; }

        public required decimal? SettingStandard { get; init; }

        public decimal? StandardOn(DateOnly date)
        {
            var version = Standards.Where(v => v.EffectiveFrom <= date).MaxBy(static v => v.EffectiveFrom);
            return version?.StandardCost ?? SettingStandard;
        }
    }

    // ------------------------------------------------------------------ valuing a new posting

    /// <summary>
    /// Values the entries of a stock posting that was just written (in the same unit of work) and posts the journal.
    /// A backdated posting re-applies everything after it in the same scopes; when that exceeds the threshold the
    /// whole valuation continues in a background job and the entries carry no value until it completes.
    /// </summary>
    public async Task<Result<(Guid? JournalEntryId, string? JournalNumber, IReadOnlyDictionary<Guid, StockValueEntry> Primary)>> ValuePostingAsync(StockPosting stockPosting, CompanyInfo company, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockPosting);
        ArgumentNullException.ThrowIfNull(company);
        var context = new RunContext
        {
            Trigger = new Trigger("stock_posting", stockPosting.SourceDocumentType, stockPosting.SourceDocumentId, null, null),
            InBackground = false,
            DocumentType = stockPosting.SourceDocumentType,
            DocumentId = stockPosting.SourceDocumentId,
            StockPostingId = stockPosting.Id,
        };
        context.Companies[company.Id.Value] = company;
        var scopes = new List<(ScopeKey Scope, bool AfterPair)>();
        foreach (var entry in stockPosting.Entries.Where(static e => e.Ownership == "own"))
        {
            var scope = new ScopeKey(entry.CompanyId, entry.ItemId, company.CostingScope == "warehouse" ? entry.WarehouseId : None);
            if (scopes.Any(s => s.Scope == scope))
            {
                continue;
            }

            // The destination scope of a transfer is valued after the source scope, so the carried cost is known.
            scopes.Add((scope, entry.EntryType == StockEntryTypes.TransferIn));
        }

        foreach (var (scope, _) in scopes.OrderBy(static s => s.AfterPair).ThenBy(static s => s.Scope.LockOrder, StringComparer.Ordinal))
        {
            context.Pending.Enqueue((scope, stockPosting.PostingDate));
        }

        var drained = await DrainAsync(context, cancellationToken);
        if (drained.IsFailure)
        {
            return drained.Error!;
        }

        await CompleteAsync(context, cancellationToken);
        var primary = context.AllValueEntries.Where(v => v.SleId is not null && v.ValueType is ValueEntryTypes.DirectCost or ValueEntryTypes.ExpectedCost && stockPosting.Entries.Any(e => e.Id == v.SleId))
            .GroupBy(static v => v.SleId!.Value).ToDictionary(static g => g.Key, static g => g.First());
        return (context.FirstJournalId, context.FirstJournalNumber, primary);
    }

    // ------------------------------------------------------------------ late invoices and landed costs

    public async Task<Result<InboundCostAdjustmentResult>> AdjustInboundCostAsync(InboundCostAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not ("invoice" or "landed_cost"))
        {
            return Error.Validation("costing.adjustment_kind_invalid", "The adjustment is an invoice settlement or a landed cost.").WithWhy(("kind", request.Kind), ("allowed", AdjustmentKinds));
        }

        if (string.IsNullOrWhiteSpace(request.TriggerDocumentType) || request.TriggerDocumentId == Guid.Empty)
        {
            return Error.Validation("costing.trigger_required", "A cost adjustment names the document that triggered it.");
        }

        var entry = await db.Entries.SingleOrDefaultAsync(e => e.Id == request.SleId, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound("stock_entry", request.SleId);
        }

        if (entry.Quantity <= 0m || entry.Ownership != "own")
        {
            return Error.Validation("costing.entry_not_inbound", "Only an inbound entry of own stock carries a cost that can be settled or increased.").WithWhy(("sleId", entry.Id), ("entryType", entry.EntryType), ("quantity", entry.Quantity));
        }

        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (key is not null)
        {
            var uow = unitOfWork.Current;
            var replayIds = (await uow.Connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM app.inv_stock_value_entries WHERE sle_id = @sle AND reason->>'idempotencyKey' = @key ORDER BY created_at", new { sle = entry.Id, key }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
            if (replayIds.Count > 0)
            {
                var replay = await db.ValueEntries.Where(v => replayIds.Contains(v.Id)).ToListAsync(cancellationToken);
                return new InboundCostAdjustmentResult(replay.Select(Map).ToList(), replay[0].GlJournalEntryId, null);
            }
        }

        var company = await companies.FindAsync(new CompanyId(entry.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", entry.CompanyId);
        }

        var context = new RunContext
        {
            Trigger = new Trigger(request.Kind, request.TriggerDocumentType.Trim(), request.TriggerDocumentId, entry.Id, request.Reason),
            InBackground = false,
            DocumentType = request.TriggerDocumentType.Trim(),
            DocumentId = request.TriggerDocumentId,
        };
        context.Companies[company.Id.Value] = company;
        var scope = ScopeOf(entry, company);
        await LockScopeAsync(scope, cancellationToken);
        var method = await MethodAsync(context, scope, cancellationToken);
        var existing = await db.ValueEntries.Where(v => v.SleId == entry.Id).ToListAsync(cancellationToken);
        var expected = existing.Sum(static v => v.CostAmountExpected);
        var actualPrimary = existing.Where(static v => IsInventoryRole(v.AccountRole)).Sum(static v => v.CostAmountActual);
        var glDate = request.PostingDate ?? await GlDateAsync(context, company, entry.PostingDate, cancellationToken);
        if (glDate < entry.PostingDate)
        {
            return Error.Validation("costing.posting_date_before_entry", "The adjustment cannot post before the entry it adjusts.").WithWhy(("postingDate", glDate), ("entryDate", entry.PostingDate));
        }

        var reason = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = request.Kind,
            ["trigger"] = new { documentType = request.TriggerDocumentType.Trim(), documentId = request.TriggerDocumentId },
            ["note"] = request.Reason,
            ["idempotencyKey"] = key,
        };
        var created = new List<StockValueEntry>();
        if (request.Kind == "invoice")
        {
            if (request.ActualUnitCost is not { } unitCost || unitCost < 0m)
            {
                return Error.Validation("costing.actual_unit_cost_required", "An invoice settlement gives the actual cost per entered unit.");
            }

            var actualTotal = method.Rounding.Round(unitCost * Math.Abs(entry.EnteredQuantity), method.Currency.MinorUnits);
            reason["expectedAmount"] = expected;
            reason["actualAmount"] = actualTotal;
            if (expected != 0m)
            {
                created.Add(NewValueEntry(entry, method, ValueEntryTypes.ExpectedCostReversal, entry.Quantity, 0m, -expected, AccountRoles.Inventory, AccountRoles.GRNI, entry.SourceDocumentId, glDate, reason, existing.FirstOrDefault(static v => v.ValueType == ValueEntryTypes.ExpectedCost)?.Id, null));
            }

            if (method.Costing == CostingMethods.Standard)
            {
                // Inventory stays at the standard; the invoice difference is a purchase price variance.
                var standardTotal = method.Rounding.Round((method.StandardOn(entry.PostingDate) ?? 0m) * entry.Quantity, method.Currency.MinorUnits);
                var priorVariance = existing.Where(static v => v.ValueType == ValueEntryTypes.Variance).Sum(static v => v.CostAmountActual);
                if (expected != 0m)
                {
                    created.Add(NewValueEntry(entry, method, ValueEntryTypes.DirectCost, entry.Quantity, standardTotal, 0m, AccountRoles.Inventory, AccountRoles.GRNI, entry.SourceDocumentId, glDate, reason, null, null));
                }

                var variance = actualTotal - standardTotal - priorVariance - (expected == 0m ? actualPrimary - standardTotal : 0m);
                if (variance != 0m)
                {
                    created.Add(NewValueEntry(entry, method, ValueEntryTypes.Variance, entry.Quantity, variance, 0m, VarianceRoleFor(entry.EntryType), AccountRoles.GRNI, entry.SourceDocumentId, glDate, reason, null, null));
                }
            }
            else
            {
                var delta = actualTotal - (expected == 0m ? actualPrimary : 0m);
                if (delta != 0m || expected != 0m)
                {
                    created.Add(NewValueEntry(entry, method, ValueEntryTypes.DirectCost, entry.Quantity, delta, 0m, AccountRoles.Inventory, AccountRoles.GRNI, entry.SourceDocumentId, glDate, reason, null, null));
                }
            }
        }
        else
        {
            if (request.Amount is not { } amount || amount == 0m)
            {
                return Error.Validation("costing.amount_required", "A landed cost gives the amount to add to the entry's cost.");
            }

            var rounded = method.Rounding.Round(amount, method.Currency.MinorUnits);
            reason["amount"] = rounded;
            created.Add(method.Costing == CostingMethods.Standard
                ? NewValueEntry(entry, method, ValueEntryTypes.Variance, entry.Quantity, rounded, 0m, VarianceRoleFor(entry.EntryType), AccountRoles.LandedCostClearing, request.TriggerDocumentId, glDate, reason, null, null)
                : NewValueEntry(entry, method, ValueEntryTypes.IndirectCost, entry.Quantity, rounded, 0m, AccountRoles.Inventory, AccountRoles.LandedCostClearing, request.TriggerDocumentId, glDate, reason, null, null));
        }

        if (created.Count == 0)
        {
            return new InboundCostAdjustmentResult([], null, null);
        }

        context.PendingValueEntries.AddRange(created);
        // The layer's value changed: everything that consumed it (and, through transfers, everything downstream) is re-applied.
        context.Pending.Enqueue((scope, entry.PostingDate));
        var drained = await DrainAsync(context, cancellationToken);
        if (drained.IsFailure)
        {
            return drained.Error!;
        }

        await CompleteAsync(context, cancellationToken);
        return new InboundCostAdjustmentResult(created.Select(Map).ToList(), context.FirstJournalId, context.Run is null ? null : Map(context.Run));
    }

    // ------------------------------------------------------------------ standard costs

    public async Task<Result<StandardCostVersion>> SetStandardCostAsync(Guid companyId, Guid itemId, decimal standardCost, DateOnly effectiveFrom, string? reason, CancellationToken cancellationToken)
    {
        if (standardCost < 0m)
        {
            return Error.Validation("costing.standard_cost.negative", "A standard cost is zero or more.");
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var item = await items.FindAsync(itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        if (await db.StandardCosts.AnyAsync(v => v.CompanyId == companyId && v.ItemId == itemId && v.EffectiveFrom == effectiveFrom, cancellationToken))
        {
            return Error.Conflict("costing.standard_cost.date_taken", "A standard cost already starts on that date; choose another date.").WithWhy(("effectiveFrom", effectiveFrom));
        }

        var version = new StandardCostVersion
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            ItemId = itemId,
            StandardCost = standardCost,
            EffectiveFrom = effectiveFrom,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ApprovedBy = principal.Principal?.UserId.Value,
            CreatedAt = clock.UtcNow,
        };
        db.StandardCosts.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        var context = new RunContext
        {
            Trigger = new Trigger("standard_cost", "standard_cost", version.Id, null, version.Reason),
            InBackground = false,
            DocumentType = "standard_cost",
            DocumentId = version.Id,
        };
        context.Companies[companyId] = company;
        var scopeKeys = company.CostingScope == "warehouse"
            ? (await db.ItemCosts.Where(c => c.CompanyId == companyId && c.ItemId == itemId).Select(static c => c.WarehouseId).Distinct().ToListAsync(cancellationToken)).Select(w => new ScopeKey(companyId, itemId, w)).ToList()
            : [new ScopeKey(companyId, itemId, None)];
        foreach (var scope in scopeKeys.OrderBy(static s => s.LockOrder, StringComparer.Ordinal))
        {
            await LockScopeAsync(scope, cancellationToken);
            var method = await MethodAsync(context, scope, cancellationToken);
            if (method.Costing != CostingMethods.Standard)
            {
                continue;
            }

            // Stock on hand at the start of the effective date moves to the new standard (POSTING_RULES §4, standard cost change).
            var opening = await db.ItemCosts.Where(c => c.CompanyId == scope.CompanyId && c.ItemId == scope.ItemId && c.WarehouseId == scope.WarehouseId && c.ValuationDate < effectiveFrom)
                .OrderByDescending(static c => c.ValuationDate).FirstOrDefaultAsync(cancellationToken);
            var quantity = opening?.Quantity ?? 0m;
            var previous = method.Standards.Where(v => v.EffectiveFrom < effectiveFrom).MaxBy(static v => v.EffectiveFrom)?.StandardCost ?? method.SettingStandard ?? 0m;
            var delta = method.Rounding.Round(quantity * (standardCost - previous), method.Currency.MinorUnits);
            if (delta != 0m)
            {
                var glDate = await GlDateAsync(context, company, effectiveFrom, cancellationToken);
                var warehouseId = scope.WarehouseId == None ? await FirstWarehouseAsync(scope, cancellationToken) : scope.WarehouseId;
                if (warehouseId is { } wh)
                {
                    context.PendingValueEntries.Add(new StockValueEntry
                    {
                        Id = Guid.CreateVersion7(),
                        SleId = null,
                        CompanyId = companyId,
                        ItemId = itemId,
                        WarehouseId = wh,
                        PostingDate = glDate,
                        ValuationDate = effectiveFrom,
                        ValueType = ValueEntryTypes.Revaluation,
                        ValuedQuantity = quantity,
                        UnitCost = standardCost - previous,
                        CostAmountActual = delta,
                        Currency = method.Currency.Code,
                        AccountRole = AccountRoles.Inventory,
                        OffsetRole = AccountRoles.InventoryWriteDown,
                        ItemPostingGroupId = method.PostingGroupId,
                        SourceDocumentType = "standard_cost",
                        SourceDocumentId = version.Id,
                        Reason = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "standard_cost", ["previousStandard"] = previous, ["newStandard"] = standardCost, ["quantityOnHand"] = quantity, ["note"] = version.Reason },
                        CreatedBy = principal.Principal?.UserId.Value,
                        CreatedAt = clock.UtcNow,
                    });
                }
            }

            context.Pending.Enqueue((scope, effectiveFrom));
        }

        var drained = await DrainAsync(context, cancellationToken);
        if (drained.IsFailure)
        {
            return drained.Error!;
        }

        if (context.Run is not null)
        {
            version.RevaluationRunId = context.Run.Id;
        }

        await CompleteAsync(context, cancellationToken);
        await audit.RecordAsync(new AuditEntry("standard_cost", version.Id, $"{item.Code}@{effectiveFrom:yyyy-MM-dd}", AuditActions.Created, After: new { company = company.Code, item = item.Code, standardCost, effectiveFrom, reason = version.Reason, revaluationRunId = version.RevaluationRunId }, CompanyId: companyId), cancellationToken);
        return version;
    }

    // ------------------------------------------------------------------ background continuation

    /// <summary>Runs a queued re-application (the job path) or one requested explicitly.</summary>
    public async Task<Result<CostAdjustmentRunInfo>> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.CostRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound("cost_adjustment_run", runId);
        }

        if (run.Status != "queued")
        {
            return Map(run);
        }

        var company = await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", run.CompanyId);
        }

        run.Status = "running";
        run.StartedAt = clock.UtcNow;
        var context = new RunContext
        {
            Trigger = new Trigger(run.TriggerKind, run.TriggerDocumentType, run.TriggerDocumentId, run.TriggerSleId, null),
            InBackground = true,
            DocumentType = RunDocumentType,
            DocumentId = run.Id,
            Run = run,
        };
        context.Companies[company.Id.Value] = company;
        context.Pending.Enqueue((new ScopeKey(run.CompanyId, run.ItemId, run.WarehouseId), run.FromDate));
        var drained = await DrainAsync(context, cancellationToken);
        if (drained.IsFailure)
        {
            // The failure is recorded on the run; the job's own unit of work rolls the walk back.
            return drained.Error!;
        }

        await CompleteAsync(context, cancellationToken);
        return Map(run);
    }

    // ------------------------------------------------------------------ cost inquiry

    public async Task<ItemCostInfo?> CostAsync(Guid companyId, Guid itemId, Guid? warehouseId = null, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var item = await items.FindAsync(itemId, cancellationToken);
        if (company is null || item is null)
        {
            return null;
        }

        var date = asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var scope = new ScopeKey(companyId, itemId, company.CostingScope == "warehouse" ? warehouseId ?? None : None);
        var context = new RunContext { Trigger = new Trigger("inquiry", "inquiry", Guid.Empty, null, null), InBackground = false, DocumentType = "inquiry", DocumentId = Guid.Empty };
        context.Companies[companyId] = company;
        var method = await MethodAsync(context, scope, cancellationToken);
        var bucket = await db.ItemCosts.Where(c => c.CompanyId == scope.CompanyId && c.ItemId == scope.ItemId && c.WarehouseId == scope.WarehouseId && c.ValuationDate <= date)
            .OrderByDescending(static c => c.ValuationDate).FirstOrDefaultAsync(cancellationToken);
        var scopeRow = await db.CostScopes.SingleOrDefaultAsync(s => s.CompanyId == scope.CompanyId && s.ItemId == scope.ItemId && s.WarehouseId == scope.WarehouseId, cancellationToken);
        var standard = method.StandardOn(date);
        var lastCost = bucket?.LastCost ?? scopeRow?.LastCost ?? 0m;
        var expected = ExpectedUnitCost(method, lastCost, bucket?.AverageUnitCost ?? 0m, date);
        return new ItemCostInfo(companyId, itemId, scope.WarehouseId == None ? null : scope.WarehouseId, method.Costing, company.CostingScope, date,
            N(bucket?.Quantity ?? 0m), bucket?.Value ?? 0m, bucket?.AverageUnitCost ?? 0m, lastCost, standard, expected, scopeRow?.ValuationPending ?? false);
    }

    // ------------------------------------------------------------------ the walk

    /// <summary>Walks every pending (scope, from date) until none is left; transfers enqueue their destination scopes.</summary>
    private async Task<Result> DrainAsync(RunContext context, CancellationToken cancellationToken)
    {
        var guard = 0;
        while (context.Pending.TryDequeue(out var next))
        {
            if (++guard > 200)
            {
                return Error.Conflict("costing.reapplication_diverged", "The re-application did not settle after 200 scope walks; the run was abandoned.").WithWhy(("scope", next.Scope.LockOrder), ("fromDate", next.From));
            }

            var walked = await WalkAsync(context, next.Scope, next.From, cancellationToken);
            if (walked.IsFailure)
            {
                return walked.Error!;
            }

            if (context.Queued)
            {
                break;
            }
        }

        return Result.Success();
    }

    private async Task<Result> WalkAsync(RunContext context, ScopeKey scope, DateOnly fromDate, CancellationToken cancellationToken)
    {
        await LockScopeAsync(scope, cancellationToken);
        var method = await MethodAsync(context, scope, cancellationToken);
        var scopeRow = await db.CostScopes.SingleAsync(s => s.CompanyId == scope.CompanyId && s.ItemId == scope.ItemId && s.WarehouseId == scope.WarehouseId, cancellationToken);
        var entries = await db.Entries
            .Where(e => e.CompanyId == scope.CompanyId && e.ItemId == scope.ItemId && e.Ownership == "own" && e.PostingDate >= fromDate && (scope.WarehouseId == None || e.WarehouseId == scope.WarehouseId))
            .OrderBy(static e => e.PostingDate).ThenBy(static e => e.Sequence).ToListAsync(cancellationToken);
        var pendingLoose = context.PendingValueEntries.Where(v => v.SleId is null && v.CompanyId == scope.CompanyId && v.ItemId == scope.ItemId && v.ValuationDate >= fromDate && (scope.WarehouseId == None || v.WarehouseId == scope.WarehouseId)).ToList();
        if (entries.Count == 0 && pendingLoose.Count == 0)
        {
            return Result.Success();
        }

        if (!context.InBackground && entries.Count > options.RecostThreshold)
        {
            return await QueueAsync(context, scope, fromDate, scopeRow, cancellationToken);
        }

        var ids = entries.Select(static e => e.Id).ToList();
        var existingValues = (await db.ValueEntries.Where(v => v.SleId != null && ids.Contains(v.SleId.Value)).ToListAsync(cancellationToken))
            .Concat(context.PendingValueEntries.Where(v => v.SleId is not null && ids.Contains(v.SleId.Value)))
            .GroupBy(static v => v.SleId!.Value).ToDictionary(static g => g.Key, static g => g.ToList());
        var looseValues = (await db.ValueEntries.Where(v => v.SleId == null && v.CompanyId == scope.CompanyId && v.ItemId == scope.ItemId && v.ValuationDate >= fromDate && (scope.WarehouseId == None || v.WarehouseId == scope.WarehouseId)).ToListAsync(cancellationToken))
            .Concat(pendingLoose)
            .GroupBy(static v => v.ValuationDate).ToDictionary(static g => g.Key, static g => g.Sum(static v => v.Amount));
        var previousApplications = await db.Applications.Where(a => a.SupersededBy == null && ids.Contains(a.OutboundSleId)).ToListAsync(cancellationToken);
        var touchesExisting = entries.Any(e => existingValues.ContainsKey(e.Id)) || previousApplications.Count > 0;
        if (touchesExisting && context.Run is null)
        {
            context.Run = NewRun(context, scope, fromDate);
            db.CostRuns.Add(context.Run);
        }

        var runId = context.Run?.Id;
        if (previousApplications.Count > 0)
        {
            foreach (var application in previousApplications)
            {
                application.SupersededBy = runId;
            }

            // Flushed now so the layer query below sees them as superseded.
            await db.SaveChangesAsync(cancellationToken);
        }

        var uow = unitOfWork.Current;
        var opening = await db.ItemCosts.Where(c => c.CompanyId == scope.CompanyId && c.ItemId == scope.ItemId && c.WarehouseId == scope.WarehouseId && c.ValuationDate < fromDate)
            .OrderByDescending(static c => c.ValuationDate).FirstOrDefaultAsync(cancellationToken);
        var runningQuantity = opening?.Quantity ?? 0m;
        var runningValue = opening?.Value ?? 0m;
        var lastCost = opening?.LastCost ?? scopeRow.LastCost;
        var lastAverage = opening?.AverageUnitCost ?? 0m;
        var layers = new List<Layer>();
        if (method.Costing == CostingMethods.Fifo)
        {
            var rows = await uow.Connection.QueryAsync<LayerRow>(new CommandDefinition("""
                SELECT e.id, e.posting_date, e.sequence, e.quantity,
                       coalesce((SELECT sum(a.quantity) FROM app.inv_item_applications a JOIN app.inv_stock_ledger_entries o ON o.tenant_id = a.tenant_id AND o.id = a.outbound_sle_id
                                 WHERE a.tenant_id = e.tenant_id AND a.inbound_sle_id = e.id AND a.superseded_by IS NULL AND o.posting_date < @from), 0) AS applied,
                       coalesce((SELECT sum(a.cost_amount) FROM app.inv_item_applications a JOIN app.inv_stock_ledger_entries o ON o.tenant_id = a.tenant_id AND o.id = a.outbound_sle_id
                                 WHERE a.tenant_id = e.tenant_id AND a.inbound_sle_id = e.id AND a.superseded_by IS NULL AND o.posting_date < @from), 0) AS applied_value,
                       coalesce((SELECT sum(v.cost_amount_actual + v.cost_amount_expected) FROM app.inv_stock_value_entries v WHERE v.tenant_id = e.tenant_id AND v.sle_id = e.id AND v.account_role IN ('Inventory', 'InventoryInTransit')), 0) AS value
                FROM app.inv_stock_ledger_entries e
                WHERE e.company_id = @company AND e.item_id = @item AND e.ownership = 'own' AND e.quantity > 0 AND e.posting_date < @from
                  AND (@warehouse::uuid = '00000000-0000-0000-0000-000000000000'::uuid OR e.warehouse_id = @warehouse)
                ORDER BY e.posting_date, e.sequence
                """, new { company = scope.CompanyId, item = scope.ItemId, warehouse = scope.WarehouseId, from = fromDate }, uow.Transaction, cancellationToken: cancellationToken));
            foreach (var row in rows)
            {
                var remaining = row.Quantity - row.Applied;
                if (remaining <= 0m)
                {
                    continue;
                }

                layers.Add(new Layer { SleId = row.Id, Date = row.PostingDate, Sequence = row.Sequence, Quantity = row.Quantity, RemainingQuantity = remaining, Value = row.Value, RemainingValue = row.Value - row.AppliedValue });
            }

            // The opening quantity and value of a FIFO scope are what its open layers hold.
            runningQuantity = layers.Sum(static l => l.RemainingQuantity);
            runningValue = layers.Sum(static l => l.RemainingValue);
        }

        var buckets = new SortedDictionary<DateOnly, (decimal Quantity, decimal Value, decimal Average, decimal LastCost)>();
        var shortfalls = new List<(StockLedgerEntry Entry, decimal Quantity, decimal ExpectedCost)>();
        var newAmounts = new Dictionary<Guid, (decimal Amount, decimal UnitCost, bool AtExpected, List<ItemApplication> Applications)>();
        var now = clock.UtcNow;
        var byDay = entries.GroupBy(static e => e.PostingDate).ToDictionary(static g => g.Key, static g => g.ToList());
        foreach (var date in byDay.Keys.Union(looseValues.Keys).Order())
        {
            if (looseValues.TryGetValue(date, out var loose))
            {
                runningValue += loose;
            }

            var day = byDay.GetValueOrDefault(date) ?? [];
            var ordered = day.Where(static e => e.Quantity > 0m).OrderBy(static e => e.Sequence).Concat(day.Where(static e => e.Quantity < 0m).OrderBy(static e => e.Sequence)).ToList();
            foreach (var entry in ordered)
            {
                context.EntriesWalked++;
                var quantity = Math.Abs(entry.Quantity);
                if (!existingValues.TryGetValue(entry.Id, out var existing))
                {
                    existing = [];
                    existingValues[entry.Id] = existing;
                }

                var current = existing.Where(static v => IsInventoryRole(v.AccountRole)).Sum(static v => v.Amount);
                var offsetRole = OffsetRoleFor(entry.EntryType);
                var offsetRef = OffsetRefFor(entry.EntryType, entry);
                var accountRole = await InventoryRoleForAsync(entry.WarehouseId, cancellationToken);
                if (entry.Quantity > 0m)
                {
                    // ---- inbound: the value comes with the document, is carried across a transfer, or is the current cost.
                    decimal amount;
                    var atExpected = false;
                    var carried = false;
                    if (entry.EntryType == StockEntryTypes.TransferIn && entry.TransferPairId is { } pairId)
                    {
                        amount = context.PairAmounts.TryGetValue(pairId, out var paired) ? paired : await PairedOutAmountAsync(pairId, entry.Id, cancellationToken);
                        carried = true;
                    }
                    else if (entry.EnteredUnitCost is { } enteredUnitCost)
                    {
                        amount = method.Rounding.Round(enteredUnitCost * Math.Abs(entry.EnteredQuantity), method.Currency.MinorUnits);
                    }
                    else if (entry.AppliesToSleId is { } reverses)
                    {
                        var (reversedAmount, reversedQuantity) = await AmountOfAsync(reverses, cancellationToken);
                        amount = reversedQuantity == 0m ? 0m : method.Rounding.Round(quantity * (reversedAmount / reversedQuantity), method.Currency.MinorUnits);
                        carried = true;
                    }
                    else
                    {
                        amount = method.Rounding.Round(quantity * ExpectedUnitCost(method, lastCost, lastAverage, date), method.Currency.MinorUnits);
                        atExpected = true;
                    }

                    var inventoryAmount = amount;
                    var variance = 0m;
                    if (method.Costing == CostingMethods.Standard && method.StandardOn(date) is { } standardOnDate)
                    {
                        inventoryAmount = method.Rounding.Round(quantity * standardOnDate, method.Currency.MinorUnits);
                        variance = amount - inventoryAmount;
                    }

                    if (existing.Count == 0)
                    {
                        var isExpected = entry.CostIsExpected && !carried;
                        var primary = NewValueEntry(entry, method, isExpected ? ValueEntryTypes.ExpectedCost : ValueEntryTypes.DirectCost, entry.Quantity, isExpected ? 0m : inventoryAmount, isExpected ? inventoryAmount : 0m, accountRole, offsetRole, offsetRef, await GlDateAsync(context, method.Company, date, cancellationToken), ReasonFor(context, "valued", null, inventoryAmount, carried ? "carried from the paired transfer entry" : atExpected ? "no cost on the document: valued at the expected cost" : null), null, runId);
                        primary.CostedAtExpected = atExpected;
                        context.PendingValueEntries.Add(primary);
                        existing.Add(primary);
                        if (variance != 0m)
                        {
                            var varianceEntry = NewValueEntry(entry, method, ValueEntryTypes.Variance, entry.Quantity, variance, 0m, VarianceRoleFor(entry.EntryType), offsetRole, offsetRef, primary.PostingDate, ReasonFor(context, "variance", null, variance, "actual cost against the standard"), primary.Id, runId);
                            context.PendingValueEntries.Add(varianceEntry);
                            existing.Add(varianceEntry);
                        }
                    }
                    else if (current != inventoryAmount && (carried || method.Costing == CostingMethods.Standard))
                    {
                        // A carried cost changed upstream, or the standard changed: the difference is an adjustment on this entry.
                        var delta = inventoryAmount - current;
                        var adjustment = NewValueEntry(entry, method, ValueEntryTypes.CostAdjustment, entry.Quantity, delta, 0m, accountRole, method.Costing == CostingMethods.Standard && !carried ? AccountRoles.InventoryWriteDown : offsetRole, offsetRef, await GlDateAsync(context, method.Company, date, cancellationToken), ReasonFor(context, "reapplied", current, inventoryAmount, carried ? "the paired transfer entry was recosted" : "the standard cost changed"), existing[0].Id, runId);
                        context.PendingValueEntries.Add(adjustment);
                        existing.Add(adjustment);
                        context.AmountAdjusted += Math.Abs(delta);
                        context.EntriesReapplied++;
                    }
                    else
                    {
                        inventoryAmount = current;
                    }

                    var unitCost = quantity == 0m ? 0m : inventoryAmount / quantity;
                    if (!atExpected)
                    {
                        lastCost = unitCost;
                    }

                    if (method.Costing == CostingMethods.Fifo)
                    {
                        layers.Add(new Layer { SleId = entry.Id, Date = date, Sequence = entry.Sequence, Quantity = quantity, RemainingQuantity = quantity, Value = inventoryAmount, RemainingValue = inventoryAmount });
                    }

                    runningQuantity += quantity;
                    runningValue += inventoryAmount;
                    newAmounts[entry.Id] = (inventoryAmount, unitCost, atExpected, []);
                }
                else
                {
                    // ---- outbound: applied to layers (FIFO), at the day's average, or at the standard.
                    var applications = new List<ItemApplication>();
                    decimal total;
                    var atExpected = false;
                    if (method.Costing == CostingMethods.Fifo)
                    {
                        var remaining = quantity;
                        total = 0m;
                        IEnumerable<Layer> candidates = layers.Where(l => l.RemainingQuantity > 0m && l.Date <= date).OrderBy(static l => l.Date).ThenBy(static l => l.Sequence);
                        if (entry.AppliesToSleId is { } specific)
                        {
                            candidates = candidates.OrderBy(l => l.SleId == specific ? 0 : 1).ThenBy(static l => l.Date).ThenBy(static l => l.Sequence);
                        }

                        foreach (var layer in candidates.ToList())
                        {
                            if (remaining <= 0m)
                            {
                                break;
                            }

                            var take = Math.Min(remaining, layer.RemainingQuantity);
                            var amount = take == layer.RemainingQuantity ? layer.RemainingValue : method.Rounding.Round(take * layer.UnitCost, method.Currency.MinorUnits);
                            layer.RemainingQuantity -= take;
                            layer.RemainingValue -= amount;
                            remaining -= take;
                            total += amount;
                            applications.Add(new ItemApplication { Id = Guid.CreateVersion7(), CompanyId = scope.CompanyId, ItemId = scope.ItemId, OutboundSleId = entry.Id, InboundSleId = layer.SleId, Quantity = take, CostAmount = amount, IsReapplication = existing.Count > 0, RunId = runId, AppliedAt = now });
                        }

                        if (remaining > 0m)
                        {
                            var expectedCost = ExpectedUnitCost(method, lastCost, lastAverage, date);
                            shortfalls.Add((entry, remaining, expectedCost));
                            total += method.Rounding.Round(remaining * expectedCost, method.Currency.MinorUnits);
                            atExpected = true;
                        }
                    }
                    else if (method.Costing == CostingMethods.Average)
                    {
                        var poolQuantity = runningQuantity;
                        var poolValue = runningValue;
                        if (poolQuantity - quantity == 0m && poolQuantity > 0m)
                        {
                            total = poolValue;
                        }
                        else if (poolQuantity > 0m)
                        {
                            var average = poolValue / poolQuantity;
                            total = method.Rounding.Round(quantity * average, method.Currency.MinorUnits);
                            if (poolQuantity - quantity < 0m)
                            {
                                atExpected = true;
                            }
                        }
                        else
                        {
                            total = method.Rounding.Round(quantity * ExpectedUnitCost(method, lastCost, lastAverage, date), method.Currency.MinorUnits);
                            atExpected = true;
                        }
                    }
                    else
                    {
                        total = method.Rounding.Round(quantity * (method.StandardOn(date) ?? ExpectedUnitCost(method, lastCost, lastAverage, date)), method.Currency.MinorUnits);
                    }

                    var newAmount = -total;
                    if (existing.Count == 0)
                    {
                        var primary = NewValueEntry(entry, method, ValueEntryTypes.DirectCost, entry.Quantity, newAmount, 0m, accountRole, offsetRole, offsetRef, await GlDateAsync(context, method.Company, date, cancellationToken), ReasonFor(context, "valued", null, newAmount, atExpected ? "no layer or balance to apply to: valued at the expected cost" : null), null, runId);
                        primary.CostedAtExpected = atExpected;
                        context.PendingValueEntries.Add(primary);
                        existing.Add(primary);
                    }
                    else if (current != newAmount)
                    {
                        var delta = newAmount - current;
                        var adjustment = NewValueEntry(entry, method, ValueEntryTypes.CostAdjustment, entry.Quantity, delta, 0m, accountRole, offsetRole, offsetRef, await GlDateAsync(context, method.Company, date, cancellationToken), ReasonFor(context, "reapplied", current, newAmount, null), existing[0].Id, runId);
                        adjustment.CostedAtExpected = atExpected;
                        context.PendingValueEntries.Add(adjustment);
                        existing.Add(adjustment);
                        context.AmountAdjusted += Math.Abs(delta);
                        context.EntriesReapplied++;
                    }
                    else if (previousApplications.Any(a => a.OutboundSleId == entry.Id))
                    {
                        context.EntriesReapplied++;
                    }

                    context.PendingApplications.AddRange(applications);
                    if (entry.EntryType == StockEntryTypes.TransferOut && entry.TransferPairId is { } pair)
                    {
                        context.PairAmounts[pair] = total;
                        await EnqueuePairedInAsync(context, scope, pair, entry.Id, total, cancellationToken);
                    }

                    runningQuantity -= quantity;
                    runningValue += newAmount;
                    newAmounts[entry.Id] = (newAmount, quantity == 0m ? 0m : total / quantity, atExpected, applications);
                }
            }

            if (runningQuantity > 0m)
            {
                lastAverage = runningValue / runningQuantity;
            }

            buckets[date] = (runningQuantity, runningValue, lastAverage, lastCost);
        }

        // FIFO shortfalls (negative stock, or an outbound dated before its receipt) apply forward to the layers that came later.
        if (shortfalls.Count > 0)
        {
            foreach (var (entry, shortQuantity, expectedCost) in shortfalls)
            {
                var remaining = shortQuantity;
                var expectedAmount = method.Rounding.Round(shortQuantity * expectedCost, method.Currency.MinorUnits);
                var applied = 0m;
                foreach (var layer in layers.Where(l => l.RemainingQuantity > 0m && l.Date > entry.PostingDate).OrderBy(static l => l.Date).ThenBy(static l => l.Sequence).ToList())
                {
                    if (remaining <= 0m)
                    {
                        break;
                    }

                    var take = Math.Min(remaining, layer.RemainingQuantity);
                    var amount = take == layer.RemainingQuantity ? layer.RemainingValue : method.Rounding.Round(take * layer.UnitCost, method.Currency.MinorUnits);
                    layer.RemainingQuantity -= take;
                    layer.RemainingValue -= amount;
                    remaining -= take;
                    applied += amount;
                    context.PendingApplications.Add(new ItemApplication { Id = Guid.CreateVersion7(), CompanyId = scope.CompanyId, ItemId = scope.ItemId, OutboundSleId = entry.Id, InboundSleId = layer.SleId, Quantity = take, CostAmount = amount, IsReapplication = true, RunId = runId, AppliedAt = now });
                }

                if (remaining < shortQuantity)
                {
                    // Part or all of the shortfall found a later layer: replace the expected value of that part by the applied one.
                    var stillExpected = method.Rounding.Round(remaining * expectedCost, method.Currency.MinorUnits);
                    var delta = -(applied + stillExpected - expectedAmount);
                    if (delta != 0m)
                    {
                        var (amount, _, _, apps) = newAmounts[entry.Id];
                        var existing = existingValues[entry.Id];
                        var adjustment = NewValueEntry(entry, method, ValueEntryTypes.CostAdjustment, entry.Quantity, delta, 0m, await InventoryRoleForAsync(entry.WarehouseId, cancellationToken), OffsetRoleFor(entry.EntryType), OffsetRefFor(entry.EntryType, entry), await GlDateAsync(context, method.Company, entry.PostingDate, cancellationToken), ReasonFor(context, "applied_forward", amount, amount + delta, "a later receipt covered stock issued before it arrived"), existing.Count > 0 ? existing[0].Id : null, runId);
                        adjustment.CostedAtExpected = remaining > 0m;
                        context.PendingValueEntries.Add(adjustment);
                        existing.Add(adjustment);
                        newAmounts[entry.Id] = (amount + delta, Math.Abs(entry.Quantity) == 0m ? 0m : -(amount + delta) / Math.Abs(entry.Quantity), remaining > 0m, apps);
                        context.AmountAdjusted += Math.Abs(delta);
                        foreach (var bucketDate in buckets.Keys.Where(d => d >= entry.PostingDate).ToList())
                        {
                            var b = buckets[bucketDate];
                            buckets[bucketDate] = (b.Quantity, b.Value + delta, b.Quantity > 0m ? (b.Value + delta) / b.Quantity : b.Average, b.LastCost);
                        }

                        if (entry.EntryType == StockEntryTypes.TransferOut && entry.TransferPairId is { } pair)
                        {
                            context.PairAmounts[pair] = -(amount + delta);
                            await EnqueuePairedInAsync(context, scope, pair, entry.Id, -(amount + delta), cancellationToken);
                        }
                    }
                }
            }
        }

        // Daily buckets from the walk's first date forward, and the scope's last cost.
        foreach (var (date, bucket) in buckets)
        {
            var row = await db.ItemCosts.SingleOrDefaultAsync(c => c.CompanyId == scope.CompanyId && c.ItemId == scope.ItemId && c.WarehouseId == scope.WarehouseId && c.ValuationDate == date, cancellationToken);
            if (row is null)
            {
                row = new ItemCost { CompanyId = scope.CompanyId, ItemId = scope.ItemId, WarehouseId = scope.WarehouseId, ValuationDate = date };
                db.ItemCosts.Add(row);
            }

            row.Quantity = bucket.Quantity;
            row.Value = bucket.Value;
            row.AverageUnitCost = bucket.Average;
            row.LastCost = bucket.LastCost;
            row.StandardCost = method.StandardOn(date);
            row.UpdatedAt = now;
        }

        scopeRow.LastCost = lastCost;
        scopeRow.UpdatedAt = now;
        if (context.InBackground && context.Run is not null && scopeRow.PendingRunId == context.Run.Id)
        {
            scopeRow.ValuationPending = false;
            scopeRow.PendingRunId = null;
        }

        return await FlushAsync(context, cancellationToken);
    }

    /// <summary>Posts the journals for the value entries of this walk and writes them with the applications, so the next walk's queries see them.</summary>
    private async Task<Result> FlushAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (context.PendingValueEntries.Count > 0)
        {
            var journal = await PostJournalsAsync(context, cancellationToken);
            if (journal.IsFailure)
            {
                return journal.Error!;
            }
        }

        db.ValueEntries.AddRange(context.PendingValueEntries);
        db.Applications.AddRange(context.PendingApplications);
        context.AllValueEntries.AddRange(context.PendingValueEntries);
        context.PendingValueEntries.Clear();
        context.PendingApplications.Clear();
        context.Flushes++;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> QueueAsync(RunContext context, ScopeKey scope, DateOnly fromDate, ItemCostScope scopeRow, CancellationToken cancellationToken)
    {
        var run = context.Run ?? NewRun(context, scope, fromDate);
        run.Status = "queued";
        run.WarehouseId = scope.WarehouseId;
        run.FromDate = fromDate;
        if (context.Run is null)
        {
            db.CostRuns.Add(run);
            context.Run = run;
        }

        run.JobId = await jobs.EnqueueAsync(new JobRequest(CostRecostJob.JobType, new CostRecostPayload(run.Id), Priority: 10, IdempotencyKey: "costing:" + run.Id.ToString("N")), cancellationToken);
        scopeRow.ValuationPending = true;
        scopeRow.PendingRunId = run.Id;
        scopeRow.UpdatedAt = clock.UtcNow;
        context.Queued = true;
        return Result.Success();
    }

    // ------------------------------------------------------------------ the books

    /// <summary>One journal per GL date for the value entries of this call, through the accounting engine (POSTING_RULES §4).</summary>
    private async Task<Result> PostJournalsAsync(RunContext context, CancellationToken cancellationToken)
    {
        var documentType = context.DocumentType;
        var documentId = context.DocumentId;
        foreach (var group in context.PendingValueEntries.GroupBy(static v => (v.CompanyId, v.PostingDate)).OrderBy(static g => g.Key.PostingDate))
        {
            var company = context.Companies[group.Key.CompanyId];
            var lines = new Dictionary<string, (PostingLine Line, decimal Amount)>(StringComparer.Ordinal);
            foreach (var value in group)
            {
                var keys = new PostingKeys(DocumentType: documentType, ItemPostingGroupId: value.ItemPostingGroupId, WarehouseId: value.WarehouseId);
                Accumulate(lines, value.AccountRole, value.Amount, keys, value.ItemId, value.OffsetRef);
                Accumulate(lines, value.OffsetRole, -value.Amount, keys, value.ItemId, value.OffsetRef);
            }

            var postingLines = lines.Values.Where(static l => l.Amount != 0m).Select(static l => l.Line with { Amount = l.Amount }).ToList();
            if (postingLines.Count == 0)
            {
                continue;
            }

            var reference = $"inventory:{documentType}:{(context.StockPostingId ?? documentId):N}:{group.Key.PostingDate:yyyyMMdd}:{context.Run?.Id.ToString("N") ?? "0"}:{context.Flushes}";
            var description = LocalizedText.Bilingual($"Inventory {documentType.Replace('_', ' ')}", $"المخزون {documentType.Replace('_', ' ')}");
            var posted = await posting.PostAsync(new PostingRequest(company.Id, "inventory", documentType, documentId, group.Key.PostingDate, company.FunctionalCurrency.Code, postingLines, null, group.Key.PostingDate, description, null, RateTypes.Spot, null, null, reference), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            foreach (var value in group)
            {
                value.GlJournalEntryId = posted.Value.EntryId;
            }

            context.JournalsPosted++;
            context.FirstJournalId ??= posted.Value.EntryId;
            context.FirstJournalNumber ??= posted.Value.Number;
        }

        return Result.Success();
    }

    private static void Accumulate(Dictionary<string, (PostingLine Line, decimal Amount)> lines, string role, decimal amount, PostingKeys keys, Guid itemId, Guid? offsetRef)
    {
        string? subledgerType = null;
        Guid? subledgerRef = null;
        if (AccountRoles.ControlSubledgers.TryGetValue(role, out var subledger))
        {
            subledgerType = subledger;
            subledgerRef = subledger == SubledgerTypes.Inventory ? itemId : offsetRef ?? itemId;
        }

        var key = string.Join('|', role, keys.ItemPostingGroupId, keys.WarehouseId, subledgerType, subledgerRef);
        if (lines.TryGetValue(key, out var existing))
        {
            lines[key] = (existing.Line, existing.Amount + amount);
        }
        else
        {
            lines[key] = (new PostingLine(role, amount, keys, SubledgerType: subledgerType, SubledgerRef: subledgerRef), amount);
        }
    }

    /// <summary>Flushes anything left (loose value entries with no walk), closes the run and records it.</summary>
    private async Task CompleteAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (context.PendingValueEntries.Count > 0 || context.PendingApplications.Count > 0)
        {
            var flushed = await FlushAsync(context, cancellationToken);
            if (flushed.IsFailure)
            {
                throw new InvalidOperationException($"costing flush failed: {flushed.Error!.Code} {flushed.Error.Message}");
            }
        }

        if (context.Run is { } run)
        {
            run.EntriesWalked += context.EntriesWalked;
            run.EntriesReapplied += context.EntriesReapplied;
            run.ValueEntriesCreated += context.AllValueEntries.Count(static v => v.ValueType == ValueEntryTypes.CostAdjustment);
            run.JournalEntriesPosted += context.JournalsPosted;
            run.AmountAdjusted += context.AmountAdjusted;
            if (!context.Queued)
            {
                run.Status = "completed";
                run.CompletedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (context.Run is { } completed && !context.Queued)
        {
            await audit.RecordAsync(new AuditEntry(RunDocumentType, completed.Id, completed.Id.ToString("N")[^12..], AuditActions.Posted, After: new
            {
                trigger = new { kind = completed.TriggerKind, documentType = completed.TriggerDocumentType, documentId = completed.TriggerDocumentId, sleId = completed.TriggerSleId },
                fromDate = completed.FromDate,
                entriesWalked = completed.EntriesWalked,
                entriesReapplied = completed.EntriesReapplied,
                valueEntriesCreated = completed.ValueEntriesCreated,
                journalEntriesPosted = completed.JournalEntriesPosted,
                amountAdjusted = completed.AmountAdjusted,
            }, CompanyId: completed.CompanyId), cancellationToken);
        }
    }

    // ------------------------------------------------------------------ helpers

    private CostAdjustmentRun NewRun(RunContext context, ScopeKey scope, DateOnly fromDate) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = scope.CompanyId,
        ItemId = scope.ItemId,
        WarehouseId = scope.WarehouseId,
        TriggerKind = context.Trigger.Kind,
        TriggerDocumentType = context.Trigger.DocumentType,
        TriggerDocumentId = context.Trigger.DocumentId,
        TriggerSleId = context.Trigger.SleId,
        FromDate = fromDate,
        Status = "running",
        StartedBy = principal.Principal?.UserId.Value,
        StartedAt = clock.UtcNow,
    };

    private StockValueEntry NewValueEntry(StockLedgerEntry entry, Method method, string valueType, decimal valuedQuantity, decimal actual, decimal expected, string accountRole, string offsetRole, Guid? offsetRef, DateOnly glDate, Dictionary<string, object?> reason, Guid? adjusts, Guid? runId) => new()
    {
        Id = Guid.CreateVersion7(),
        SleId = entry.Id,
        CompanyId = entry.CompanyId,
        ItemId = entry.ItemId,
        WarehouseId = entry.WarehouseId,
        PostingDate = glDate,
        ValuationDate = entry.PostingDate,
        ValueType = valueType,
        ValuedQuantity = valuedQuantity,
        UnitCost = valuedQuantity == 0m ? 0m : (actual + expected) / Math.Abs(valuedQuantity),
        CostAmountActual = actual,
        CostAmountExpected = expected,
        Currency = method.Currency.Code,
        AccountRole = accountRole,
        OffsetRole = offsetRole,
        OffsetRef = offsetRef,
        ItemPostingGroupId = method.PostingGroupId,
        AdjustsSveId = adjusts,
        AdjustmentRunId = runId,
        SourceDocumentType = entry.SourceDocumentType,
        SourceDocumentId = entry.SourceDocumentId,
        Reason = reason,
        CreatedBy = principal.Principal?.UserId.Value,
        CreatedAt = clock.UtcNow,
    };

    private static Dictionary<string, object?> ReasonFor(RunContext context, string kind, decimal? previous, decimal current, string? note) => new(StringComparer.Ordinal)
    {
        ["kind"] = kind,
        ["trigger"] = new { kind = context.Trigger.Kind, documentType = context.Trigger.DocumentType, documentId = context.Trigger.DocumentId, sleId = context.Trigger.SleId },
        ["runId"] = context.Run?.Id,
        ["previousAmount"] = previous,
        ["newAmount"] = current,
        ["note"] = note ?? context.Trigger.Note,
    };

    private async Task<Method> MethodAsync(RunContext context, ScopeKey scope, CancellationToken cancellationToken)
    {
        if (!context.Companies.TryGetValue(scope.CompanyId, out var company))
        {
            company = await companies.FindAsync(new CompanyId(scope.CompanyId), cancellationToken) ?? throw new InvalidOperationException($"company {scope.CompanyId} not found");
            context.Companies[scope.CompanyId] = company;
        }

        var item = await items.FindAsync(scope.ItemId, cancellationToken) ?? throw new InvalidOperationException($"item {scope.ItemId} not found");
        var policy = await items.CompanyPolicyAsync(scope.ItemId, scope.CompanyId, cancellationToken);
        var costing = policy?.CostingMethodOverride ?? item.CostingMethodOverride ?? company.CostingMethod;
        var standards = await db.StandardCosts.Where(v => v.CompanyId == scope.CompanyId && v.ItemId == scope.ItemId).ToListAsync(cancellationToken);
        return new Method
        {
            Costing = CostingMethods.All.Contains(costing, StringComparer.Ordinal) ? costing : CostingMethods.Average,
            Scope = company.CostingScope,
            Currency = company.FunctionalCurrency,
            Rounding = new RoundingPolicy(company.RoundingMode),
            PostingGroupId = policy?.ItemPostingGroupOverride ?? item.ItemPostingGroupId,
            Item = item,
            Company = company,
            Standards = standards,
            SettingStandard = policy?.StandardCost,
        };
    }

    private static ScopeKey ScopeOf(StockLedgerEntry entry, CompanyInfo company) => new(entry.CompanyId, entry.ItemId, company.CostingScope == "warehouse" ? entry.WarehouseId : None);

    private async Task LockScopeAsync(ScopeKey scope, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var parameters = new { tenant = uow.Context.TenantId.Value, company = scope.CompanyId, item = scope.ItemId, warehouse = scope.WarehouseId };
        await uow.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO app.inv_item_cost_scopes (tenant_id, company_id, item_id, warehouse_id) VALUES (@tenant, @company, @item, @warehouse) ON CONFLICT DO NOTHING", parameters, uow.Transaction, cancellationToken: cancellationToken));
        await uow.Connection.ExecuteAsync(new CommandDefinition("SELECT 1 FROM app.inv_item_cost_scopes WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND warehouse_id = @warehouse FOR UPDATE", parameters, uow.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>The GL date of a value entry: the movement's date when its inventory period takes postings from this actor, else the first open period's start (ADR-0008).</summary>
    private async Task<DateOnly> GlDateAsync(RunContext context, CompanyInfo company, DateOnly date, CancellationToken cancellationToken)
    {
        if (context.GlDates.TryGetValue((company.Id.Value, date), out var known))
        {
            return known;
        }

        var mayPostInSoftClosed = principal.Principal?.Has(InventoryPermissions.PostInSoftClosed) ?? true;
        var state = await periods.ResolveAsync(company.Id, date, PostingModules.Inventory, cancellationToken);
        var result = date;
        if (state.IsFailure || !state.Value.AllowsPosting(mayPostInSoftClosed))
        {
            var open = await periods.FirstOpenPeriodAsync(company.Id, date, PostingModules.Inventory, cancellationToken);
            result = open.IsSuccess ? (open.Value.StartsOn > date ? open.Value.StartsOn : date) : date;
        }

        context.GlDates[(company.Id.Value, date)] = result;
        return result;
    }

    private async Task<string> InventoryRoleForAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.FindAsync(warehouseId, cancellationToken);
        return warehouse?.Kind == "in_transit" ? AccountRoles.InventoryInTransit : AccountRoles.Inventory;
    }

    private async Task<decimal> PairedOutAmountAsync(Guid pairId, Guid inboundId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var amount = await uow.Connection.ExecuteScalarAsync<decimal?>(new CommandDefinition("""
            SELECT sum(v.cost_amount_actual + v.cost_amount_expected)
            FROM app.inv_stock_value_entries v JOIN app.inv_stock_ledger_entries e ON e.tenant_id = v.tenant_id AND e.id = v.sle_id
            WHERE e.transfer_pair_id = @pair AND e.quantity < 0 AND e.id <> @inbound AND v.account_role IN ('Inventory', 'InventoryInTransit')
            """, new { pair = pairId, inbound = inboundId }, uow.Transaction, cancellationToken: cancellationToken));
        return -(amount ?? 0m);
    }

    private async Task<(decimal Amount, decimal Quantity)> AmountOfAsync(Guid sleId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleOrDefaultAsync<(decimal? Amount, decimal? Quantity)>(new CommandDefinition("""
            SELECT (SELECT sum(v.cost_amount_actual + v.cost_amount_expected) FROM app.inv_stock_value_entries v WHERE v.tenant_id = e.tenant_id AND v.sle_id = e.id AND v.account_role IN ('Inventory', 'InventoryInTransit')) AS amount,
                   abs(e.quantity) AS quantity
            FROM app.inv_stock_ledger_entries e WHERE e.id = @id
            """, new { id = sleId }, uow.Transaction, cancellationToken: cancellationToken));
        return (Math.Abs(row.Amount ?? 0m), row.Quantity ?? 0m);
    }

    /// <summary>When a transfer's outbound cost changes, its inbound twin (possibly in another scope) is re-valued from its own date.</summary>
    private async Task EnqueuePairedInAsync(RunContext context, ScopeKey scope, Guid pairId, Guid outboundId, decimal amount, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var inbound = await uow.Connection.QuerySingleOrDefaultAsync<PairRow>(new CommandDefinition(
            "SELECT id, company_id, item_id, warehouse_id, posting_date FROM app.inv_stock_ledger_entries WHERE transfer_pair_id = @pair AND quantity > 0 AND id <> @outbound",
            new { pair = pairId, outbound = outboundId }, uow.Transaction, cancellationToken: cancellationToken));
        if (inbound is null)
        {
            return;
        }

        var company = context.Companies[scope.CompanyId];
        var target = new ScopeKey(inbound.CompanyId, inbound.ItemId, company.CostingScope == "warehouse" ? inbound.WarehouseId : None);
        var current = context.PendingValueEntries.Where(v => v.SleId == inbound.Id && IsInventoryRole(v.AccountRole)).Sum(static v => v.Amount)
            + (await uow.Connection.ExecuteScalarAsync<decimal?>(new CommandDefinition("SELECT sum(cost_amount_actual + cost_amount_expected) FROM app.inv_stock_value_entries WHERE sle_id = @id AND account_role IN ('Inventory', 'InventoryInTransit')", new { id = inbound.Id }, uow.Transaction, cancellationToken: cancellationToken)) ?? 0m);
        if (current != amount || target != scope)
        {
            context.Pending.Enqueue((target, inbound.PostingDate));
        }
    }

    private async Task<Guid?> FirstWarehouseAsync(ScopeKey scope, CancellationToken cancellationToken)
    {
        var fromEntries = await db.Entries.Where(e => e.CompanyId == scope.CompanyId && e.ItemId == scope.ItemId).OrderBy(static e => e.Sequence).Select(static e => (Guid?)e.WarehouseId).FirstOrDefaultAsync(cancellationToken);
        return fromEntries ?? (await warehouses.ListAsync(scope.CompanyId, cancellationToken)).FirstOrDefault(static w => w.Kind == "standard")?.Id;
    }

    private static decimal ExpectedUnitCost(Method method, decimal lastCost, decimal lastAverage, DateOnly date)
    {
        if (method.Costing == CostingMethods.Standard && method.StandardOn(date) is { } standard)
        {
            return standard;
        }

        if (method.Costing == CostingMethods.Average && lastAverage > 0m)
        {
            return lastAverage;
        }

        return lastCost > 0m ? lastCost : method.StandardOn(date) ?? 0m;
    }

    internal static bool IsInventoryRole(string role) => role is AccountRoles.Inventory or AccountRoles.InventoryInTransit;

    /// <summary>The other side of Inventory for each movement type (POSTING_RULES §4, DOMAIN_MODEL §9.1).</summary>
    internal static string OffsetRoleFor(string entryType) => entryType switch
    {
        StockEntryTypes.PurchaseReceipt or StockEntryTypes.PurchaseReturn or StockEntryTypes.DropShip => AccountRoles.GRNI,
        StockEntryTypes.SaleShipment or StockEntryTypes.SaleReturn => AccountRoles.Cogs,
        StockEntryTypes.TransferOut or StockEntryTypes.TransferIn => AccountRoles.InventoryInTransit,
        StockEntryTypes.PositiveAdjustment or StockEntryTypes.NegativeAdjustment => AccountRoles.InventoryAdjustment,
        StockEntryTypes.Scrap => AccountRoles.Scrap,
        StockEntryTypes.CountVariance => AccountRoles.CountVariance,
        StockEntryTypes.AssemblyConsumption or StockEntryTypes.AssemblyOutput => AccountRoles.Inventory,
        StockEntryTypes.Opening => AccountRoles.OpeningBalanceEquity,
        _ => AccountRoles.InventoryAdjustment,
    };

    private static string VarianceRoleFor(string entryType) => entryType switch
    {
        StockEntryTypes.PurchaseReceipt or StockEntryTypes.PurchaseReturn or StockEntryTypes.DropShip => AccountRoles.PurchasePriceVariance,
        StockEntryTypes.AssemblyOutput or StockEntryTypes.AssemblyConsumption => AccountRoles.AssemblyVariance,
        _ => AccountRoles.InventoryAdjustment,
    };

    /// <summary>The subledger item of a control-account offset: the receipt document for GRNI, the transit pair for in-transit stock.</summary>
    private static Guid? OffsetRefFor(string entryType, StockLedgerEntry entry) => entryType switch
    {
        StockEntryTypes.PurchaseReceipt or StockEntryTypes.PurchaseReturn or StockEntryTypes.DropShip => entry.SourceDocumentId,
        StockEntryTypes.TransferOut or StockEntryTypes.TransferIn => entry.ItemId,
        StockEntryTypes.AssemblyConsumption or StockEntryTypes.AssemblyOutput => entry.SourceDocumentId,
        _ => null,
    };

    private static decimal N(decimal value) => ItemUomMath.Normalize(value);

    internal static StockValueEntryInfo Map(StockValueEntry v) => new(v.Id, v.SleId, v.PostingDate, v.ValuationDate, v.ValueType, N(v.ValuedQuantity), v.UnitCost, v.CostAmountActual, v.CostAmountExpected, v.Currency, v.AccountRole, v.OffsetRole, v.OffsetRef,
        v.GlJournalEntryId, v.AdjustsSveId, v.AdjustmentRunId, v.SourceDocumentType, v.SourceDocumentId, v.Reason, v.CostedAtExpected, v.CreatedAt);

    internal static CostAdjustmentRunInfo Map(CostAdjustmentRun r) => new(r.Id, r.CompanyId, r.ItemId, r.WarehouseId == None ? null : r.WarehouseId, r.TriggerKind, r.TriggerDocumentType, r.TriggerDocumentId, r.TriggerSleId, r.FromDate, r.Status,
        r.EntriesWalked, r.EntriesReapplied, r.ValueEntriesCreated, r.JournalEntriesPosted, r.AmountAdjusted, r.JobId, r.Error, r.StartedAt, r.CompletedAt);

}

public sealed record CostRecostPayload(Guid RunId);

/// <summary>Continues a re-application that exceeded the synchronous threshold (ADR-0008 §6), inside the run's tenant.</summary>
public sealed class CostRecostJob(CostingService costing) : IJobHandler<CostRecostPayload>
{
    public static string JobType => "inventory.costing.recost";

    public async Task<object?> ExecuteAsync(CostRecostPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        var result = await costing.RunAsync(payload.RunId, cancellationToken);
        if (result.IsFailure)
        {
            throw new JobFailedException($"{result.Error!.Code}: {result.Error.Message}");
        }

        return new { runId = result.Value.Id, status = result.Value.Status, entriesWalked = result.Value.EntriesWalked, entriesReapplied = result.Value.EntriesReapplied, journalEntriesPosted = result.Value.JournalEntriesPosted };
    }
}

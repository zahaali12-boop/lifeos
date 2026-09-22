using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Inventory.Application;

public sealed record ReplenishmentRunRequest(Guid CompanyId, Guid? WarehouseId = null, DateOnly? AsOf = null);

public sealed record AcceptSuggestionRequest(decimal? Quantity = null, Guid? SupplierId = null, string? Note = null);

public sealed record DismissSuggestionRequest(string Reason);

public sealed record ReplenishmentRunSummary(Guid Id, Guid CompanyId, Guid? WarehouseId, DateTimeOffset RanAt, DateOnly AsOf, int ItemsChecked, int SuggestionsCreated, int SuggestionsRefreshed, int SuggestionsClosed, Guid? StartedBy);

public sealed record ReplenishmentSuggestionSummary(
    Guid Id,
    Guid CompanyId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    string BaseUom,
    Guid WarehouseId,
    string WarehouseCode,
    Guid? RunId,
    decimal SuggestedQty,
    Guid? SuggestedSupplierId,
    DateOnly? NeededBy,
    IReadOnlyDictionary<string, object?> Explanation,
    string Status,
    decimal? AcceptedQty,
    Guid? AcceptedSupplierId,
    Guid? PurchaseOrderLineId,
    string? DecisionNote,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The default when no purchasing module reports incoming supply: nothing on its way.</summary>
public sealed class NoIncomingSupply : IIncomingSupply
{
    public Task<IReadOnlyList<IncomingSupplyInfo>> IncomingAsync(Guid companyId, Guid warehouseId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IncomingSupplyInfo>>([]);
}

/// <summary>
/// Replenishment (roadmap 3.7): for every item with a reorder point, minimum or maximum in a warehouse, the projected
/// stock is what is available (on hand − reserved − quality hold) plus what is on its way (transfers in transit and
/// the purchasing module's incoming supply). When the projection is at or below the reorder point (or below the
/// minimum), the planner suggests buying up to the maximum (or, without one, back to the reorder point plus the
/// safety stock), needed by today plus the lead time, and writes the whole arithmetic into the suggestion. A later run
/// refreshes the open suggestion of an item and warehouse and closes it when the need is gone.
/// </summary>
public sealed class ReplenishmentService(
    InventoryDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IIncomingSupply incoming,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    private sealed class StockRow
    {
        public Guid ItemId { get; set; }

        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal QualityHold { get; set; }
    }

    private sealed class TransitRow
    {
        public Guid ItemId { get; set; }

        public decimal Quantity { get; set; }
    }

    public async Task<Result<ReplenishmentRunSummary>> RunAsync(Guid companyId, Guid? warehouseId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var scope = Validation.Scope(principal, InventoryPermissions.ReplenishmentManage, companyId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var today = asOf ?? clock.TodayIn(company.TimeZone);
        var targets = (await warehouses.ListAsync(companyId, cancellationToken)).Where(w => w.IsActive && w.Kind is "standard" && (warehouseId is null || w.Id == warehouseId)).ToList();
        if (warehouseId is { } wanted && targets.Count == 0)
        {
            return Error.Validation("replenishment.warehouse_invalid", "The warehouse must be an active stock warehouse of the company.").WithWhy(("warehouseId", wanted));
        }

        var run = new ReplenishmentRun { Id = Guid.CreateVersion7(), CompanyId = companyId, WarehouseId = warehouseId, RanAt = clock.UtcNow, AsOf = today, StartedBy = principal.Principal?.UserId.Value };
        db.ReplenishmentRuns.Add(run);
        foreach (var warehouse in targets)
        {
            await PlanWarehouseAsync(run, company, warehouse, today, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("replenishment_run", run.Id, run.Id.ToString("N")[^12..], AuditActions.Created, After: new { company = company.Code, warehouseId, asOf = today, run.ItemsChecked, run.SuggestionsCreated, run.SuggestionsRefreshed, run.SuggestionsClosed }, CompanyId: companyId), cancellationToken);
        return Map(run);
    }

    private async Task PlanWarehouseAsync(ReplenishmentRun run, CompanyInfo company, WarehouseInfo warehouse, DateOnly today, CancellationToken cancellationToken)
    {
        var parameters = await items.PlanningParametersAsync(warehouse.Id, cancellationToken);
        var open = await db.ReplenishmentSuggestions.Where(s => s.CompanyId == company.Id.Value && s.WarehouseId == warehouse.Id && s.Status == "open").ToDictionaryAsync(static s => s.ItemId, cancellationToken);
        if (parameters.Count == 0 && open.Count == 0)
        {
            return;
        }

        var uow = unitOfWork.Current;
        var stock = (await uow.Connection.QueryAsync<StockRow>(new CommandDefinition(
            "SELECT item_id, sum(on_hand) AS on_hand, sum(reserved) AS reserved, sum(quality_hold) AS quality_hold FROM app.inv_stock_balances WHERE company_id = @company AND warehouse_id = @warehouse GROUP BY item_id",
            new { company = company.Id.Value, warehouse = warehouse.Id }, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static r => r.ItemId);
        var transit = (await uow.Connection.QueryAsync<TransitRow>(new CommandDefinition("""
            SELECT l.item_id, sum(l.qty_shipped - l.qty_received - l.qty_shortage) AS quantity
            FROM app.inv_transfer_lines l JOIN app.inv_transfers t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            WHERE t.company_id = @company AND t.to_warehouse_id = @warehouse AND t.status IN ('shipped', 'partially_received')
            GROUP BY l.item_id
            """, new { company = company.Id.Value, warehouse = warehouse.Id }, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static r => r.ItemId, static r => r.Quantity);
        var ordered = (await incoming.IncomingAsync(company.Id.Value, warehouse.Id, cancellationToken)).GroupBy(static i => i.ItemId).ToDictionary(static g => g.Key, static g => g.Sum(static i => i.Quantity));
        var stillNeeded = new HashSet<Guid>();
        foreach (var p in parameters)
        {
            run.ItemsChecked++;
            var row = stock.GetValueOrDefault(p.ItemId);
            var onHand = row?.OnHand ?? 0m;
            var reserved = row?.Reserved ?? 0m;
            var hold = row?.QualityHold ?? 0m;
            var available = onHand - reserved - hold;
            var inTransit = transit.GetValueOrDefault(p.ItemId);
            var onOrder = ordered.GetValueOrDefault(p.ItemId);
            var projected = available + inTransit + onOrder;
            var reorderPoint = p.ReorderPoint ?? p.MinQty ?? 0m;
            var trigger = p.ReorderPoint is not null ? "reorder_point" : p.MinQty is not null ? "min_qty" : "max_qty";
            var needed = p.ReorderPoint is not null ? projected <= reorderPoint : p.MinQty is not null ? projected < p.MinQty.Value : projected < p.MaxQty!.Value;
            if (!needed)
            {
                continue;
            }

            var target = p.MaxQty ?? reorderPoint + (p.SafetyStock ?? 0m);
            var quantity = ItemUomMath.Normalize(target - projected);
            if (p.MinQty is { } min && projected + quantity < min)
            {
                quantity = ItemUomMath.Normalize(min - projected);
            }

            if (quantity <= 0m)
            {
                continue;
            }

            var leadTime = p.LeadTimeDays ?? p.SupplierLeadTimeDays;
            var explanation = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["asOf"] = today,
                ["onHand"] = ItemUomMath.Normalize(onHand),
                ["reserved"] = ItemUomMath.Normalize(reserved),
                ["qualityHold"] = ItemUomMath.Normalize(hold),
                ["available"] = ItemUomMath.Normalize(available),
                ["inTransit"] = ItemUomMath.Normalize(inTransit),
                ["onOrder"] = ItemUomMath.Normalize(onOrder),
                ["projected"] = ItemUomMath.Normalize(projected),
                ["reorderPoint"] = p.ReorderPoint,
                ["minQty"] = p.MinQty,
                ["maxQty"] = p.MaxQty,
                ["safetyStock"] = p.SafetyStock,
                ["trigger"] = trigger,
                ["target"] = ItemUomMath.Normalize(target),
                ["formula"] = p.MaxQty is not null ? "max − (available + in transit + on order)" : "reorder point + safety stock − (available + in transit + on order)",
                ["leadTimeDays"] = leadTime,
                ["supplierId"] = p.PreferredSupplierId,
            };
            stillNeeded.Add(p.ItemId);
            if (open.TryGetValue(p.ItemId, out var existing))
            {
                existing.RunId = run.Id;
                existing.SuggestedQty = quantity;
                existing.SuggestedSupplierId = p.PreferredSupplierId;
                existing.NeededBy = leadTime is { } days ? today.AddDays(days) : null;
                existing.Explanation = explanation;
                existing.UpdatedAt = clock.UtcNow;
                run.SuggestionsRefreshed++;
            }
            else
            {
                db.ReplenishmentSuggestions.Add(new ReplenishmentSuggestion
                {
                    Id = Guid.CreateVersion7(),
                    CompanyId = company.Id.Value,
                    ItemId = p.ItemId,
                    WarehouseId = warehouse.Id,
                    RunId = run.Id,
                    SuggestedQty = quantity,
                    SuggestedSupplierId = p.PreferredSupplierId,
                    NeededBy = leadTime is { } days ? today.AddDays(days) : null,
                    Explanation = explanation,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                });
                run.SuggestionsCreated++;
            }
        }

        // Open suggestions the run no longer finds a need for are closed as superseded, with the reason.
        foreach (var (itemId, suggestion) in open)
        {
            if (stillNeeded.Contains(itemId))
            {
                continue;
            }

            suggestion.Status = "superseded";
            suggestion.DecisionNote = "no longer needed at the run of " + today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            suggestion.DecidedAt = clock.UtcNow;
            suggestion.UpdatedAt = clock.UtcNow;
            run.SuggestionsClosed++;
        }
    }

    public async Task<IReadOnlyList<ReplenishmentRunSummary>> RunsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.ReplenishmentRuns.AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        return (await query.OrderByDescending(static r => r.Id).Take(100).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ReplenishmentSuggestionSummary>> SuggestionsAsync(Guid? companyId, Guid? warehouseId, Guid? itemId, string? status, CancellationToken cancellationToken)
    {
        var query = db.ReplenishmentSuggestions.AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(s => s.WarehouseId == w);
        }

        if (itemId is { } i)
        {
            query = query.Where(s => s.ItemId == i);
        }

        var wanted = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
        if (wanted != "all")
        {
            query = query.Where(s => s.Status == wanted);
        }

        var result = new List<ReplenishmentSuggestionSummary>();
        foreach (var suggestion in await query.OrderByDescending(static s => s.UpdatedAt).Take(500).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(suggestion, cancellationToken));
        }

        return result;
    }

    public async Task<ReplenishmentSuggestionSummary?> GetAsync(Guid suggestionId, CancellationToken cancellationToken)
    {
        var suggestion = await db.ReplenishmentSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, cancellationToken);
        return suggestion is null ? null : await MapAsync(suggestion, cancellationToken);
    }

    public async Task<Result<ReplenishmentSuggestionSummary>> AcceptAsync(Guid suggestionId, AcceptSuggestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var suggestion = await db.ReplenishmentSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return Error.NotFound("replenishment_suggestion", suggestionId);
        }

        if (suggestion.Status != "open")
        {
            return Error.Conflict("replenishment.not_open", "The suggestion was already decided.").WithWhy(("status", suggestion.Status));
        }

        if (request.Quantity is { } q && q <= 0m)
        {
            return Error.Validation("replenishment.quantity_invalid", "The accepted quantity is positive.").WithWhy(("quantity", q));
        }

        suggestion.Status = "accepted";
        suggestion.AcceptedQty = request.Quantity ?? suggestion.SuggestedQty;
        suggestion.AcceptedSupplierId = request.SupplierId ?? suggestion.SuggestedSupplierId;
        suggestion.DecisionNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        suggestion.DecidedBy = principal.Principal?.UserId.Value;
        suggestion.DecidedAt = clock.UtcNow;
        suggestion.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("replenishment_suggestion", suggestion.Id, suggestion.Id.ToString("N")[^12..], AuditActions.Approved, After: new { quantity = suggestion.AcceptedQty, supplierId = suggestion.AcceptedSupplierId, note = suggestion.DecisionNote }, CompanyId: suggestion.CompanyId), cancellationToken);
        return await MapAsync(suggestion, cancellationToken);
    }

    public async Task<Result<ReplenishmentSuggestionSummary>> DismissAsync(Guid suggestionId, DismissSuggestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var suggestion = await db.ReplenishmentSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return Error.NotFound("replenishment_suggestion", suggestionId);
        }

        if (suggestion.Status != "open")
        {
            return Error.Conflict("replenishment.not_open", "The suggestion was already decided.").WithWhy(("status", suggestion.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("replenishment.reason_required", "A dismissal gives its reason.");
        }

        suggestion.Status = "dismissed";
        suggestion.DecisionNote = request.Reason.Trim();
        suggestion.DecidedBy = principal.Principal?.UserId.Value;
        suggestion.DecidedAt = clock.UtcNow;
        suggestion.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("replenishment_suggestion", suggestion.Id, suggestion.Id.ToString("N")[^12..], AuditActions.Rejected, After: new { reason = suggestion.DecisionNote }, CompanyId: suggestion.CompanyId), cancellationToken);
        return await MapAsync(suggestion, cancellationToken);
    }

    /// <summary>The daily run for one tenant: every company, every stock warehouse.</summary>
    public async Task<(int Companies, int Suggestions)> PlanAllAsync(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var count = 0;
        var suggestions = 0;
        foreach (var company in await companies.ListAsync(cancellationToken))
        {
            var run = await RunAsync(company.Id.Value, null, asOf, cancellationToken);
            if (run.IsSuccess)
            {
                count++;
                suggestions += run.Value.SuggestionsCreated + run.Value.SuggestionsRefreshed;
            }
        }

        return (count, suggestions);
    }

    private static ReplenishmentRunSummary Map(ReplenishmentRun r) => new(r.Id, r.CompanyId, r.WarehouseId, r.RanAt, r.AsOf, r.ItemsChecked, r.SuggestionsCreated, r.SuggestionsRefreshed, r.SuggestionsClosed, r.StartedBy);

    private async Task<ReplenishmentSuggestionSummary> MapAsync(ReplenishmentSuggestion s, CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(s.ItemId, cancellationToken);
        var warehouse = await warehouses.FindAsync(s.WarehouseId, cancellationToken);
        return new ReplenishmentSuggestionSummary(s.Id, s.CompanyId, s.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), item?.BaseUomCode ?? string.Empty, s.WarehouseId, warehouse?.Code ?? string.Empty,
            s.RunId, ItemUomMath.Normalize(s.SuggestedQty), s.SuggestedSupplierId, s.NeededBy, s.Explanation, s.Status, s.AcceptedQty is { } a ? ItemUomMath.Normalize(a) : null, s.AcceptedSupplierId, s.PurchaseOrderLineId, s.DecisionNote, s.DecidedBy, s.DecidedAt, s.UpdatedAt);
    }
}

public sealed record ReplenishmentPayload(DateOnly? AsOf = null);

/// <summary>The daily planner across tenants (platform schedule) or for one tenant.</summary>
public sealed class ReplenishmentJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, ReplenishmentService planner) : IJobHandler<ReplenishmentPayload>
{
    public static string JobType => "inventory.replenishment.plan";

    public async Task<object?> ExecuteAsync(ReplenishmentPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is not null)
        {
            var (companies, suggestions) = await planner.PlanAllAsync(payload?.AsOf, cancellationToken);
            return new { companies, suggestions };
        }

        var totalCompanies = 0;
        var totalSuggestions = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            var (companies, suggestions) = await InTenantAsync(tenant.Id.Value, (sp, ct) => sp.GetRequiredService<ReplenishmentService>().PlanAllAsync(payload?.AsOf, ct), cancellationToken);
            totalCompanies += companies;
            totalSuggestions += suggestions;
        }

        return new { tenants = tenants.Count, companies = totalCompanies, suggestions = totalSuggestions };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

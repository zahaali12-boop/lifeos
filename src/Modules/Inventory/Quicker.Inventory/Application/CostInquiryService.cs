using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Results;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Inventory.Application;

/// <summary>The valuation report at a date, the "why did this cost change" view of one entry, the adjustment runs and the standard cost versions.</summary>
public sealed class CostInquiryService(InventoryDbContext db, IUnitOfWorkAccessor unitOfWork)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class ValuationRecord
    {
        public Guid ItemId { get; set; }

        public string ItemCode { get; set; } = string.Empty;

        public string ItemName { get; set; } = string.Empty;

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public string BaseUom { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public decimal Actual { get; set; }

        public decimal Expected { get; set; }
    }

    private sealed class ApplicationRecord
    {
        public Guid Id { get; set; }

        public Guid OutboundSleId { get; set; }

        public Guid InboundSleId { get; set; }

        public decimal Quantity { get; set; }

        public decimal CostAmount { get; set; }

        public bool IsReapplication { get; set; }

        public Guid? RunId { get; set; }

        public Guid? SupersededBy { get; set; }

        public DateTimeOffset AppliedAt { get; set; }

        public DateOnly CounterpartDate { get; set; }

        public string CounterpartType { get; set; } = string.Empty;

        public decimal CounterpartQuantity { get; set; }
    }

    /// <summary>Inventory value at a date: Σ value entries on the inventory accounts with a GL date on or before it, per item and warehouse; the quantity from the ledger at the same date.</summary>
    public async Task<ValuationReport> ValuationAsync(Guid companyId, DateOnly asOf, Guid? warehouseId, Guid? itemId, bool includeZero, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<ValuationRecord>(new CommandDefinition("""
            WITH valued AS (
              SELECT tenant_id, item_id, warehouse_id, sum(cost_amount_actual) AS actual, sum(cost_amount_expected) AS expected
              FROM app.inv_stock_value_entries
              WHERE company_id = @company AND posting_date <= @asOf AND account_role IN ('Inventory', 'InventoryInTransit')
                AND (@warehouse::uuid IS NULL OR warehouse_id = @warehouse) AND (@item::uuid IS NULL OR item_id = @item)
              GROUP BY 1, 2, 3
            ), quantities AS (
              SELECT tenant_id, item_id, warehouse_id, sum(quantity) AS quantity
              FROM app.inv_stock_ledger_entries
              WHERE company_id = @company AND posting_date <= @asOf AND ownership = 'own'
                AND (@warehouse::uuid IS NULL OR warehouse_id = @warehouse) AND (@item::uuid IS NULL OR item_id = @item)
              GROUP BY 1, 2, 3
            ), keys AS (
              SELECT tenant_id, item_id, warehouse_id FROM valued UNION SELECT tenant_id, item_id, warehouse_id FROM quantities
            )
            SELECT k.item_id, i.code AS item_code, i.name_i18n::text AS item_name, k.warehouse_id, w.code AS warehouse_code, u.code AS base_uom,
                   coalesce(q.quantity, 0) AS quantity, coalesce(v.actual, 0) AS actual, coalesce(v.expected, 0) AS expected
            FROM keys k
            JOIN app.itm_items i ON i.tenant_id = k.tenant_id AND i.id = k.item_id
            JOIN app.org_uoms u ON u.tenant_id = i.tenant_id AND u.id = i.base_uom_id
            JOIN app.inv_warehouses w ON w.tenant_id = i.tenant_id AND w.id = k.warehouse_id
            LEFT JOIN valued v ON v.item_id = k.item_id AND v.warehouse_id = k.warehouse_id
            LEFT JOIN quantities q ON q.item_id = k.item_id AND q.warehouse_id = k.warehouse_id
            WHERE @includeZero OR coalesce(q.quantity, 0) <> 0 OR coalesce(v.actual, 0) + coalesce(v.expected, 0) <> 0
            ORDER BY i.code, w.code
            """, new { company = companyId, asOf, warehouse = warehouseId, item = itemId, includeZero }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var lines = rows.Select(static r => new ValuationRow(r.ItemId, r.ItemCode, Parse(r.ItemName), r.WarehouseId, r.WarehouseCode, r.BaseUom, N(r.Quantity), r.Actual, r.Expected, r.Actual + r.Expected,
            r.Quantity == 0m ? 0m : (r.Actual + r.Expected) / r.Quantity)).ToList();
        return new ValuationReport(companyId, asOf, lines, lines.Sum(static l => l.Actual), lines.Sum(static l => l.Expected), lines.Sum(static l => l.Value));
    }

    /// <summary>Why did this cost change: the entry, every value entry in order with its reason, the layers it consumed or the entries that consumed it, and the runs involved.</summary>
    public async Task<Result<CostExplanation>> ExplainAsync(Guid sleId, CancellationToken cancellationToken)
    {
        var entry = await db.Entries.SingleOrDefaultAsync(e => e.Id == sleId, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound("stock_entry", sleId);
        }

        var values = await db.ValueEntries.Where(v => v.SleId == sleId).OrderBy(static v => v.CreatedAt).ThenBy(static v => v.Id).ToListAsync(cancellationToken);
        var uow = unitOfWork.Current;
        var applications = (await uow.Connection.QueryAsync<ApplicationRecord>(new CommandDefinition("""
            SELECT a.id, a.outbound_sle_id, a.inbound_sle_id, a.quantity, a.cost_amount, a.is_reapplication, a.run_id, a.superseded_by, a.applied_at,
                   c.posting_date AS counterpart_date, c.entry_type AS counterpart_type, c.quantity AS counterpart_quantity
            FROM app.inv_item_applications a
            JOIN app.inv_stock_ledger_entries c ON c.tenant_id = a.tenant_id AND c.id = CASE WHEN a.outbound_sle_id = @id THEN a.inbound_sle_id ELSE a.outbound_sle_id END
            WHERE a.outbound_sle_id = @id OR a.inbound_sle_id = @id
            ORDER BY a.applied_at, c.posting_date, c.sequence, a.id
            """, new { id = sleId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var runIds = values.Where(static v => v.AdjustmentRunId is not null).Select(static v => v.AdjustmentRunId!.Value)
            .Concat(applications.Where(static a => a.RunId is not null).Select(static a => a.RunId!.Value))
            .Concat(applications.Where(static a => a.SupersededBy is not null).Select(static a => a.SupersededBy!.Value)).Distinct().ToList();
        var runs = runIds.Count == 0 ? new List<CostAdjustmentRun>() : await db.CostRuns.Where(r => runIds.Contains(r.Id)).OrderBy(static r => r.StartedAt).ToListAsync(cancellationToken);
        var inventoryAmount = values.Where(static v => CostingService.IsInventoryRole(v.AccountRole)).Sum(static v => v.Amount);
        var quantity = Math.Abs(entry.Quantity);
        return new CostExplanation(
            new StockEntryCostSummary(entry.Id, entry.Sequence, entry.CompanyId, entry.ItemId, entry.WarehouseId, entry.EntryType, N(entry.Quantity), entry.PostingDate, entry.SourceDocumentType, entry.SourceDocumentId, entry.TransferPairId, entry.EnteredUnitCost, entry.CostIsExpected, entry.AppliesToSleId,
                inventoryAmount, quantity == 0m ? 0m : Math.Abs(inventoryAmount) / quantity, values.Any(static v => v.CostedAtExpected)),
            values.Select(CostingService.Map).ToList(),
            applications.Select(a => new CostApplicationInfo(a.Id, a.OutboundSleId == sleId ? "consumed" : "consumed_by", a.OutboundSleId == sleId ? a.InboundSleId : a.OutboundSleId, a.CounterpartType, a.CounterpartDate, N(a.CounterpartQuantity), N(a.Quantity), a.CostAmount, a.IsReapplication, a.RunId, a.SupersededBy, a.AppliedAt)).ToList(),
            runs.Select(CostingService.Map).ToList());
    }

    public async Task<Result<Page<CostAdjustmentRunInfo>>> RunsAsync(Guid? companyId, Guid? itemId, string? status, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.CostRuns.AsQueryable();
        if (companyId is { } company)
        {
            query = query.Where(r => r.CompanyId == company);
        }

        if (itemId is { } item)
        {
            query = query.Where(r => r.ItemId == item);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        var result = await KeysetPaging.ByIdDescendingAsync(query, static r => r.Id, page, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        // The item code for display (the list is read by people, not only by the engine).
        var itemIds = result.Value.Items.Select(static r => r.ItemId).Distinct().ToArray();
        var uow = unitOfWork.Current;
        var codes = (await uow.Connection.QueryAsync<(Guid Id, string Code)>(new CommandDefinition(
            "SELECT id, code FROM app.itm_items WHERE id = ANY(@itemIds)", new { itemIds }, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static c => c.Id, static c => c.Code);
        return new Page<CostAdjustmentRunInfo>(result.Value.Items.Select(r => CostingService.Map(r) with { ItemCode = codes.GetValueOrDefault(r.ItemId) }).ToList(), result.Value.NextCursor);
    }

    public async Task<Result<CostAdjustmentRunInfo>> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.CostRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        return run is null ? Error.NotFound("cost_adjustment_run", runId) : CostingService.Map(run);
    }

    public async Task<IReadOnlyList<StandardCostVersionInfo>> StandardCostsAsync(Guid companyId, Guid itemId, CancellationToken cancellationToken)
    {
        var versions = await db.StandardCosts.Where(v => v.CompanyId == companyId && v.ItemId == itemId).OrderByDescending(static v => v.EffectiveFrom).ToListAsync(cancellationToken);
        var ids = versions.Select(static v => v.Id).ToList();
        var runs = await db.CostRuns.Where(r => r.TriggerDocumentType == "standard_cost" && ids.Contains(r.TriggerDocumentId)).Select(static r => new { r.TriggerDocumentId, r.Id }).ToListAsync(cancellationToken);
        return versions.Select(v => Map(v) with { RevaluationRunId = v.RevaluationRunId ?? runs.Where(r => r.TriggerDocumentId == v.Id).Select(static r => (Guid?)r.Id).FirstOrDefault() }).ToList();
    }

    public static StandardCostVersionInfo Map(StandardCostVersion v) => new(v.Id, v.CompanyId, v.ItemId, v.StandardCost, v.EffectiveFrom, v.Reason, v.RevaluationRunId, v.ApprovedBy, v.CreatedAt);

    private static IReadOnlyDictionary<string, string> Parse(string json) =>
        string.IsNullOrEmpty(json) ? new Dictionary<string, string>(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static decimal N(decimal value) => ItemUomMath.Normalize(value);
}

public sealed record ValuationRow(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid WarehouseId, string WarehouseCode, string BaseUom, decimal Quantity, decimal Actual, decimal Expected, decimal Value, decimal AverageUnitCost);

public sealed record ValuationReport(Guid CompanyId, DateOnly AsOf, IReadOnlyList<ValuationRow> Lines, decimal TotalActual, decimal TotalExpected, decimal TotalValue);

public sealed record StockEntryCostSummary(Guid Id, long Sequence, Guid CompanyId, Guid ItemId, Guid WarehouseId, string EntryType, decimal Quantity, DateOnly PostingDate, string SourceDocumentType, Guid SourceDocumentId, Guid? TransferPairId, decimal? EnteredUnitCost, bool CostIsExpected, Guid? AppliesToSleId, decimal CostAmount, decimal UnitCost, bool CostedAtExpected);

public sealed record CostApplicationInfo(Guid Id, string Direction, Guid CounterpartSleId, string CounterpartEntryType, DateOnly CounterpartDate, decimal CounterpartQuantity, decimal Quantity, decimal CostAmount, bool IsReapplication, Guid? RunId, Guid? SupersededBy, DateTimeOffset AppliedAt);

public sealed record CostExplanation(StockEntryCostSummary Entry, IReadOnlyList<StockValueEntryInfo> ValueEntries, IReadOnlyList<CostApplicationInfo> Applications, IReadOnlyList<CostAdjustmentRunInfo> Runs);

public sealed record StandardCostVersionInfo(Guid Id, Guid CompanyId, Guid ItemId, decimal StandardCost, DateOnly EffectiveFrom, string? Reason, Guid? RevaluationRunId, Guid? ApprovedBy, DateTimeOffset CreatedAt);

public sealed record SetStandardCostRequest(Guid CompanyId, Guid ItemId, decimal StandardCost, DateOnly EffectiveFrom, string? Reason = null);

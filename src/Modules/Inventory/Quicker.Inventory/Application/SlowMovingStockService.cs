using System.Text.Json;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Results;
using Quicker.Persistence;

namespace Quicker.Inventory.Application;

/// <summary>
/// One item's stock in one warehouse that has not been used for a while: what is on hand at the date, when it first and
/// last came in, when it was last sold or consumed, how many days it has been idle (since the last use, or since it
/// first came in when it never was) and, for those who may see costs, its value.
/// </summary>
public sealed record SlowMovingRow(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid WarehouseId, string WarehouseCode, string BaseUom, decimal OnHand, DateOnly? FirstReceived, DateOnly? LastReceived, DateOnly? LastUsed, int IdleDays, decimal? Value);

/// <summary>Stock idle for at least <see cref="MinIdleDays"/> days at <see cref="AsOf"/>, the longest idle first; the value total only when costs may be seen.</summary>
public sealed record SlowMovingReport(Guid CompanyId, DateOnly AsOf, int MinIdleDays, IReadOnlyList<SlowMovingRow> Rows, decimal? TotalValue);

/// <summary>
/// Slow-moving stock, read from the stock ledger and the value entries as the valuation reads them: own stock only (not
/// consignment); "used" means sold or consumed by an assembly, so transfers, adjustments, counts and returns to the
/// supplier move stock without making it used.
/// </summary>
public sealed class SlowMovingStockService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] UsedBy = [StockEntryTypes.SaleShipment, StockEntryTypes.AssemblyConsumption];

    private sealed class Record
    {
        public Guid ItemId { get; set; }

        public string ItemCode { get; set; } = string.Empty;

        public string ItemName { get; set; } = "{}";

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public string BaseUom { get; set; } = string.Empty;

        public decimal OnHand { get; set; }

        public DateOnly? FirstReceived { get; set; }

        public DateOnly? LastReceived { get; set; }

        public DateOnly? LastUsed { get; set; }

        public decimal Value { get; set; }
    }

    public async Task<Result<SlowMovingReport>> ReportAsync(Guid companyId, DateOnly asOf, int minIdleDays, Guid? warehouseId, CancellationToken cancellationToken)
    {
        if (minIdleDays is < 0 or > 3650)
        {
            return Error.Validation("slow_moving.idle_days_invalid", "Idle days are between 0 and 3650.").WithWhy(("idleDays", minIdleDays));
        }

        var scopes = principal.Principal?.ScopesFor(InventoryPermissions.StockRead);
        if (principal.Principal is not null && scopes?.AllowsCompany(companyId) != true)
        {
            return Error.Forbidden("stock.company_scope", "Your role does not cover this company.").WithWhy(("companyId", companyId), ("permission", InventoryPermissions.StockRead));
        }

        var uow = unitOfWork.Current;
        var records = (await uow.Connection.QueryAsync<Record>(new CommandDefinition("""
            WITH moves AS (
              SELECT tenant_id, item_id, warehouse_id, sum(quantity) AS on_hand,
                     min(posting_date) FILTER (WHERE quantity > 0) AS first_received,
                     max(posting_date) FILTER (WHERE quantity > 0) AS last_received,
                     max(posting_date) FILTER (WHERE quantity < 0 AND entry_type = ANY(@usedBy)) AS last_used
              FROM app.inv_stock_ledger_entries
              WHERE company_id = @company AND posting_date <= @asOf AND ownership = 'own' AND (@warehouse::uuid IS NULL OR warehouse_id = @warehouse)
              GROUP BY 1, 2, 3
            ), valued AS (
              SELECT item_id, warehouse_id, sum(cost_amount_actual + cost_amount_expected) AS value
              FROM app.inv_stock_value_entries
              WHERE company_id = @company AND posting_date <= @asOf AND account_role = 'Inventory' AND (@warehouse::uuid IS NULL OR warehouse_id = @warehouse)
              GROUP BY 1, 2
            )
            SELECT m.item_id, i.code AS item_code, i.name_i18n::text AS item_name, m.warehouse_id, w.code AS warehouse_code, u.code AS base_uom,
                   m.on_hand, m.first_received, m.last_received, m.last_used, coalesce(v.value, 0) AS value
            FROM moves m
            JOIN app.itm_items i ON i.tenant_id = m.tenant_id AND i.id = m.item_id
            JOIN app.org_uoms u ON u.tenant_id = i.tenant_id AND u.id = i.base_uom_id
            JOIN app.inv_warehouses w ON w.tenant_id = m.tenant_id AND w.id = m.warehouse_id
            LEFT JOIN valued v ON v.item_id = m.item_id AND v.warehouse_id = m.warehouse_id
            WHERE m.on_hand > 0
            """, new { company = companyId, asOf, warehouse = warehouseId, usedBy = UsedBy }, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var costs = principal.Principal is not { } p || p.Has(InventoryPermissions.CostingRead);
        var rows = records
            .Where(r => scopes is null || scopes.AllowsWarehouse(r.WarehouseId))
            .Select(r => (Record: r, Idle: (r.LastUsed ?? r.FirstReceived) is { } since ? asOf.DayNumber - since.DayNumber : 0))
            .Where(x => x.Idle >= minIdleDays)
            .OrderByDescending(static x => x.Idle).ThenBy(static x => x.Record.ItemCode, StringComparer.Ordinal).ThenBy(static x => x.Record.WarehouseCode, StringComparer.Ordinal)
            .Select(x => new SlowMovingRow(x.Record.ItemId, x.Record.ItemCode, Names(x.Record.ItemName), x.Record.WarehouseId, x.Record.WarehouseCode, x.Record.BaseUom, ItemUomMath.Normalize(x.Record.OnHand),
                x.Record.FirstReceived, x.Record.LastReceived, x.Record.LastUsed, x.Idle, costs ? x.Record.Value : null))
            .ToList();
        return new SlowMovingReport(companyId, asOf, minIdleDays, rows, costs ? rows.Sum(static r => r.Value ?? 0m) : null);
    }

    private static Dictionary<string, string> Names(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

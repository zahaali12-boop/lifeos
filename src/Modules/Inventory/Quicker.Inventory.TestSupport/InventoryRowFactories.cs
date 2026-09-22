using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Inventory.TestSupport;

/// <summary>Minimal valid rows for every Inventory tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class InventoryRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.inv_warehouses", static async (c, tx, t) => new RowRef("app.inv_warehouses", $"id = '{(await WarehouseAsync(c, tx, t)).Warehouse}'"));
        IsolationRegistry.Register("app.inv_bins", static async (c, tx, t) =>
        {
            var (warehouse, _) = await WarehouseAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("UPDATE app.inv_warehouses SET bins_enabled = true WHERE tenant_id = @t AND id = @warehouse", new { t, warehouse }, tx);
            await c.ExecuteAsync("INSERT INTO app.inv_bins (tenant_id, id, warehouse_id, code) VALUES (@t, @id, @warehouse, @code)", new { t, id, warehouse, code = Suffix(id) }, tx);
            return new RowRef("app.inv_bins", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.inv_stock_postings", static async (c, tx, t) => new RowRef("app.inv_stock_postings", $"id = '{(await PostingAsync(c, tx, t)).Posting}'"));
        IsolationRegistry.Register("app.inv_stock_ledger_entries", static async (c, tx, t) => new RowRef("app.inv_stock_ledger_entries", $"id = '{(await PostingAsync(c, tx, t)).Entry}'"));
        IsolationRegistry.Register("app.inv_stock_balances", static async (c, tx, t) =>
        {
            var (warehouse, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.inv_stock_balances (tenant_id, company_id, item_id, warehouse_id, on_hand) VALUES (@t, @company, @item, @warehouse, 5)", new { t, company, item, warehouse }, tx);
            return new RowRef("app.inv_stock_balances", $"item_id = '{item}'");
        });
        IsolationRegistry.Register("app.inv_reservations", static async (c, tx, t) =>
        {
            var (warehouse, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.inv_reservations (tenant_id, id, company_id, item_id, warehouse_id, quantity, source_document_type, source_document_id) VALUES (@t, @id, @company, @item, @warehouse, 1, 'probe', @doc)", new { t, id, company, item, warehouse, doc = Guid.CreateVersion7() }, tx);
            return new RowRef("app.inv_reservations", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.inv_item_cost_scopes", static async (c, tx, t) =>
        {
            var (_, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.inv_item_cost_scopes (tenant_id, company_id, item_id) VALUES (@t, @company, @item)", new { t, company, item }, tx);
            return new RowRef("app.inv_item_cost_scopes", $"item_id = '{item}'");
        });
        IsolationRegistry.Register("app.inv_stock_value_entries", static async (c, tx, t) => new RowRef("app.inv_stock_value_entries", $"id = '{(await ValueEntryAsync(c, tx, t)).Value}'"));
        IsolationRegistry.Register("app.inv_item_applications", static async (c, tx, t) =>
        {
            var (_, entry, company, item) = await ValueEntryAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.inv_item_applications (tenant_id, id, company_id, item_id, outbound_sle_id, inbound_sle_id, quantity, cost_amount) VALUES (@t, @id, @company, @item, @entry, @entry, 1, 1)", new { t, id, company, item, entry }, tx);
            return new RowRef("app.inv_item_applications", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.inv_item_costs", static async (c, tx, t) =>
        {
            var (_, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.inv_item_costs (tenant_id, company_id, item_id, valuation_date, quantity, value, average_unit_cost) VALUES (@t, @company, @item, '2026-09-22', 5, 10, 2)", new { t, company, item }, tx);
            return new RowRef("app.inv_item_costs", $"item_id = '{item}'");
        });
        IsolationRegistry.Register("app.inv_cost_adjustment_runs", static async (c, tx, t) =>
        {
            var (_, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.inv_cost_adjustment_runs (tenant_id, id, company_id, item_id, trigger_kind, trigger_document_type, trigger_document_id, from_date) VALUES (@t, @id, @company, @item, 'probe', 'probe', @doc, '2026-09-22')", new { t, id, company, item, doc = Guid.CreateVersion7() }, tx);
            return new RowRef("app.inv_cost_adjustment_runs", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.inv_standard_cost_versions", static async (c, tx, t) =>
        {
            var (_, company) = await WarehouseAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.inv_standard_cost_versions (tenant_id, id, company_id, item_id, standard_cost, effective_from) VALUES (@t, @id, @company, @item, 2.5, '2026-09-22')", new { t, id, company, item }, tx);
            return new RowRef("app.inv_standard_cost_versions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.inv_transfers", static async (c, tx, t) => new RowRef("app.inv_transfers", $"id = '{(await TransferAsync(c, tx, t)).Transfer}'"));
        IsolationRegistry.Register("app.inv_transfer_lines", static async (c, tx, t) => new RowRef("app.inv_transfer_lines", $"id = '{(await TransferAsync(c, tx, t)).Line}'"));
    }

    private static async Task<Guid> CompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var fiscal = Guid.CreateVersion7();
        var business = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @fiscal, @code, '{}', 1, 12)", new { t, fiscal, code = Suffix(fiscal).ToLowerInvariant() }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @business, @code, '{}', ARRAY[0,1,2,3,4])", new { t, business, code = Suffix(business).ToLowerInvariant() }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }

    private static async Task<(Guid Warehouse, Guid Company)> WarehouseAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_warehouses (tenant_id, id, company_id, code, name_i18n) VALUES (@t, @id, @company, @code, '{}')", new { t, id, company, code = Suffix(id) }, tx);
        return (id, company);
    }

    private static async Task<(Guid Item, Guid Uom)> ItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var uom = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_uoms (tenant_id, id, code, name_i18n, family) VALUES (@t, @uom, @code, '{}', 'count')", new { t, uom, code = Suffix(uom) }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_items (tenant_id, id, code, name_i18n, type, base_uom_id) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', 'stock', @uom)", new { t, id, code = Suffix(id), uom }, tx);
        await c.ExecuteAsync("INSERT INTO app.itm_item_uoms (tenant_id, id, item_id, uom_id, numerator, denominator) VALUES (@t, @iu, @id, @uom, 1, 1)", new { t, iu = Guid.CreateVersion7(), id, uom }, tx);
        return (id, uom);
    }

    private static async Task<(Guid Posting, Guid Entry)> PostingAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (warehouse, company) = await WarehouseAsync(c, tx, t);
        var (item, uom) = await ItemAsync(c, tx, t);
        var posting = Guid.CreateVersion7();
        var entry = Guid.CreateVersion7();
        var doc = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_stock_postings (tenant_id, id, company_id, posting_date, source_document_type, source_document_id, entry_count) VALUES (@t, @posting, @company, '2026-09-22', 'probe', @doc, 1)", new { t, posting, company, doc }, tx);
        await c.ExecuteAsync("""
            INSERT INTO app.inv_stock_ledger_entries (tenant_id, id, posting_id, company_id, item_id, warehouse_id, entry_type, quantity, entered_uom_id, entered_quantity, posting_date, source_document_type, source_document_id)
            VALUES (@t, @entry, @posting, @company, @item, @warehouse, 'opening', 5, @uom, 5, '2026-09-22', 'probe', @doc)
            """, new { t, entry, posting, company, item, warehouse, uom, doc }, tx);
        return (posting, entry);
    }

    private static async Task<(Guid Value, Guid Entry, Guid Company, Guid Item)> ValueEntryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (warehouse, company) = await WarehouseAsync(c, tx, t);
        var (item, uom) = await ItemAsync(c, tx, t);
        var posting = Guid.CreateVersion7();
        var entry = Guid.CreateVersion7();
        var doc = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_stock_postings (tenant_id, id, company_id, posting_date, source_document_type, source_document_id, entry_count) VALUES (@t, @posting, @company, '2026-09-22', 'probe', @doc, 1)", new { t, posting, company, doc }, tx);
        await c.ExecuteAsync("""
            INSERT INTO app.inv_stock_ledger_entries (tenant_id, id, posting_id, company_id, item_id, warehouse_id, entry_type, quantity, entered_uom_id, entered_quantity, posting_date, source_document_type, source_document_id)
            VALUES (@t, @entry, @posting, @company, @item, @warehouse, 'opening', 5, @uom, 5, '2026-09-22', 'probe', @doc)
            """, new { t, entry, posting, company, item, warehouse, uom, doc }, tx);
        var value = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.inv_stock_value_entries (tenant_id, id, sle_id, company_id, item_id, warehouse_id, posting_date, valuation_date, value_type, valued_quantity, unit_cost, cost_amount_actual, currency, account_role, offset_role, source_document_type, source_document_id)
            VALUES (@t, @value, @entry, @company, @item, @warehouse, '2026-09-22', '2026-09-22', 'direct_cost', 5, 2, 10, 'IQD', 'Inventory', 'OpeningBalanceEquity', 'probe', @doc)
            """, new { t, value, entry, company, item, warehouse, doc }, tx);
        return (value, entry, company, item);
    }

    private static async Task<(Guid Transfer, Guid Line)> TransferAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (from, company) = await WarehouseAsync(c, tx, t);
        var to = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_warehouses (tenant_id, id, company_id, code, name_i18n) VALUES (@t, @to, @company, @code, '{}')", new { t, to, company, code = Suffix(to) }, tx);
        var (item, uom) = await ItemAsync(c, tx, t);
        var transfer = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_transfers (tenant_id, id, company_id, from_warehouse_id, to_warehouse_id) VALUES (@t, @transfer, @company, @from, @to)", new { t, transfer, company, from, to }, tx);
        await c.ExecuteAsync("INSERT INTO app.inv_transfer_lines (tenant_id, id, transfer_id, line_no, item_id, qty_requested, uom_id) VALUES (@t, @line, @transfer, 1, @item, 1, @uom)", new { t, line, transfer, item, uom }, tx);
        return (transfer, line);
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();
}

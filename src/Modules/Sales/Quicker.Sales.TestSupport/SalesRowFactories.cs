using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Sales.TestSupport;

/// <summary>Minimal valid rows for every Sales tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class SalesRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.sls_quotations", static async (c, tx, t) => new RowRef("app.sls_quotations", $"id = '{(await QuotationAsync(c, tx, t)).Quotation}'"));
        IsolationRegistry.Register("app.sls_quotation_lines", static async (c, tx, t) => new RowRef("app.sls_quotation_lines", $"id = '{(await QuotationAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.sls_orders", static async (c, tx, t) => new RowRef("app.sls_orders", $"id = '{(await OrderAsync(c, tx, t)).Order}'"));
        IsolationRegistry.Register("app.sls_order_lines", static async (c, tx, t) => new RowRef("app.sls_order_lines", $"id = '{(await OrderAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.sls_shipments", static async (c, tx, t) => new RowRef("app.sls_shipments", $"id = '{(await ShipmentAsync(c, tx, t)).Shipment}'"));
        IsolationRegistry.Register("app.sls_shipment_lines", static async (c, tx, t) => new RowRef("app.sls_shipment_lines", $"id = '{(await ShipmentAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.sls_shipment_packages", static async (c, tx, t) => new RowRef("app.sls_shipment_packages", $"id = '{(await PackageAsync(c, tx, t)).Package}'"));
        IsolationRegistry.Register("app.sls_shipment_package_lines", static async (c, tx, t) => new RowRef("app.sls_shipment_package_lines", $"id = '{(await PackageAsync(c, tx, t)).Line}'"));
    }

    private static async Task<(Guid Package, Guid Line)> PackageAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (shipment, _, orderLine) = await ShipmentAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.sls_shipment_packages (tenant_id, id, shipment_id, package_no, package_number) VALUES (@t, @id, @shipment, 1, @number)", new { t, id, shipment, number = Suffix(id) }, tx);
        await c.ExecuteAsync("INSERT INTO app.sls_shipment_package_lines (tenant_id, id, package_id, order_line_id, quantity) VALUES (@t, @line, @id, @orderLine, 1)", new { t, line, id, orderLine }, tx);
        return (id, line);
    }

    private static async Task<(Guid Shipment, Guid Line, Guid OrderLine)> ShipmentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (order, orderLine, company) = await OrderAsync(c, tx, t);
        var order2 = await c.QuerySingleAsync<(Guid PartnerId, Guid WarehouseId)>("SELECT partner_id AS \"PartnerId\", warehouse_id AS \"WarehouseId\" FROM app.sls_orders WHERE tenant_id = @t AND id = @order", new { t, order }, tx);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.sls_shipments (tenant_id, id, company_id, number, order_id, partner_id, warehouse_id, posting_date) VALUES (@t, @id, @company, @number, @order, @partner, @warehouse, '2026-09-22')", new { t, id, company, number = Suffix(id), order, partner = order2.PartnerId, warehouse = order2.WarehouseId }, tx);
        await c.ExecuteAsync("INSERT INTO app.sls_shipment_lines (tenant_id, id, shipment_id, line_no, order_line_id, item_id, quantity, uom_id, quantity_base) VALUES (@t, @line, @id, 1, @orderLine, @item, 1, @uom, 1)", new { t, line, id, orderLine, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line, orderLine);
    }

    private static async Task<(Guid Order, Guid Line, Guid Company)> OrderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var warehouse = await WarehouseAsync(c, tx, t, company);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.sls_orders (tenant_id, id, company_id, number, partner_id, currency, order_date, pricing_date, warehouse_id) VALUES (@t, @id, @company, @number, @partner, 'IQD', '2026-09-22', '2026-09-22', @warehouse)", new { t, id, company, number = Suffix(id), partner, warehouse }, tx);
        await c.ExecuteAsync("INSERT INTO app.sls_order_lines (tenant_id, id, order_id, line_no, item_id, quantity, uom_id, quantity_base, unit_price) VALUES (@t, @line, @id, 1, @item, 1, @uom, 1, 10)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line, company);
    }

    private static async Task<Guid> WarehouseAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, Guid company)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_warehouses (tenant_id, id, company_id, code, name_i18n) VALUES (@t, @id, @company, @code, '{}')", new { t, id, company, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Quotation, Guid Line, Guid Company)> QuotationAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.sls_quotations (tenant_id, id, company_id, number, partner_id, currency, quote_date, pricing_date) VALUES (@t, @id, @company, @number, @partner, 'IQD', '2026-09-22', '2026-09-22')", new { t, id, company, number = Suffix(id), partner }, tx);
        await c.ExecuteAsync("INSERT INTO app.sls_quotation_lines (tenant_id, id, quotation_id, line_no, item_id, quantity, uom_id, quantity_base, unit_price) VALUES (@t, @line, @id, 1, @item, 1, @uom, 1, 10)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line, company);
    }

    private static async Task<Guid> PartnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_customer) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', true)", new { t, id, code = Suffix(id) }, tx);
        return id;
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

    private static string Suffix(Guid id) => "Q" + id.ToString("N")[^10..].ToUpperInvariant();
}

using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Pos.TestSupport;

/// <summary>Minimal valid rows for every Point of sale tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PosRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.pos_registers", static async (c, tx, t) => new RowRef("app.pos_registers", $"id = '{(await RegisterAsync(c, tx, t)).Register}'"));
        IsolationRegistry.Register("app.pos_sessions", static async (c, tx, t) => new RowRef("app.pos_sessions", $"id = '{(await SessionAsync(c, tx, t)).Session}'"));
        IsolationRegistry.Register("app.pos_orders", static async (c, tx, t) => new RowRef("app.pos_orders", $"id = '{await OrderAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.pos_order_lines", static async (c, tx, t) =>
        {
            var order = await OrderAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pos_order_lines (tenant_id, id, order_id, line_no, item_id, item_code, uom_id, uom_code, quantity, base_quantity, unit_price, net_amount, gross_amount) VALUES (@t, @id, @order, 1, @item, 'PROBE', @uom, 'EA', 1, 1, 10, 10, 10)", new { t, id, order, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
            return new RowRef("app.pos_order_lines", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.pos_payments", static async (c, tx, t) =>
        {
            var order = await OrderAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pos_payments (tenant_id, id, order_id, line_no, method, amount) VALUES (@t, @id, @order, 1, 'cash', 10)", new { t, id, order }, tx);
            return new RowRef("app.pos_payments", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<Guid> OrderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (session, register, company) = await SessionAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.pos_orders (tenant_id, id, company_id, register_id, session_id, number, device_order_id, kind, cashier_user_id, order_date, ordered_at, posted_at, currency)
            VALUES (@t, @id, @company, @register, @session, @number, @device, 'sale', @cashier, '2026-01-15', now(), now(), 'IQD')
            """, new { t, id, company, register, session, number = Suffix(id), device = id.ToString("N"), cashier = Guid.CreateVersion7() }, tx);
        return id;
    }

    private static async Task<(Guid Session, Guid Register, Guid Company)> SessionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (register, company) = await RegisterAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pos_sessions (tenant_id, id, company_id, register_id, number, cashier_user_id, cashier_name, opened_at) VALUES (@t, @id, @company, @register, @number, @cashier, 'Probe', now())", new { t, id, company, register, number = Suffix(id), cashier = Guid.CreateVersion7() }, tx);
        return (id, register, company);
    }

    private static async Task<(Guid Register, Guid Company)> RegisterAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var warehouse = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.inv_warehouses (tenant_id, id, company_id, code, name_i18n) VALUES (@t, @id, @company, @code, '{}')", new { t, id = warehouse, company, code = Suffix(warehouse) }, tx);
        var chart = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_charts (tenant_id, id, code, name_i18n) VALUES (@t, @chart, @code, '{\"en\":\"Probe\"}')", new { t, chart, code = Suffix(chart) }, tx);
        var gl = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_accounts (tenant_id, id, chart_id, code, name_i18n, type) VALUES (@t, @gl, @chart, @code, '{\"en\":\"Probe\"}', 'asset')", new { t, gl, chart, code = gl.ToString("N")[^6..] }, tx);
        var cash = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_bank_accounts (tenant_id, id, company_id, code, name_i18n, kind, currency, gl_account_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', 'cash', 'IQD', @gl)", new { t, id = cash, company, code = Suffix(cash), gl }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pos_registers (tenant_id, id, company_id, code, name_i18n, warehouse_id, cash_account_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', @warehouse, @cash)", new { t, id, company, code = Suffix(id), warehouse, cash }, tx);
        return (id, company);
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
}

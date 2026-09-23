using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Purchasing.TestSupport;

/// <summary>Minimal valid rows for every Purchasing tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PurchasingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.pur_requisitions", static async (c, tx, t) => new RowRef("app.pur_requisitions", $"id = '{(await RequisitionAsync(c, tx, t)).Requisition}'"));
        IsolationRegistry.Register("app.pur_requisition_lines", static async (c, tx, t) => new RowRef("app.pur_requisition_lines", $"id = '{(await RequisitionAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pur_rfqs", static async (c, tx, t) => new RowRef("app.pur_rfqs", $"id = '{(await RfqAsync(c, tx, t)).Rfq}'"));
        IsolationRegistry.Register("app.pur_rfq_lines", static async (c, tx, t) => new RowRef("app.pur_rfq_lines", $"id = '{(await RfqAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pur_rfq_suppliers", static async (c, tx, t) => new RowRef("app.pur_rfq_suppliers", $"id = '{(await RfqAsync(c, tx, t)).Supplier}'"));
        IsolationRegistry.Register("app.pur_supplier_quotes", static async (c, tx, t) => new RowRef("app.pur_supplier_quotes", $"id = '{(await QuoteAsync(c, tx, t)).Quote}'"));
        IsolationRegistry.Register("app.pur_supplier_quote_lines", static async (c, tx, t) => new RowRef("app.pur_supplier_quote_lines", $"id = '{(await QuoteAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pur_blanket_agreements", static async (c, tx, t) => new RowRef("app.pur_blanket_agreements", $"id = '{(await AgreementAsync(c, tx, t)).Agreement}'"));
        IsolationRegistry.Register("app.pur_blanket_lines", static async (c, tx, t) => new RowRef("app.pur_blanket_lines", $"id = '{(await AgreementAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pur_orders", static async (c, tx, t) => new RowRef("app.pur_orders", $"id = '{(await OrderAsync(c, tx, t)).Order}'"));
        IsolationRegistry.Register("app.pur_order_lines", static async (c, tx, t) => new RowRef("app.pur_order_lines", $"id = '{(await OrderAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pur_order_revisions", static async (c, tx, t) =>
        {
            var (order, _, _) = await OrderAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pur_order_revisions (tenant_id, id, order_id, revision, snapshot) VALUES (@t, @id, @order, 0, '{}')", new { t, id, order }, tx);
            return new RowRef("app.pur_order_revisions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.pur_commitments", static async (c, tx, t) =>
        {
            var (order, line, company) = await OrderAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pur_commitments (tenant_id, id, company_id, order_id, order_line_id, account_role, period_key, amount_fc, currency, amount_rc) VALUES (@t, @id, @company, @order, @line, 'Expense', '2026-09', 10, 'IQD', 10)", new { t, id, company, order, line }, tx);
            return new RowRef("app.pur_commitments", $"id = '{id}'");
        });
    }

    private static async Task<(Guid Requisition, Guid Line)> RequisitionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pur_requisitions (tenant_id, id, company_id, number, currency) VALUES (@t, @id, @company, @number, 'IQD')", new { t, id, company, number = Suffix(id) }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_requisition_lines (tenant_id, id, requisition_id, line_no, item_id, quantity, uom_id, quantity_base) VALUES (@t, @line, @id, 1, @item, 1, @uom, 1)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line);
    }

    private static async Task<(Guid Rfq, Guid Line, Guid Supplier)> RfqAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        var supplier = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pur_rfqs (tenant_id, id, company_id, number) VALUES (@t, @id, @company, @number)", new { t, id, company, number = Suffix(id) }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_rfq_lines (tenant_id, id, rfq_id, line_no, item_id, quantity, uom_id, quantity_base) VALUES (@t, @line, @id, 1, @item, 1, @uom, 1)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_rfq_suppliers (tenant_id, id, rfq_id, partner_id) VALUES (@t, @supplier, @id, @partner)", new { t, supplier, id, partner }, tx);
        return (id, line, supplier);
    }

    private static async Task<(Guid Quote, Guid Line)> QuoteAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (_, rfqLine, supplier) = await RfqAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pur_supplier_quotes (tenant_id, id, rfq_supplier_id, currency) VALUES (@t, @id, @supplier, 'IQD')", new { t, id, supplier }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_supplier_quote_lines (tenant_id, id, quote_id, rfq_line_id, unit_price, quantity, uom_id) VALUES (@t, @line, @id, @rfqLine, 1, 1, @uom)", new { t, line, id, rfqLine, uom = Guid.CreateVersion7() }, tx);
        return (id, line);
    }

    private static async Task<(Guid Agreement, Guid Line)> AgreementAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pur_blanket_agreements (tenant_id, id, company_id, number, partner_id, valid_from, valid_to, currency) VALUES (@t, @id, @company, @number, @partner, '2026-01-01', '2026-12-31', 'IQD')", new { t, id, company, number = Suffix(id), partner }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_blanket_lines (tenant_id, id, agreement_id, line_no, item_id, uom_id, agreed_qty, agreed_price) VALUES (@t, @line, @id, 1, @item, @uom, 10, 1)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line);
    }

    private static async Task<(Guid Order, Guid Line, Guid Company)> OrderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pur_orders (tenant_id, id, company_id, number, partner_id, currency, order_date) VALUES (@t, @id, @company, @number, @partner, 'IQD', '2026-09-22')", new { t, id, company, number = Suffix(id), partner }, tx);
        await c.ExecuteAsync("INSERT INTO app.pur_order_lines (tenant_id, id, order_id, line_no, item_id, quantity, uom_id, quantity_base, unit_price) VALUES (@t, @line, @id, 1, @item, 1, @uom, 1, 10)", new { t, line, id, item = Guid.CreateVersion7(), uom = Guid.CreateVersion7() }, tx);
        return (id, line, company);
    }

    private static async Task<Guid> PartnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', true)", new { t, id, code = Suffix(id) }, tx);
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

using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Banking.TestSupport;

/// <summary>Minimal valid rows for every Banking tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class BankingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.bnk_bank_accounts", static async (c, tx, t) => new RowRef("app.bnk_bank_accounts", $"id = '{(await BankAccountAsync(c, tx, t)).Account}'"));
        IsolationRegistry.Register("app.bnk_bank_transactions", static async (c, tx, t) =>
        {
            var (account, company) = await BankAccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_bank_transactions (tenant_id, id, bank_account_id, company_id, posting_date, value_date, kind, amount_tc, amount_fc) VALUES (@t, @id, @account, @company, '2026-09-22', '2026-09-22', 'disbursement', -1, -1)", new { t, id, account, company }, tx);
            return new RowRef("app.bnk_bank_transactions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_payments", static async (c, tx, t) => new RowRef("app.bnk_payments", $"id = '{(await PaymentAsync(c, tx, t)).Payment}'"));
        IsolationRegistry.Register("app.bnk_payment_lines", static async (c, tx, t) => new RowRef("app.bnk_payment_lines", $"id = '{(await PaymentAsync(c, tx, t)).Line}'"));
    }

    private static string Suffix(Guid id) => "B" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid Account, Guid Company)> BankAccountAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var chart = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_charts (tenant_id, id, code, name_i18n) VALUES (@t, @chart, @code, '{\"en\":\"Probe\"}')", new { t, chart, code = Suffix(chart) }, tx);
        var gl = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_accounts (tenant_id, id, chart_id, code, name_i18n, type) VALUES (@t, @gl, @chart, @code, '{\"en\":\"Probe\"}', 'asset')", new { t, gl, chart, code = gl.ToString("N")[^6..] }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_bank_accounts (tenant_id, id, company_id, code, name_i18n, kind, currency, gl_account_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', 'bank', 'IQD', @gl)", new { t, id, company, code = Suffix(id), gl }, tx);
        return (id, company);
    }

    private static async Task<(Guid Payment, Guid Line)> PaymentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (account, company) = await BankAccountAsync(c, tx, t);
        var partner = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@t, @partner, @code, '{\"en\":\"Probe\"}', true)", new { t, partner, code = Suffix(partner) }, tx);
        var item = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ap_open_items (tenant_id, id, company_id, partner_id, kind, document_type, document_id, document_number, posting_date, document_date, due_date, currency, original_tc, original_fc, remaining_tc, remaining_fc) VALUES (@t, @item, @company, @partner, 'invoice', 'purchase_invoice', @doc, @number, '2026-09-22', '2026-09-22', '2026-10-22', 'IQD', 10, 10, 10, 10)", new { t, item, company, partner, doc = Guid.CreateVersion7(), number = Suffix(item) }, tx);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_payments (tenant_id, id, company_id, number, kind, partner_id, bank_account_id, payment_date, method, currency, bank_currency) VALUES (@t, @id, @company, @number, 'supplier_payment', @partner, @account, '2026-09-22', 'transfer', 'IQD', 'IQD')", new { t, id, company, number = Suffix(id), partner, account }, tx);
        await c.ExecuteAsync("INSERT INTO app.bnk_payment_lines (tenant_id, id, payment_id, line_no, open_item_id, amount_tc) VALUES (@t, @line, @id, 1, @item, 10)", new { t, line, id, item }, tx);
        return (id, line);
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

using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Expenses.TestSupport;

/// <summary>Minimal valid rows for every Expenses tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class ExpensesRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.exp_categories", static async (c, tx, t) => new RowRef("app.exp_categories", $"id = '{await CategoryAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.exp_funds", static async (c, tx, t) => new RowRef("app.exp_funds", $"id = '{(await FundAsync(c, tx, t)).Fund}'"));
        IsolationRegistry.Register("app.exp_expenses", static async (c, tx, t) => new RowRef("app.exp_expenses", $"id = '{(await ExpenseAsync(c, tx, t)).Expense}'"));
        IsolationRegistry.Register("app.exp_expense_lines", static async (c, tx, t) => new RowRef("app.exp_expense_lines", $"id = '{(await ExpenseAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.exp_replenishments", static async (c, tx, t) =>
        {
            var (fund, company) = await FundAsync(c, tx, t);
            var (source, _) = await BankAccountAsync(c, tx, t, company);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.exp_replenishments (tenant_id, id, company_id, fund_id, number, replenished_on, amount, from_bank_account_id, fund_transaction_id, bank_transaction_id) VALUES (@t, @id, @company, @fund, @number, '2026-10-31', 100, @source, @ft, @bt)", new { t, id, company, fund, number = Suffix(id), source, ft = Guid.CreateVersion7(), bt = Guid.CreateVersion7() }, tx);
            return new RowRef("app.exp_replenishments", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "E" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<Guid> CategoryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.exp_categories (tenant_id, id, code, name_i18n, account_code) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', '6150')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Fund, Guid Company)> FundAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var (box, _) = await BankAccountAsync(c, tx, t, company);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.exp_funds (tenant_id, id, company_id, code, name_i18n, bank_account_id, float_amount, currency) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', @box, 500, 'IQD')", new { t, id, company, code = Suffix(id), box }, tx);
        return (id, company);
    }

    private static async Task<(Guid Expense, Guid Line)> ExpenseAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@t, @partner, @code, '{\"en\":\"Probe\"}', true)", new { t, partner, code = Suffix(partner) }, tx);
        var category = await CategoryAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.exp_expenses (tenant_id, id, company_id, kind, partner_id, expense_date, currency, total_net, total_gross) VALUES (@t, @id, @company, 'supplier', @partner, '2026-10-31', 'IQD', 10, 10)", new { t, id, company, partner }, tx);
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.exp_expense_lines (tenant_id, id, expense_id, line_no, category_id, amount) VALUES (@t, @line, @id, 1, @category, 10)", new { t, line, id, category }, tx);
        return (id, line);
    }

    private static async Task<(Guid Account, Guid Company)> BankAccountAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, Guid company)
    {
        var chart = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_charts (tenant_id, id, code, name_i18n) VALUES (@t, @chart, @code, '{\"en\":\"Probe\"}')", new { t, chart, code = Suffix(chart) }, tx);
        var gl = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_accounts (tenant_id, id, chart_id, code, name_i18n, type) VALUES (@t, @gl, @chart, @code, '{\"en\":\"Probe\"}', 'asset')", new { t, gl, chart, code = gl.ToString("N")[^6..] }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_bank_accounts (tenant_id, id, company_id, code, name_i18n, kind, currency, gl_account_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', 'cash', 'IQD', @gl)", new { t, id, company, code = Suffix(id), gl }, tx);
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

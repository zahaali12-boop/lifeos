using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Payroll.TestSupport;

/// <summary>Minimal valid rows for every Payroll tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PayrollRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.pay_components", static async (c, tx, t) => new RowRef("app.pay_components", $"id = '{(await ComponentAsync(c, tx, t)).Component}'"));
        IsolationRegistry.Register("app.pay_employees", static async (c, tx, t) => new RowRef("app.pay_employees", $"id = '{(await EmployeeAsync(c, tx, t)).Employee}'"));
        IsolationRegistry.Register("app.pay_employee_components", static async (c, tx, t) =>
        {
            var (employee, company, _) = await EmployeeAsync(c, tx, t);
            var component = await ComponentOfAsync(c, tx, t, company);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pay_employee_components (tenant_id, id, employee_id, component_id, amount) VALUES (@t, @id, @employee, @component, 100)", new { t, id, employee, component }, tx);
            return new RowRef("app.pay_employee_components", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.pay_leaves", static async (c, tx, t) =>
        {
            var (employee, company, _) = await EmployeeAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pay_leaves (tenant_id, id, company_id, employee_id, kind, from_date, to_date, days) VALUES (@t, @id, @company, @employee, 'annual', '2026-03-01', '2026-03-02', 2)", new { t, id, company, employee }, tx);
            return new RowRef("app.pay_leaves", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.pay_runs", static async (c, tx, t) => new RowRef("app.pay_runs", $"id = '{(await RunAsync(c, tx, t)).Run}'"));
        IsolationRegistry.Register("app.pay_run_lines", static async (c, tx, t) => new RowRef("app.pay_run_lines", $"id = '{(await RunLineAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.pay_run_items", static async (c, tx, t) =>
        {
            var (line, company) = await RunLineAsync(c, tx, t);
            var component = await ComponentOfAsync(c, tx, t, company);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.pay_run_items (tenant_id, id, line_id, component_id, component_code, kind, calculation, amount) VALUES (@t, @id, @line, @component, 'PROBE', 'earning', 'fixed', 100)", new { t, id, line, component }, tx);
            return new RowRef("app.pay_run_items", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid Line, Guid Company)> RunLineAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (run, company) = await RunAsync(c, tx, t);
        var (employee, _, partner) = await EmployeeOfAsync(c, tx, t, company);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pay_run_lines (tenant_id, id, run_id, line_no, employee_id, partner_id, employee_number) VALUES (@t, @id, @run, 1, @employee, @partner, @number)", new { t, id, run, employee, partner, number = Suffix(id) }, tx);
        return (id, company);
    }

    private static async Task<(Guid Run, Guid Company)> RunAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.pay_runs (tenant_id, id, company_id, number, period_year, period_month, period_start, period_end, posting_date, pay_date)
            VALUES (@t, @id, @company, @number, 2026, 3, '2026-03-01', '2026-03-31', '2026-03-31', '2026-03-31')
            """, new { t, id, company, number = Suffix(id) }, tx);
        return (id, company);
    }

    private static async Task<(Guid Component, Guid Company)> ComponentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        return (await ComponentOfAsync(c, tx, t, company), company);
    }

    private static async Task<Guid> ComponentOfAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, Guid company)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pay_components (tenant_id, id, company_id, code, name_i18n, kind, calculation) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', 'earning', 'fixed')", new { t, id, company, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Employee, Guid Company, Guid Partner)> EmployeeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        return await EmployeeOfAsync(c, tx, t, company);
    }

    private static async Task<(Guid Employee, Guid Company, Guid Partner)> EmployeeOfAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, Guid company)
    {
        var partner = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_employee) VALUES (@t, @partner, @code, '{\"en\":\"Probe\"}', true)", new { t, partner, code = Suffix(partner) }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.pay_employees (tenant_id, id, company_id, partner_id, number, hire_date, basic_salary, currency) VALUES (@t, @id, @company, @partner, @number, '2026-01-01', 1000, 'IQD')", new { t, id, company, partner, number = Suffix(id) }, tx);
        return (id, company, partner);
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

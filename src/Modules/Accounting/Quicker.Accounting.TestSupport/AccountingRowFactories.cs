using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Accounting.TestSupport;

/// <summary>Minimal valid rows for every Accounting tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class AccountingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.gl_charts", static async (c, tx, t) => new RowRef("app.gl_charts", $"id = '{await ChartAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.gl_account_categories", static async (c, tx, t) => new RowRef("app.gl_account_categories", $"id = '{await CategoryAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.gl_accounts", static async (c, tx, t) => new RowRef("app.gl_accounts", $"id = '{(await AccountAsync(c, tx, t)).Account}'"));
        IsolationRegistry.Register("app.gl_account_dimension_rules", static async (c, tx, t) =>
        {
            var (account, _) = await AccountAsync(c, tx, t);
            var dimension = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_dimensions (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id = dimension, code = "D" + dimension.ToString("N")[^8..].ToUpperInvariant() }, tx);
            await c.ExecuteAsync("INSERT INTO app.gl_account_dimension_rules (tenant_id, account_id, dimension_id, rule) VALUES (@t, @account, @dimension, 'required')", new { t, account, dimension }, tx);
            return new RowRef("app.gl_account_dimension_rules", $"account_id = '{account}'");
        });
        IsolationRegistry.Register("app.gl_account_mappings", static async (c, tx, t) =>
        {
            var (account, _) = await AccountAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.gl_account_mappings (tenant_id, account_id, statutory_chart_code, statutory_code) VALUES (@t, @account, 'IRAQ_UAS', '3')", new { t, account }, tx);
            return new RowRef("app.gl_account_mappings", $"account_id = '{account}'");
        });
        IsolationRegistry.Register("app.gl_posting_groups", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.gl_posting_groups (tenant_id, id, kind, code, name_i18n) VALUES (@t, @id, 'item', @code, '{}')", new { t, id, code = "G" + id.ToString("N")[^8..].ToUpperInvariant() }, tx);
            return new RowRef("app.gl_posting_groups", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.gl_posting_profiles", static async (c, tx, t) => new RowRef("app.gl_posting_profiles", $"id = '{(await ProfileAsync(c, tx, t)).Profile}'"));
        IsolationRegistry.Register("app.gl_posting_rules", static async (c, tx, t) =>
        {
            var (profile, _) = await ProfileAsync(c, tx, t);
            var (account, _) = await AccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.gl_posting_rules (tenant_id, id, profile_id, account_role, account_id) VALUES (@t, @id, @profile, 'Suspense', @account)", new { t, id, profile, account }, tx);
            return new RowRef("app.gl_posting_rules", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.gl_journal_entries", static async (c, tx, t) => new RowRef("app.gl_journal_entries", $"id = '{(await EntryAsync(c, tx, t)).Entry}'"));
        IsolationRegistry.Register("app.gl_journal_lines", static async (c, tx, t) => new RowRef("app.gl_journal_lines", $"id = '{(await EntryAsync(c, tx, t)).FirstLine}'"));
        IsolationRegistry.Register("app.gl_entry_links", static async (c, tx, t) =>
        {
            var (from, _, _, _, _) = await EntryAsync(c, tx, t);
            var (to, _, _, _, _) = await EntryAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.gl_entry_links (tenant_id, from_entry_id, to_entry_id, relation) VALUES (@t, @from, @to, 'corrects')", new { t, from, to }, tx);
            return new RowRef("app.gl_entry_links", $"from_entry_id = '{from}'");
        });
        IsolationRegistry.Register("app.gl_balances", static async (c, tx, t) =>
        {
            var (_, company, period, account, _) = await EntryAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.gl_balances (tenant_id, company_id, account_id, fiscal_period_id, currency_tc, debit_tc, credit_tc, debit_fc, credit_fc) VALUES (@t, @company, @account, @period, 'IQD', 1, 1, 1, 1)", new { t, company, account, period }, tx);
            return new RowRef("app.gl_balances", $"company_id = '{company}' AND account_id = '{account}'");
        });
    }

    private static async Task<(Guid Company, Guid Calendar)> CompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (fiscal, business) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @id, @code, '{}', 1, 12)", new { t, id = fiscal, code = "a" + fiscal.ToString("N")[^8..] }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @id, @code, '{}', ARRAY[0,1,2,3,4])", new { t, id = business, code = "a" + business.ToString("N")[^8..] }, tx);
        var company = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id = company, code = "A" + company.ToString("N")[^8..].ToUpperInvariant(), fiscal, business }, tx);
        return (company, fiscal);
    }

    private static async Task<(Guid Profile, Guid Company)> ProfileAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (company, _) = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_posting_profiles (tenant_id, id, company_id, code, name_i18n, valid_from) VALUES (@t, @id, @company, 'DEFAULT', '{}', '2026-01-01')", new { t, id, company }, tx);
        return (id, company);
    }

    /// <summary>A balanced two-line entry (the deferred constraint trigger checks it at commit).</summary>
    private static async Task<(Guid Entry, Guid Company, Guid Period, Guid Account, Guid FirstLine)> EntryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (company, calendar) = await CompanyAsync(c, tx, t);
        var (account, _) = await AccountAsync(c, tx, t);
        var (year, period, entry, firstLine) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_years (tenant_id, id, calendar_id, code, starts_on, ends_on, status) VALUES (@t, @id, @cal, 'FY2026', '2026-01-01', '2026-12-31', 'open')", new { t, id = year, cal = calendar }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_periods (tenant_id, id, fiscal_year_id, number, starts_on, ends_on) VALUES (@t, @id, @year, 1, '2026-01-01', '2026-01-31')", new { t, id = period, year }, tx);
        await c.ExecuteAsync("""
            INSERT INTO app.gl_journal_entries (tenant_id, id, company_id, number, posting_date, document_date, fiscal_year_id, fiscal_period_id, source_module, source_document_type, source_document_id, currency_tc, currency_fc, rate_tc_fc, line_count)
            VALUES (@t, @id, @company, @number, '2026-01-15', '2026-01-15', @year, @period, 'probe', 'probe', @doc, 'IQD', 'IQD', 1, 2)
            """, new { t, id = entry, company, number = "P-" + entry.ToString("N")[^8..], year, period, doc = Guid.CreateVersion7() }, tx);
        await c.ExecuteAsync("""
            INSERT INTO app.gl_journal_lines (tenant_id, id, entry_id, company_id, posting_date, line_no, account_id, account_role, debit_tc, credit_tc, currency_tc, rate_tc_fc, debit_fc, credit_fc, rate_date)
            VALUES (@t, @l1, @entry, @company, '2026-01-15', 1, @account, 'Suspense', 1, 0, 'IQD', 1, 1, 0, '2026-01-15'),
                   (@t, @l2, @entry, @company, '2026-01-15', 2, @account, 'Suspense', 0, 1, 'IQD', 1, 0, 1, '2026-01-15')
            """, new { t, l1 = firstLine, l2 = Guid.CreateVersion7(), entry, company, account }, tx);
        return (entry, company, period, account, firstLine);
    }

    private static async Task<Guid> ChartAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_charts (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}')", new { t, id, code = "C" + id.ToString("N")[^8..].ToUpperInvariant() }, tx);
        return id;
    }

    private static async Task<Guid> CategoryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_account_categories (tenant_id, id, code, name_i18n, statement) VALUES (@t, @id, @code, '{}', 'bs')", new { t, id, code = "c" + id.ToString("N")[^8..] }, tx);
        return id;
    }

    private static async Task<(Guid Account, Guid Chart)> AccountAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var chart = await ChartAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_accounts (tenant_id, id, chart_id, code, name_i18n, type) VALUES (@t, @id, @chart, @code, '{\"en\":\"Probe\"}', 'asset')", new { t, id, chart, code = id.ToString("N")[^6..] }, tx);
        return (id, chart);
    }
}

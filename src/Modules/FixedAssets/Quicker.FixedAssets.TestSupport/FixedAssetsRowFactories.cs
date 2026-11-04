using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.FixedAssets.TestSupport;

/// <summary>Minimal valid rows for every fixed-asset tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class FixedAssetsRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.fa_books", static async (c, tx, t) => new RowRef("app.fa_books", $"id = '{(await BookAsync(c, tx, t)).Book}'"));
        IsolationRegistry.Register("app.fa_asset_categories", static async (c, tx, t) => new RowRef("app.fa_asset_categories", $"id = '{await CategoryAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.fa_assets", static async (c, tx, t) => new RowRef("app.fa_assets", $"id = '{(await AssetAsync(c, tx, t)).Asset}'"));
        IsolationRegistry.Register("app.fa_asset_books", static async (c, tx, t) => new RowRef("app.fa_asset_books", $"id = '{(await AssetBookAsync(c, tx, t)).Value}'"));
        IsolationRegistry.Register("app.fa_schedule_lines", static async (c, tx, t) =>
        {
            var (value, _, _, _) = await AssetBookAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.fa_schedule_lines (tenant_id, id, asset_book_id, period_start, period_end, planned_amount) VALUES (@t, @id, @value, '2026-10-01', '2026-10-31', 10)", new { t, id, value }, tx);
            return new RowRef("app.fa_schedule_lines", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.fa_transactions", static async (c, tx, t) =>
        {
            var (_, asset, book, company) = await AssetBookAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.fa_transactions (tenant_id, id, company_id, asset_id, book_id, posting_date, kind, cost_delta) VALUES (@t, @id, @company, @asset, @book, '2026-10-31', 'acquisition', 100)", new { t, id, company, asset, book }, tx);
            return new RowRef("app.fa_transactions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.fa_depreciation_runs", static async (c, tx, t) =>
        {
            var (book, company) = await BookAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.fa_depreciation_runs (tenant_id, id, company_id, book_id, number, period_start, period_end, posting_date) VALUES (@t, @id, @company, @book, @number, '2026-10-01', '2026-10-31', '2026-10-31')", new { t, id, company, book, number = Suffix(id) }, tx);
            return new RowRef("app.fa_depreciation_runs", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.fa_usage", static async (c, tx, t) =>
        {
            var (value, _, _, _) = await AssetBookAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.fa_usage (tenant_id, id, asset_book_id, period_start, units) VALUES (@t, @id, @value, '2026-10-01', 5)", new { t, id, value }, tx);
            return new RowRef("app.fa_usage", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.fa_verifications", static async (c, tx, t) =>
        {
            var (asset, _) = await AssetAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.fa_verifications (tenant_id, id, asset_id, verified_on) VALUES (@t, @id, @asset, '2026-10-31')", new { t, id, asset }, tx);
            return new RowRef("app.fa_verifications", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "F" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<Guid> CategoryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.fa_asset_categories (tenant_id, id, code, name_i18n, default_method, default_life_months, convention) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', 'straight_line', 60, 'full_month')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Book, Guid Company)> BookAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.fa_books (tenant_id, id, company_id, code, name_i18n, posts_to_gl) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', false)", new { t, id, company, code = Suffix(id) }, tx);
        return (id, company);
    }

    private static async Task<(Guid Asset, Guid Company)> AssetAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var category = await CategoryAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.fa_assets (tenant_id, id, company_id, number, name_i18n, category_id, acquisition_date, in_service_date, cost, currency, acquisition_source) VALUES (@t, @id, @company, @number, '{\"en\":\"Probe\"}', @category, '2026-10-01', '2026-10-01', 100, 'IQD', 'clearing')", new { t, id, company, number = Suffix(id), category }, tx);
        return (id, company);
    }

    private static async Task<(Guid Value, Guid Asset, Guid Book, Guid Company)> AssetBookAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (asset, company) = await AssetAsync(c, tx, t);
        var book = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.fa_books (tenant_id, id, company_id, code, name_i18n, posts_to_gl) VALUES (@t, @book, @company, @code, '{\"en\":\"Probe\"}', false)", new { t, book, company, code = Suffix(book) }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.fa_asset_books (tenant_id, id, asset_id, book_id, method, life_months, convention, depreciation_start, cost) VALUES (@t, @id, @asset, @book, 'straight_line', 60, 'full_month', '2026-10-01', 100)", new { t, id, asset, book }, tx);
        return (id, asset, book, company);
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

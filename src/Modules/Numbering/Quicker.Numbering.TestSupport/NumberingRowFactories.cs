using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Numbering.TestSupport;

/// <summary>Minimal valid rows for every Numbering tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class NumberingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.num_series", static async (c, tx, t) => new RowRef("app.num_series", $"id = '{await SeriesAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.num_series_counters", static async (c, tx, t) =>
        {
            var series = await SeriesAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.num_series_counters (tenant_id, series_id, period_key, next_number) VALUES (@t, @series, '', 1)", new { t, series }, tx);
            return new RowRef("app.num_series_counters", $"series_id = '{series}'");
        });
        IsolationRegistry.Register("app.num_allocations", static async (c, tx, t) =>
        {
            var series = await SeriesAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.num_allocations (tenant_id, id, series_id, period_key, number, text, document_type, document_id) VALUES (@t, @id, @series, '', 1, 'PRB-000001', 'probe', @doc)", new { t, id, series, doc = Guid.CreateVersion7() }, tx);
            return new RowRef("app.num_allocations", $"id = '{id}'");
        });
    }

    private static async Task<Guid> SeriesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (fiscal, business) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @id, @code, '{}', 1, 12)", new { t, id = fiscal, code = "n" + fiscal.ToString("N")[^8..] }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @id, @code, '{}', ARRAY[0,1,2,3,4])", new { t, id = business, code = "n" + business.ToString("N")[^8..] }, tx);
        var company = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id = company, code = "N" + company.ToString("N")[^8..].ToUpperInvariant(), fiscal, business }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.num_series (tenant_id, id, code, document_type, company_id, template) VALUES (@t, @id, @code, 'probe', @company, 'PRB-{seq:6}')", new { t, id, code = "S" + id.ToString("N")[^8..].ToUpperInvariant(), company }, tx);
        return id;
    }
}

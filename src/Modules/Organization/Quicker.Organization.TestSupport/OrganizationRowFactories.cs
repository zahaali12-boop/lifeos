using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Organization.TestSupport;

/// <summary>Minimal valid rows for every Organization tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class OrganizationRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.org_fiscal_calendars", static async (c, tx, t) => Row("app.org_fiscal_calendars", await FiscalCalendarAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_fiscal_years", static async (c, tx, t) => Row("app.org_fiscal_years", await FiscalYearAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_fiscal_periods", static async (c, tx, t) => Row("app.org_fiscal_periods", await FiscalPeriodAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_business_calendars", static async (c, tx, t) => Row("app.org_business_calendars", await BusinessCalendarAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_holidays", static async (c, tx, t) =>
        {
            var calendar = await BusinessCalendarAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_holidays (tenant_id, id, calendar_id, on_date, name_i18n) VALUES (@t, @id, @cal, '2026-01-01', '{\"en\":\"Probe\"}')", new { t, id, cal = calendar }, tx);
            return Row("app.org_holidays", id);
        });
        IsolationRegistry.Register("app.org_companies", static async (c, tx, t) => Row("app.org_companies", await CompanyAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_dimensions", static async (c, tx, t) => Row("app.org_dimensions", await DimensionAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_dimension_values", static async (c, tx, t) => Row("app.org_dimension_values", await DimensionValueAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_dimension_sets", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_dimension_sets (tenant_id, id, hash, values) VALUES (@t, @id, @hash, '{\"PROBE\":\"00000000-0000-0000-0000-000000000000\"}')", new { t, id, hash = id.ToByteArray() }, tx);
            return Row("app.org_dimension_sets", id);
        });
        IsolationRegistry.Register("app.org_branches", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            var value = await DimensionValueAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_branches (tenant_id, id, company_id, code, name_i18n, dimension_value_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', @value)", new { t, id, company, code = Suffix(id), value }, tx);
            return Row("app.org_branches", id);
        });
        IsolationRegistry.Register("app.org_period_module_states", static async (c, tx, t) =>
        {
            var period = await FiscalPeriodAsync(c, tx, t);
            var company = await CompanyAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.org_period_module_states (tenant_id, period_id, company_id, module, state) VALUES (@t, @period, @company, 'GL', 'open')", new { t, period, company }, tx);
            return new RowRef("app.org_period_module_states", $"period_id = '{period}'");
        });
        IsolationRegistry.Register("app.org_company_currencies", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.org_company_currencies (tenant_id, company_id, currency, display_decimals) VALUES (@t, @company, 'EUR', 2)", new { t, company }, tx);
            return new RowRef("app.org_company_currencies", $"company_id = '{company}' AND currency = 'EUR'");
        });
        IsolationRegistry.Register("app.org_exchange_rate_types", static async (c, tx, t) => Row("app.org_exchange_rate_types", await RateTypeAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_exchange_rates", static async (c, tx, t) =>
        {
            var type = await RateTypeAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_exchange_rates (tenant_id, id, rate_type_id, from_currency, to_currency, valid_from, rate) VALUES (@t, @id, @type, 'USD', 'IQD', '2026-01-01', 1310)", new { t, id, type }, tx);
            return Row("app.org_exchange_rates", id);
        });
        IsolationRegistry.Register("app.org_uoms", static async (c, tx, t) => Row("app.org_uoms", await UomAsync(c, tx, t)));
        IsolationRegistry.Register("app.org_uom_conversions", static async (c, tx, t) =>
        {
            var (from, to) = (await UomAsync(c, tx, t), await UomAsync(c, tx, t));
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_uom_conversions (tenant_id, id, from_uom_id, to_uom_id, numerator, denominator) VALUES (@t, @id, @from, @to, 12, 1)", new { t, id, from, to }, tx);
            return Row("app.org_uom_conversions", id);
        });
        IsolationRegistry.Register("app.org_settings", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.org_settings (tenant_id, id, key, value, value_type) VALUES (@t, @id, @key, '\"x\"', 'string')", new { t, id, key = "probe." + Suffix(id).ToLowerInvariant() }, tx);
            return Row("app.org_settings", id);
        });
    }

    private static RowRef Row(string table, Guid id) => new(table, $"id = '{id}'");

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<Guid> FiscalCalendarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @id, @code, '{}', 1, 12)", new { t, id, code = Suffix(id).ToLowerInvariant() }, tx);
        return id;
    }

    private static async Task<Guid> FiscalYearAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var calendar = await FiscalCalendarAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_years (tenant_id, id, calendar_id, code, starts_on, ends_on, status) VALUES (@t, @id, @cal, 'FY2026', '2026-01-01', '2026-12-31', 'open')", new { t, id, cal = calendar }, tx);
        return id;
    }

    private static async Task<Guid> FiscalPeriodAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var year = await FiscalYearAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_periods (tenant_id, id, fiscal_year_id, number, starts_on, ends_on) VALUES (@t, @id, @year, 1, '2026-01-01', '2026-01-31')", new { t, id, year }, tx);
        return id;
    }

    private static async Task<Guid> BusinessCalendarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @id, @code, '{}', ARRAY[0,1,2,3,4])", new { t, id, code = Suffix(id).ToLowerInvariant() }, tx);
        return id;
    }

    private static async Task<Guid> CompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (fiscal, business) = (await FiscalCalendarAsync(c, tx, t), await BusinessCalendarAsync(c, tx, t));
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }

    private static async Task<Guid> DimensionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_dimensions (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> DimensionValueAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var dimension = await DimensionAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_dimension_values (tenant_id, id, dimension_id, code, name_i18n) VALUES (@t, @id, @dim, @code, '{}')", new { t, id, dim = dimension, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> RateTypeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_exchange_rate_types (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id).ToLowerInvariant() }, tx);
        return id;
    }

    private static async Task<Guid> UomAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_uoms (tenant_id, id, code, name_i18n, family) VALUES (@t, @id, @code, '{}', 'count')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }
}

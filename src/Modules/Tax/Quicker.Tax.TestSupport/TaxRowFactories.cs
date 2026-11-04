using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Tax.TestSupport;

/// <summary>Minimal valid rows for every Tax tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class TaxRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.tax_regimes", static async (c, tx, t) => new RowRef("app.tax_regimes", $"id = '{await RegimeAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.tax_codes", static async (c, tx, t) => new RowRef("app.tax_codes", $"id = '{(await CodeAsync(c, tx, t)).Code}'"));
        IsolationRegistry.Register("app.tax_rates", static async (c, tx, t) =>
        {
            var (code, _) = await CodeAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.tax_rates (tenant_id, tax_code_id, valid_from, rate_pct) VALUES (@t, @code, DATE '2026-01-01', 15)", new { t, code }, tx);
            return new RowRef("app.tax_rates", $"tax_code_id = '{code}'");
        });
        IsolationRegistry.Register("app.tax_groups", static async (c, tx, t) => new RowRef("app.tax_groups", $"id = '{await GroupAsync(c, tx, t, "item")}'"));
        IsolationRegistry.Register("app.tax_determination_rules", static async (c, tx, t) =>
        {
            var (code, regime) = await CodeAsync(c, tx, t);
            var group = await GroupAsync(c, tx, t, "item");
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.tax_determination_rules (tenant_id, id, regime_id, direction, item_tax_group_id, tax_code_id) VALUES (@t, @id, @regime, 'sales', @group, @code)", new { t, id, regime, group, code }, tx);
            return new RowRef("app.tax_determination_rules", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.tax_registrations", static async (c, tx, t) => new RowRef("app.tax_registrations", $"id = '{(await RegistrationAsync(c, tx, t)).Registration}'"));
        IsolationRegistry.Register("app.tax_exemptions", static async (c, tx, t) =>
        {
            var (code, regime) = await CodeAsync(c, tx, t);
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.tax_exemptions (tenant_id, id, partner_id, regime_id, tax_code_id, certificate_number, valid_from) VALUES (@t, @id, @partner, @regime, @code, 'CERT-1', DATE '2026-01-01')", new { t, id, partner, regime, code }, tx);
            return new RowRef("app.tax_exemptions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.tax_return_periods", static async (c, tx, t) =>
        {
            var (_, company, regime) = await RegistrationAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.tax_return_periods (tenant_id, id, company_id, regime_id, period_start, period_end) VALUES (@t, @id, @company, @regime, DATE '2026-01-01', DATE '2026-03-31')", new { t, id, company, regime }, tx);
            return new RowRef("app.tax_return_periods", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.tax_entries", static async (c, tx, t) =>
        {
            var (_, company, regime) = await RegistrationAsync(c, tx, t);
            var code = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.tax_codes (tenant_id, id, regime_id, code, kind) VALUES (@t, @code, @regime, @name, 'vat')", new { t, code, regime, name = Suffix(code) }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.tax_entries (tenant_id, id, company_id, regime_id, tax_code_id, direction, posting_date, source_module, source_document_type, source_document_id, currency, rate_pct, base_tc, tax_tc, base_fc, tax_fc)
                VALUES (@t, @id, @company, @regime, @code, 'sales', DATE '2026-01-15', 'sales', 'sales_invoice', @doc, 'IQD', 15, 100, 15, 100, 15)
                """, new { t, id, company, regime, code, doc = Guid.CreateVersion7() }, tx);
            return new RowRef("app.tax_entries", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "T" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<Guid> RegimeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.tax_regimes (tenant_id, id, code, country, family) VALUES (@t, @id, @code, 'SA', 'vat')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Code, Guid Regime)> CodeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var regime = await RegimeAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.tax_codes (tenant_id, id, regime_id, code, kind) VALUES (@t, @id, @regime, @code, 'vat')", new { t, id, regime, code = Suffix(id) }, tx);
        return (id, regime);
    }

    private static async Task<Guid> GroupAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, string kind)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.tax_groups (tenant_id, id, kind, code) VALUES (@t, @id, @kind, @code)", new { t, id, kind, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Registration, Guid Company, Guid Regime)> RegistrationAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var regime = await RegimeAsync(c, tx, t);
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.tax_registrations (tenant_id, id, company_id, regime_id, registration_number) VALUES (@t, @id, @company, @regime, '300000000000003')", new { t, id, company, regime }, tx);
        return (id, company, regime);
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
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'SA', 'SAR', @fiscal, @business, 'Asia/Riyadh')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }
}

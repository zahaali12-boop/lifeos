using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Einvoicing.TestSupport;

/// <summary>Minimal valid rows for every E-invoicing tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class EinvoicingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.einv_devices", static async (c, tx, t) => new RowRef("app.einv_devices", $"id = '{(await DeviceAsync(c, tx, t)).Device}'"));
        IsolationRegistry.Register("app.einv_submissions", static async (c, tx, t) =>
        {
            var (device, company) = await DeviceAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.einv_submissions (tenant_id, id, company_id, device_id, document_type, document_id, document_number, kind, type_code, invoice_counter, invoice_hash, previous_hash, signed_xml) VALUES (@t, @id, @company, @device, 'sales_invoice', @doc, @number, 'standard', '388', 1, 'hash', 'previous', '<Invoice/>')", new { t, id, company, device, doc = Guid.CreateVersion7(), number = Suffix(id) }, tx);
            return new RowRef("app.einv_submissions", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "E" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid Device, Guid Company)> DeviceAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.einv_devices (tenant_id, id, company_id, name, environment, serial_number, common_name, organization_unit, organization, industry, private_key_enc) VALUES (@t, @id, @company, 'Probe', 'simulator', @serial, 'probe', 'Probe', 'Probe', 'Trading', 'enc')", new { t, id, company, serial = "1-Quicker|2-Probe|3-" + id.ToString("N") }, tx);
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
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'SA', 'SAR', @fiscal, @business, 'Asia/Riyadh')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }
}

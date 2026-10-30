using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Receivables.TestSupport;

/// <summary>Minimal valid rows for every Receivables tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class ReceivablesRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.ar_open_items", static async (c, tx, t) => new RowRef("app.ar_open_items", $"id = '{(await OpenItemAsync(c, tx, t, "invoice", 10m)).Item}'"));
        IsolationRegistry.Register("app.ar_settlements", static async (c, tx, t) =>
        {
            var (settled, company, partner) = await OpenItemAsync(c, tx, t, "invoice", 10m);
            var settling = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ar_open_items (tenant_id, id, company_id, partner_id, kind, document_type, document_id, document_number, posting_date, document_date, due_date, currency, original_tc, original_fc, remaining_tc, remaining_fc) VALUES (@t, @id, @company, @partner, 'credit_note', 'sales_invoice', @doc, @number, '2026-09-22', '2026-09-22', '2026-09-22', 'IQD', -10, -10, -10, -10)", new { t, id = settling, company, partner, doc = Guid.CreateVersion7(), number = Suffix(settling) }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ar_settlements (tenant_id, id, company_id, settling_item_id, settled_item_id, settlement_date, kind, currency, amount_tc, amount_fc_settled_item, amount_fc_settling_item) VALUES (@t, @id, @company, @settling, @settled, '2026-09-22', 'credit_application', 'IQD', 1, 1, 1)", new { t, id, company, settling, settled }, tx);
            return new RowRef("app.ar_settlements", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "R" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid Item, Guid Company, Guid Partner)> OpenItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, string kind, decimal amount)
    {
        var company = await CompanyAsync(c, tx, t);
        var partner = await PartnerAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ar_open_items (tenant_id, id, company_id, partner_id, kind, document_type, document_id, document_number, posting_date, document_date, due_date, currency, original_tc, original_fc, remaining_tc, remaining_fc) VALUES (@t, @id, @company, @partner, @kind, 'sales_invoice', @doc, @number, '2026-09-22', '2026-09-22', '2026-10-22', 'IQD', @amount, @amount, @amount, @amount)", new { t, id, company, partner, kind, doc = Guid.CreateVersion7(), number = Suffix(id), amount }, tx);
        return (id, company, partner);
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
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }
}

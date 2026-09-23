using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Partners.TestSupport;

/// <summary>Minimal valid rows for every Partners tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PartnersRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.ptr_partners", static async (c, tx, t) => new RowRef("app.ptr_partners", $"id = '{await PartnerAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_contacts", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_contacts (tenant_id, id, partner_id, name_i18n) VALUES (@t, @id, @partner, '{\"en\":\"Probe\"}')", new { t, id, partner }, tx);
            return new RowRef("app.ptr_contacts", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_partner_addresses", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_partner_addresses (tenant_id, id, partner_id, country) VALUES (@t, @id, @partner, 'IQ')", new { t, id, partner }, tx);
            return new RowRef("app.ptr_partner_addresses", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_partner_bank_accounts", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_partner_bank_accounts (tenant_id, id, partner_id, bank_name, currency, account_number_enc, account_number_masked) VALUES (@t, @id, @partner, 'Probe Bank', 'IQD', 'enc', '**1234')", new { t, id, partner }, tx);
            return new RowRef("app.ptr_partner_bank_accounts", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_partner_tax_registrations", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_partner_tax_registrations (tenant_id, id, partner_id, country, registration_type, number) VALUES (@t, @id, @partner, 'IQ', 'tin', @number)", new { t, id, partner, number = Suffix(id) }, tx);
            return new RowRef("app.ptr_partner_tax_registrations", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_payment_terms", static async (c, tx, t) => new RowRef("app.ptr_payment_terms", $"id = '{await PaymentTermsAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_payment_term_lines", static async (c, tx, t) =>
        {
            var terms = await PaymentTermsAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.ptr_payment_term_lines (tenant_id, terms_id, sequence, percentage, days) VALUES (@t, @terms, 1, 100, 30)", new { t, terms }, tx);
            return new RowRef("app.ptr_payment_term_lines", $"terms_id = '{terms}'");
        });
        IsolationRegistry.Register("app.ptr_delivery_terms", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_delivery_terms (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
            return new RowRef("app.ptr_delivery_terms", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_wht_codes", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_wht_codes (tenant_id, id, code, name_i18n, rate_pct) VALUES (@t, @id, @code, '{}', 3.5)", new { t, id, code = Suffix(id) }, tx);
            return new RowRef("app.ptr_wht_codes", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_supplier_groups", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_supplier_groups (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
            return new RowRef("app.ptr_supplier_groups", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_supplier_accounts", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var company = await CompanyAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_supplier_accounts (tenant_id, id, partner_id, company_id, currency) VALUES (@t, @id, @partner, @company, 'IQD')", new { t, id, partner, company }, tx);
            return new RowRef("app.ptr_supplier_accounts", $"id = '{id}'");
        });
    }

    private static async Task<Guid> PartnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', true)", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> PaymentTermsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_payment_terms (tenant_id, id, code, name_i18n, due_days) VALUES (@t, @id, @code, '{}', 30)", new { t, id, code = Suffix(id) }, tx);
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

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^10..].ToUpperInvariant();
}

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
        IsolationRegistry.Register("app.ptr_customer_groups", static async (c, tx, t) => new RowRef("app.ptr_customer_groups", $"id = '{await CustomerGroupAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_customer_accounts", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var company = await CompanyAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_customer_accounts (tenant_id, id, partner_id, company_id, currency, credit_limit) VALUES (@t, @id, @partner, @company, 'IQD', 1000)", new { t, id, partner, company }, tx);
            return new RowRef("app.ptr_customer_accounts", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_commission_plans", static async (c, tx, t) => new RowRef("app.ptr_commission_plans", $"id = '{await CommissionPlanAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_commission_rules", static async (c, tx, t) =>
        {
            var plan = await CommissionPlanAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.ptr_commission_rules (tenant_id, plan_id, sequence, rate_pct) VALUES (@t, @plan, 1, 2.5)", new { t, plan }, tx);
            return new RowRef("app.ptr_commission_rules", $"plan_id = '{plan}'");
        });
        IsolationRegistry.Register("app.ptr_sales_reps", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_sales_reps (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}')", new { t, id, code = Suffix(id) }, tx);
            return new RowRef("app.ptr_sales_reps", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_pipeline_stages", static async (c, tx, t) => new RowRef("app.ptr_pipeline_stages", $"id = '{await StageAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_opportunities", static async (c, tx, t) => new RowRef("app.ptr_opportunities", $"id = '{await OpportunityAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.ptr_opportunity_stage_changes", static async (c, tx, t) =>
        {
            var opportunity = await OpportunityAsync(c, tx, t);
            var stage = await c.ExecuteScalarAsync<Guid>("SELECT stage_id FROM app.ptr_opportunities WHERE tenant_id = @t AND id = @opportunity", new { t, opportunity }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_opportunity_stage_changes (tenant_id, id, opportunity_id, to_stage_id, probability_pct, expected_amount) VALUES (@t, @id, @opportunity, @stage, 10, 500)", new { t, id, opportunity, stage }, tx);
            return new RowRef("app.ptr_opportunity_stage_changes", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.ptr_crm_activities", static async (c, tx, t) =>
        {
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.ptr_crm_activities (tenant_id, id, partner_id, kind, subject) VALUES (@t, @id, @partner, 'call', 'Probe call')", new { t, id, partner }, tx);
            return new RowRef("app.ptr_crm_activities", $"id = '{id}'");
        });
    }

    private static async Task<Guid> CustomerGroupAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_customer_groups (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> CommissionPlanAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_commission_plans (tenant_id, id, code, name_i18n, currency) VALUES (@t, @id, @code, '{}', 'IQD')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> StageAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_pipeline_stages (tenant_id, id, code, name_i18n, sort_order, default_probability) VALUES (@t, @id, @code, '{}', 1, 10)", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> OpportunityAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var partner = await PartnerAsync(c, tx, t);
        var company = await CompanyAsync(c, tx, t);
        var stage = await StageAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.ptr_opportunities (tenant_id, id, company_id, number, partner_id, title, stage_id, currency, probability_pct)
            VALUES (@t, @id, @company, @number, @partner, 'Probe deal', @stage, 'IQD', 10)
            """, new { t, id, company, number = Suffix(id), partner, stage }, tx);
        return id;
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

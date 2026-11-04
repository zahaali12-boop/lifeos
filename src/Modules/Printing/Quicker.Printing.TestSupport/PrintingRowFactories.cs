using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Printing.TestSupport;

/// <summary>Minimal valid rows for every Printing tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PrintingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.prt_templates", static async (c, tx, t) => new RowRef("app.prt_templates", $"id = '{(await TemplateAsync(c, tx, t)).Template}'"));
        IsolationRegistry.Register("app.prt_template_versions", static async (c, tx, t) => new RowRef("app.prt_template_versions", $"id = '{(await TemplateAsync(c, tx, t)).Version}'"));
        IsolationRegistry.Register("app.prt_renders", static async (c, tx, t) => new RowRef("app.prt_renders", $"id = '{await RenderAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.prt_emails", static async (c, tx, t) =>
        {
            var render = await RenderAsync(c, tx, t);
            var company = await c.QuerySingleAsync<Guid>("SELECT company_id FROM app.prt_renders WHERE tenant_id = @t AND id = @render", new { t, render }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prt_emails (tenant_id, id, company_id, render_id, document_type, to_address, subject) VALUES (@t, @id, @company, @render, 'sales_invoice', 'probe@example.test', 'Probe')", new { t, id, company, render }, tx);
            return new RowRef("app.prt_emails", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prt_share_links", static async (c, tx, t) =>
        {
            var render = await RenderAsync(c, tx, t);
            var company = await c.QuerySingleAsync<Guid>("SELECT company_id FROM app.prt_renders WHERE tenant_id = @t AND id = @render", new { t, render }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prt_share_links (tenant_id, id, company_id, render_id, document_type, token_hash, expires_at) VALUES (@t, @id, @company, @render, 'sales_invoice', @hash, now() + interval '7 days')",
                new { t, id, company, render, hash = Suffix(id) }, tx);
            return new RowRef("app.prt_share_links", $"id = '{id}'");
        });
    }

    private static async Task<Guid> RenderAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (template, version) = await TemplateAsync(c, tx, t);
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.prt_renders (tenant_id, id, company_id, document_type, variant, template_id, template_version_id, template_version, language_mode, title, number, file_name, fingerprint, html) VALUES (@t, @id, @company, 'sales_invoice', 'standard', @template, @version, 1, 'bilingual', 'Probe', 'PRB-1', 'prb-1.pdf', @fingerprint, '<html></html>')",
            new { t, id, company, template, version, fingerprint = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Template, Guid Version)> TemplateAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.prt_templates (tenant_id, id, code, document_type, variant, name_i18n) VALUES (@t, @id, @code, 'sales_invoice', 'standard', '{\"en\":\"Probe\"}')", new { t, id, code = "PRB-" + Suffix(id) }, tx);
        await c.ExecuteAsync("INSERT INTO app.prt_template_versions (tenant_id, id, template_id, version, html) VALUES (@t, @version, @id, 1, '<html></html>')", new { t, version, id }, tx);
        return (id, version);
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

    private static string Suffix(Guid id) => id.ToString("N")[^8..].ToUpperInvariant();
}

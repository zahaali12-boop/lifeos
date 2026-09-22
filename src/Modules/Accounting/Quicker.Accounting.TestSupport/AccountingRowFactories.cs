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

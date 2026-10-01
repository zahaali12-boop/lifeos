using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Audit.TestSupport;

/// <summary>Minimal valid rows for the Audit tenant tables, so the isolation suite (hard scenario 18) covers them.</summary>
public static class AuditRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.aud_events", static async (c, tx, t) =>
        {
            var id = await InsertEventAsync(c, tx, t);
            return new RowRef("app.aud_events", $"id = '{id}'");
        });

        // The head row is created by the chaining trigger; the application role cannot insert one directly.
        IsolationRegistry.Register("app.aud_chain_heads", static async (c, tx, t) =>
        {
            await InsertEventAsync(c, tx, t);
            return new RowRef("app.aud_chain_heads", $"tenant_id = '{t}'");
        });
    }

    public static async Task<Guid> InsertEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, string action = "created", string? reason = null)
    {
        var id = Guid.CreateVersion7();
        await connection.ExecuteAsync("""
            INSERT INTO app.aud_events (tenant_id, id, occurred_at, actor_type, actor_display, entity_type, entity_id, entity_display, action, after, reason)
            VALUES (@t, @id, now(), 'system', 'probe', 'probe', @id, 'probe', @action, '{"note": "probe"}'::jsonb, @reason)
            """, new { t = tenantId, id, action, reason }, transaction);
        return id;
    }

    /// <summary>Alters or removes a stored event as the schema owner in maintenance mode: what a hostile DBA could do.</summary>
    public static async Task TamperAsync(string ownerConnectionString, Guid tenantId, string sql, object? parameters = null)
    {
        await using var owner = new NpgsqlConnection(ownerConnectionString);
        await owner.OpenAsync();
        await owner.ExecuteAsync("SELECT set_config('app.maintenance', 'on', false)");
        await owner.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = tenantId.ToString() });
        await owner.ExecuteAsync(sql, parameters);
    }
}

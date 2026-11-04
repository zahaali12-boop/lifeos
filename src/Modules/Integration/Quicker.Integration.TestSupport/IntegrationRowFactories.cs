using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Integration.TestSupport;

/// <summary>Minimal valid rows for the Integration tenant tables, so the isolation suite (hard scenario 18) covers them.</summary>
public static class IntegrationRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.int_webhook_subscriptions", static async (c, tx, t) => new RowRef("app.int_webhook_subscriptions", $"id = '{await SubscriptionAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.int_webhook_deliveries", static async (c, tx, t) =>
        {
            var subscription = await SubscriptionAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.int_webhook_deliveries (tenant_id, id, subscription_id, event_id, event_type, payload) VALUES (@t, @id, @s, @e, 'probe.event', '{}')", new { t, id, s = subscription, e = Guid.CreateVersion7() }, tx);
            return new RowRef("app.int_webhook_deliveries", $"id = '{id}'");
        });
    }

    private static async Task<Guid> SubscriptionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.int_webhook_subscriptions (tenant_id, id, name, url, secret_enc) VALUES (@t, @id, @name, 'https://example.test/hook', 'enc')", new { t, id, name = "probe-" + id.ToString("N")[^8..] }, tx);
        return id;
    }
}

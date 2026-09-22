using Dapper;
using Quicker.Testing;

namespace Quicker.Collaboration.TestSupport;

/// <summary>Minimal valid rows for the Collaboration tenant tables, so the isolation suite (hard scenario 18) covers them.</summary>
public static class CollaborationRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.col_notifications", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_notifications (tenant_id, id, membership_id, kind, title_i18n) VALUES (@t, @id, @m, 'probe.event', '{\"en\":\"Probe\"}')", new { t, id, m = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_notifications", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_notification_preferences", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_notification_preferences (tenant_id, id, membership_id, kind) VALUES (@t, @id, @m, '*')", new { t, id, m = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_notification_preferences", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_email_log", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_email_log (tenant_id, id, to_address, subject, text_body) VALUES (@t, @id, 'probe@example.test', 'Probe', 'probe')", new { t, id }, tx);
            return new RowRef("app.col_email_log", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_attachments", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_attachments (tenant_id, id, entity_type, entity_id, file_name, content_type, size_bytes, sha256, storage_key) VALUES (@t, @id, 'probe', @e, 'probe.txt', 'text/plain', 5, 'abc', @key)", new { t, id, e = Guid.CreateVersion7(), key = "tenants/" + t.ToString("N") + "/attachments/" + id.ToString("N") }, tx);
            return new RowRef("app.col_attachments", $"id = '{id}'");
        });
    }
}

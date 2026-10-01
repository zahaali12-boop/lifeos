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
        IsolationRegistry.Register("app.col_comments", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_comments (tenant_id, id, entity_type, entity_id, author_membership_id, body) VALUES (@t, @id, 'probe', @e, @m, 'probe')", new { t, id, e = Guid.CreateVersion7(), m = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_comments", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_activities", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_activities (tenant_id, id, entity_type, entity_id, kind) VALUES (@t, @id, 'probe', @e, 'probe.event')", new { t, id, e = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_activities", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_document_links", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_document_links (tenant_id, id, from_type, from_id, to_type, to_id, relation) VALUES (@t, @id, 'probe', @a, 'probe', @b, 'related')", new { t, id, a = Guid.CreateVersion7(), b = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_document_links", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_saved_views", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_saved_views (tenant_id, id, entity_type, name, owner_membership_id) VALUES (@t, @id, 'probe', @name, @m)", new { t, id, name = "probe-" + id.ToString("N")[^8..], m = Guid.CreateVersion7() }, tx);
            return new RowRef("app.col_saved_views", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.col_custom_fields", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.col_custom_fields (tenant_id, id, entity_type, key, label_i18n, type) VALUES (@t, @id, 'probe', @key, '{\"en\":\"Probe\"}', 'text')", new { t, id, key = "probe_" + id.ToString("N")[^6..] }, tx);
            return new RowRef("app.col_custom_fields", $"id = '{id}'");
        });
    }
}

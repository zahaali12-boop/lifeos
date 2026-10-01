using Dapper;
using Quicker.Testing;

namespace Quicker.Identity.TestSupport;

/// <summary>Minimal valid rows for every Identity tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class IdentityRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.idn_roles", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_roles (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}')", new { t, id, code = "probe_" + id.ToString("N")[^6..] }, tx);
            return new RowRef("app.idn_roles", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_role_permissions", static async (c, tx, t) =>
        {
            var role = await RoleAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.idn_role_permissions (tenant_id, role_id, permission_key) VALUES (@t, @role, 'identity.user.read')", new { t, role }, tx);
            return new RowRef("app.idn_role_permissions", $"role_id = '{role}'");
        });

        IsolationRegistry.Register("app.idn_role_assignments", static async (c, tx, t) =>
        {
            var (role, membership) = (await RoleAsync(c, tx, t), await MembershipAsync(c, tx, t));
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_role_assignments (tenant_id, id, membership_id, role_id) VALUES (@t, @id, @m, @role)", new { t, id, m = membership, role }, tx);
            return new RowRef("app.idn_role_assignments", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_assignment_scopes", static async (c, tx, t) =>
        {
            var (role, membership) = (await RoleAsync(c, tx, t), await MembershipAsync(c, tx, t));
            var assignment = Guid.CreateVersion7();
            var scope = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_role_assignments (tenant_id, id, membership_id, role_id) VALUES (@t, @id, @m, @role)", new { t, id = assignment, m = membership, role }, tx);
            await c.ExecuteAsync("INSERT INTO app.idn_assignment_scopes (tenant_id, assignment_id, scope_type, scope_id) VALUES (@t, @a, 'company', @s)", new { t, a = assignment, s = scope }, tx);
            return new RowRef("app.idn_assignment_scopes", $"assignment_id = '{assignment}'");
        });

        IsolationRegistry.Register("app.idn_field_rules", static async (c, tx, t) =>
        {
            var role = await RoleAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_field_rules (tenant_id, id, role_id, entity_type, field, access) VALUES (@t, @id, @role, 'customer', 'credit_limit', 'hidden')", new { t, id, role }, tx);
            return new RowRef("app.idn_field_rules", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_document_type_rules", static async (c, tx, t) =>
        {
            var role = await RoleAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_document_type_rules (tenant_id, id, role_id, document_type, action, allowed) VALUES (@t, @id, @role, 'sales_invoice', 'post', false)", new { t, id, role }, tx);
            return new RowRef("app.idn_document_type_rules", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_sod_rules", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_sod_rules (tenant_id, id, permission_a, permission_b) VALUES (@t, @id, @a, @b)", new { t, id, a = "x.probe." + id.ToString("N")[^6..], b = "y.probe.z" }, tx);
            return new RowRef("app.idn_sod_rules", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_sod_exceptions", static async (c, tx, t) =>
        {
            var rule = Guid.CreateVersion7();
            var membership = await MembershipAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_sod_rules (tenant_id, id, permission_a, permission_b) VALUES (@t, @id, @a, 'y.probe.z')", new { t, id = rule, a = "x.probe." + rule.ToString("N")[^6..] }, tx);
            await c.ExecuteAsync("INSERT INTO app.idn_sod_exceptions (tenant_id, id, sod_rule_id, membership_id, reason, approved_by) VALUES (@t, @id, @rule, @m, 'probe', @m2)", new { t, id, rule, m = membership, m2 = Guid.CreateVersion7() }, tx);
            return new RowRef("app.idn_sod_exceptions", $"id = '{id}'");
        });

        IsolationRegistry.Register("app.idn_api_keys", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.idn_api_keys (tenant_id, id, name, prefix, key_hash) VALUES (@t, @id, 'probe', 'abcdef', @hash)", new { t, id, hash = id.ToByteArray() }, tx);
            return new RowRef("app.idn_api_keys", $"id = '{id}'");
        });
    }

    private static async Task<Guid> RoleAsync(Npgsql.NpgsqlConnection c, Npgsql.NpgsqlTransaction tx, Guid tenant)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.idn_roles (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t = tenant, id, code = "r_" + id.ToString("N")[^8..] }, tx);
        return id;
    }

    /// <summary>Memberships live in control (no RLS); the factory needs one that belongs to the tenant.</summary>
    private static async Task<Guid> MembershipAsync(Npgsql.NpgsqlConnection c, Npgsql.NpgsqlTransaction tx, Guid tenant)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO control.users (id, email, display_name) VALUES (@id, @email, 'Probe')", new { id = user, email = $"probe-{user:N}@example.test" }, tx);
        await c.ExecuteAsync("INSERT INTO control.tenant_memberships (id, tenant_id, user_id, status) VALUES (@id, @t, @u, 'active')", new { id = membership, t = tenant, u = user }, tx);
        return membership;
    }
}

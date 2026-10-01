using Dapper;
using Npgsql;

namespace Quicker.Migrator.Demo;

/// <summary>
/// Removes one tenant physically: every row of every table that carries a tenant_id (children before parents,
/// derived from the foreign keys), the tenant's users when they belong to no other tenant, then the tenant row.
/// Runs on the owner connection inside the tenant's session so forced row-level security lets the rows through,
/// and in maintenance mode so append-only ledgers (the audit chain) accept the deletion. Only the demo seeder
/// uses it; production tenants are never purged this way (ADR-0007).
/// </summary>
internal static class TenantPurge
{
    public static async Task<int> PurgeAsync(string ownerConnection, Guid tenantId, string emailDomain, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnection);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.tenant_id', @tenant, true), set_config('app.maintenance', 'on', true)",
            new { tenant = tenantId.ToString() }, transaction, cancellationToken: cancellationToken));
        // Deferrable constraints (the company/chart pair) are checked at commit, when every row of the tenant is gone.
        await connection.ExecuteAsync(new CommandDefinition("SET CONSTRAINTS ALL DEFERRED", transaction: transaction, cancellationToken: cancellationToken));

        var tables = (await connection.QueryAsync<(string Schema, string Table)>(new CommandDefinition("""
            SELECT n.nspname, c.relname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.attname = 'tenant_id' AND NOT a.attisdropped
              AND c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname IN ('app', 'control', 'ops', 'reporting')
              AND NOT (n.nspname = 'control' AND c.relname = 'tenants')
            """, transaction: transaction, cancellationToken: cancellationToken)))
            .Select(static t => t.Schema + "." + t.Table)
            .ToHashSet(StringComparer.Ordinal);

        var edges = (await connection.QueryAsync<(string Child, string Parent)>(new CommandDefinition("""
            SELECT conrelid::regclass::text, confrelid::regclass::text
            FROM pg_constraint
            WHERE contype = 'f' AND conrelid <> confrelid AND NOT condeferrable
            """, transaction: transaction, cancellationToken: cancellationToken)))
            .Where(e => tables.Contains(e.Child) && tables.Contains(e.Parent))
            .ToList();

        var deleted = 0;
        var remaining = new HashSet<string>(tables, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            // A table can go once no remaining table still references it through an immediate constraint. A cycle of
            // immediate constraints would leave nothing deletable; then delete the rest in name order and let
            // PostgreSQL report the constraint (cycles are declared deferrable instead, see V0010).
            var deletable = remaining.Where(t => !edges.Any(e => e.Parent == t && remaining.Contains(e.Child))).Order(StringComparer.Ordinal).ToList();
            if (deletable.Count == 0)
            {
                deletable = remaining.Order(StringComparer.Ordinal).ToList();
            }

            foreach (var table in deletable)
            {
                deleted += await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {table} WHERE tenant_id = @tenant", new { tenant = tenantId }, transaction, cancellationToken: cancellationToken));
                remaining.Remove(table);
            }
        }

        deleted += await RemoveOrphanUsersAsync(connection, transaction, emailDomain, cancellationToken);
        deleted += await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM control.tenants WHERE id = @tenant", new { tenant = tenantId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>Users of the demo mail domain that no tenant references any more (a tenant deleted by hand).</summary>
    public static Task<int> RemoveOrphanUsersAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string emailDomain, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM control.users u
            WHERE lower(u.email) LIKE @pattern
              AND NOT EXISTS (SELECT 1 FROM control.tenant_memberships m WHERE m.user_id = u.id)
            """, new { pattern = "%@" + emailDomain.ToLowerInvariant() }, transaction, cancellationToken: cancellationToken));
}

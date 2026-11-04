using Dapper;
using Npgsql;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Testing;

/// <summary>Helpers to create tenants and open tenant-scoped transactions in tests.</summary>
public static class TestTenants
{
    private static readonly DbOptions DefaultOptions = new();

    public static async Task<TenantId> CreateAsync(TestDatabase db, string? slug = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = TenantId.New();
        slug ??= "t-" + id.Value.ToString("N")[^12..]; // random tail; the head of a UUIDv7 is the timestamp
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO control.tenants (id, slug, name) VALUES (@id, @slug, @name)",
            new { id = id.Value, slug, name = "Tenant " + slug });
        return id;
    }

    /// <summary>Opens an app-role connection with a transaction carrying the tenant session (or none when tenant is null).</summary>
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> OpenAsync(TestDatabase db, TenantId? tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (tenant is { } id)
        {
            await TenantSession.ApplyAsync(connection, transaction, TenantContext.System(id, "test-" + Guid.NewGuid().ToString("N")[..8]), DefaultOptions, cancellationToken);
        }

        return (connection, transaction);
    }
}

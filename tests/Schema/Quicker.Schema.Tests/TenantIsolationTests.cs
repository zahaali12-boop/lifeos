using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Schema.Tests;

/// <summary>
/// Hard scenario 18 at the database layer: for every registered tenant table, rows of tenant A are invisible and
/// unmodifiable under tenant B's session, and invisible with no session at all.
/// </summary>
public sealed class TenantIsolationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Probe_table_proves_the_isolation_mechanism()
    {
        // A scratch tenant table created by the owner and put under the contract with the shared procedures.
        await using (var owner = new NpgsqlConnection(fixture.Db.OwnerConnectionString))
        {
            await owner.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS app.zz_isolation_probe (
                  tenant_id uuid NOT NULL,
                  id uuid NOT NULL,
                  note text NOT NULL DEFAULT '',
                  PRIMARY KEY (tenant_id, id));
                CALL app.enable_tenant_rls('app.zz_isolation_probe');
                GRANT SELECT, INSERT, UPDATE, DELETE ON app.zz_isolation_probe TO quicker_app;
                """);
        }

        IsolationRegistry.Register("app.zz_isolation_probe", static async (connection, transaction, tenantId) =>
        {
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync("INSERT INTO app.zz_isolation_probe (tenant_id, id, note) VALUES (@t, @id, 'x')", new { t = tenantId, id }, transaction);
            return new RowRef("app.zz_isolation_probe", $"id = '{id}'");
        });

        await AssertIsolatedAsync("app.zz_isolation_probe", IsolationRegistry.All()["app.zz_isolation_probe"]);
    }

    [Fact]
    public async Task Every_registered_tenant_table_is_isolated()
    {
        foreach (var (table, factory) in IsolationRegistry.All().Where(static kv => !kv.Key.StartsWith("app.zz_", StringComparison.Ordinal)))
        {
            await AssertIsolatedAsync(table, factory);
        }
    }

    [Fact]
    public async Task Missing_tenant_context_yields_no_rows_not_all_rows()
    {
        var tenantA = await TestTenants.CreateAsync(fixture.Db);
        var (connection, transaction) = await TestTenants.OpenAsync(fixture.Db, null);
        await using (connection)
        await using (transaction)
        {
            var current = await connection.ExecuteScalarAsync<Guid?>("SELECT app.current_tenant()", transaction: transaction);
            current.ShouldBeNull();
            // Any tenant table, even the catalogue's own view of tenant-scoped tables, must be empty without context.
            var count = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM control.tenants WHERE id = @id", new { id = tenantA.Value }, transaction);
            count.ShouldBe(1L, "control-plane tables are not tenant scoped; RLS applies only to app/reporting");
        }
    }

    private async Task AssertIsolatedAsync(string table, TenantRowFactory factory)
    {
        var tenantA = await TestTenants.CreateAsync(fixture.Db);
        var tenantB = await TestTenants.CreateAsync(fixture.Db);

        RowRef rowA;
        var (connA, txA) = await TestTenants.OpenAsync(fixture.Db, tenantA);
        await using (connA)
        {
            rowA = await factory(connA, txA, tenantA.Value);
            await txA.CommitAsync();
        }

        // Tenant B: cannot see, update or delete A's row; cannot insert a row claiming to be A.
        var (connB, txB) = await TestTenants.OpenAsync(fixture.Db, tenantB);
        await using (connB)
        {
            (await connB.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {rowA.Predicate}", transaction: txB)).ShouldBe(0L, $"{table}: B can see A's row");

            // Append-only tables revoke DELETE from the application role altogether (42501), which is stricter still.
            await connB.ExecuteAsync("SAVEPOINT delete_probe", transaction: txB);
            try
            {
                (await connB.ExecuteAsync($"DELETE FROM {table} WHERE {rowA.Predicate}", transaction: txB)).ShouldBe(0, $"{table}: B deleted A's row");
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, "42501", StringComparison.Ordinal))
            {
                await connB.ExecuteAsync("ROLLBACK TO SAVEPOINT delete_probe", transaction: txB);
            }

            var leak = await Should.ThrowAsync<PostgresException>(async () => await factory(connB, txB, tenantA.Value));
            leak.SqlState.ShouldBe("42501", $"{table}: B inserted a row for A (expected RLS WITH CHECK violation)");
            await txB.RollbackAsync();
        }

        // Tenant A sees its own row.
        var (connA2, txA2) = await TestTenants.OpenAsync(fixture.Db, tenantA);
        await using (connA2)
        {
            (await connA2.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE {rowA.Predicate}", transaction: txA2)).ShouldBe(1L, $"{table}: A cannot see its own row");
            await txA2.RollbackAsync();
        }

        // No context: nothing.
        var (connNone, txNone) = await TestTenants.OpenAsync(fixture.Db, null);
        await using (connNone)
        {
            (await connNone.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}", transaction: txNone)).ShouldBe(0L, $"{table}: rows visible without tenant context");
            await txNone.RollbackAsync();
        }
    }
}

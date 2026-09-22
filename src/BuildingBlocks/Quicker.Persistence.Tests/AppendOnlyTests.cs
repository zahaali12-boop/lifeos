using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Persistence.Tests;

/// <summary>ADR-0007: append-only tables refuse UPDATE and DELETE from every role, except in audited maintenance mode.</summary>
public sealed class AppendOnlyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Append_only_tables_refuse_update_and_delete_even_for_the_owner()
    {
        await using var owner = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        await owner.OpenAsync(); // one session, so set_config(..., false) below persists across statements
        await owner.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS app.zz_ledger_probe (tenant_id uuid NOT NULL, id uuid NOT NULL PRIMARY KEY, amount numeric(20,6) NOT NULL);
            CALL app.enable_tenant_rls('app.zz_ledger_probe');
            CALL app.make_append_only('app.zz_ledger_probe');
            """);

        var tenant = await TestTenants.CreateAsync(fixture.Db);
        var id = Guid.CreateVersion7();

        // Insert as the application role under the tenant session.
        var (conn, tx) = await TestTenants.OpenAsync(fixture.Db, tenant);
        await using (conn)
        {
            await conn.ExecuteAsync("INSERT INTO app.zz_ledger_probe VALUES (@t, @id, 10)", new { t = tenant.Value, id }, tx);
            await tx.CommitAsync();
        }

        // App role: privilege revoked → 42501 insufficient_privilege.
        var (conn2, tx2) = await TestTenants.OpenAsync(fixture.Db, tenant);
        await using (conn2)
        {
            var ex = await Should.ThrowAsync<PostgresException>(async () => await conn2.ExecuteAsync("UPDATE app.zz_ledger_probe SET amount = 11 WHERE id = @id", new { id }, tx2));
            ex.SqlState.ShouldBe("42501");
            await tx2.RollbackAsync();
        }

        // Owner (has privileges): the trigger still refuses.
        // Session-level tenant context for the owner (a GUC change rolls back with a failed batch, so set it alone).
        await owner.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = tenant.Value.ToString() });
        var ownerEx = await Should.ThrowAsync<PostgresException>(async () =>
            await owner.ExecuteAsync("UPDATE app.zz_ledger_probe SET amount = 11 WHERE id = @id", new { id }));
        ownerEx.MessageText.ShouldContain("append_only_violation");

        var deleteEx = await Should.ThrowAsync<PostgresException>(async () =>
            await owner.ExecuteAsync("DELETE FROM app.zz_ledger_probe WHERE id = @id", new { id }));
        deleteEx.MessageText.ShouldContain("append_only_violation");

        // Maintenance mode (owner only, audited backfills): allowed.
        await owner.ExecuteAsync("SELECT set_config('app.maintenance', 'on', false); DELETE FROM app.zz_ledger_probe WHERE id = @id", new { id });
        await owner.ExecuteAsync("SELECT set_config('app.maintenance', '', false)");
        (await owner.ExecuteScalarAsync<long>("SELECT count(*) FROM app.zz_ledger_probe WHERE id = @id", new { id })).ShouldBe(0L);
    }
}

using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Persistence.Tests;

/// <summary>The database contract from ADR-0003/ADR-0004/ADR-0005, checked against the migrated schema.</summary>
public sealed class SchemaConventionsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private sealed class TenantTable
    {
        public string SchemaName { get; set; } = string.Empty;

        public string TableName { get; set; } = string.Empty;

        public bool HasTenantId { get; set; }

        public bool RlsEnabled { get; set; }

        public bool RlsForced { get; set; }

        public bool HasPolicy { get; set; }

        public bool IsAppendOnly { get; set; }
    }

    [Fact]
    public async Task Every_tenant_table_has_tenant_id_and_forced_rls_with_a_policy()
    {
        await using var connection = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        var tables = (await connection.QueryAsync<TenantTable>("SELECT * FROM ops.tenant_tables")).ToList();

        var violations = tables
            .Where(static t => !t.HasTenantId || !t.RlsEnabled || !t.RlsForced || !t.HasPolicy)
            .Select(static t => $"{t.SchemaName}.{t.TableName}: tenant_id={t.HasTenantId} rls={t.RlsEnabled} forced={t.RlsForced} policy={t.HasPolicy}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_tenant_table_has_an_isolation_row_factory()
    {
        await using var connection = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        var tables = await connection.QueryAsync<TenantTable>("SELECT * FROM ops.tenant_tables");

        var missing = tables
            .Select(static t => $"{t.SchemaName}.{t.TableName}")
            .Where(static name => !IsolationRegistry.Has(name))
            .ToList();

        missing.ShouldBeEmpty("every tenant table needs a TenantRowFactory so the isolation suite covers it");
    }

    [Fact]
    public async Task No_floating_point_columns_exist()
    {
        await using var connection = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        var columns = await connection.QueryAsync<(string TableSchema, string TableName, string ColumnName, string DataType)>(
            "SELECT table_schema, table_name, column_name, data_type FROM ops.floating_point_columns");
        columns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Foreign_keys_between_tenant_tables_include_tenant_id()
    {
        await using var connection = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        var offenders = await connection.QueryAsync<string>("""
            SELECT conrelid::regclass::text || ' -> ' || confrelid::regclass::text || ' (' || conname || ')'
            FROM pg_constraint c
            JOIN pg_class src ON src.oid = c.conrelid
            JOIN pg_namespace ns ON ns.oid = src.relnamespace
            JOIN pg_class dst ON dst.oid = c.confrelid
            JOIN pg_namespace nd ON nd.oid = dst.relnamespace
            WHERE c.contype = 'f'
              AND ns.nspname IN ('app', 'reporting')
              AND nd.nspname IN ('app', 'reporting')
              AND NOT EXISTS (
                SELECT 1 FROM unnest(c.conkey) k JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k
                WHERE a.attname = 'tenant_id')
            """);
        offenders.ShouldBeEmpty("foreign keys between tenant tables must be composite on tenant_id (ADR-0004)");
    }

    [Fact]
    public async Task Application_role_cannot_bypass_rls_or_own_tables()
    {
        await using var connection = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        var role = await connection.QuerySingleAsync<(bool Super, bool Bypass, bool CreateDb)>(
            "SELECT rolsuper, rolbypassrls, rolcreatedb FROM pg_roles WHERE rolname = 'quicker_app'");
        role.Super.ShouldBeFalse();
        role.Bypass.ShouldBeFalse();
        role.CreateDb.ShouldBeFalse();

        var ownedByApp = await connection.QueryAsync<string>(
            "SELECT c.relname FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = 'quicker_app' AND c.relkind = 'r'");
        ownedByApp.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migrations_are_idempotent_on_replay()
    {
        var runner = new Migrator.MigrationRunner(fixture.Db.OwnerConnectionString, new SilentLog());
        runner.PendingScripts().ShouldBeEmpty();
        var result = runner.Migrate();
        result.Successful.ShouldBeTrue(result.Error?.ToString());
        runner.Seed().Successful.ShouldBeTrue();
    }

    private sealed class SilentLog : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args)
        {
        }

        public void LogDebug(string format, params object[] args)
        {
        }

        public void LogInformation(string format, params object[] args)
        {
        }

        public void LogWarning(string format, params object[] args)
        {
        }

        public void LogError(string format, params object[] args)
        {
        }

        public void LogError(Exception ex, string format, params object[] args)
        {
        }
    }
}

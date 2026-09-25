using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Helpers;
using Npgsql;

namespace Quicker.Migrator;

/// <summary>
/// Applies the database contract (ADR-0003): versioned scripts once (journaled in ops.schemaversions), repeatable
/// scripts on every run, reference seeds on every run (they are idempotent MERGE/UPSERT scripts).
/// Also bootstraps the application role so a fresh cluster works with one command.
/// </summary>
public sealed class MigrationRunner(string ownerConnectionString, IUpgradeLog log)
{
    private static readonly Assembly Scripts = typeof(MigrationRunner).Assembly;

    public string OwnerConnectionString { get; } = ownerConnectionString;

    public async Task EnsureDatabaseAndRolesAsync(string appRolePassword, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(OwnerConnectionString);
        var database = builder.Database ?? throw new InvalidOperationException("Connection string has no database.");
        builder.Database = "postgres";

        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync(cancellationToken);

        // A parameter placeholder is never substituted inside a dollar-quoted DO block body (it is opaque text to the
        // outer parser), so the password is handed to the session through set_config first and read back with
        // current_setting() inside the block, keeping it bound rather than string-built.
        await using (var stage = admin.CreateCommand())
        {
            stage.CommandText = "SELECT set_config('quicker.bootstrap_password', @password, false)";
            stage.Parameters.AddWithValue("password", appRolePassword);
            await stage.ExecuteScalarAsync(cancellationToken);
        }

        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText = """
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'quicker_app') THEN
                    EXECUTE format('CREATE ROLE quicker_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L', current_setting('quicker.bootstrap_password'));
                  END IF;
                  -- Owner is a member of the app role so maintenance can SET ROLE quicker_app and terminate its sessions.
                  IF NOT EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles g ON g.oid = m.member
                                 WHERE r.rolname = 'quicker_app' AND g.rolname = current_user) THEN
                    EXECUTE format('GRANT quicker_app TO %I', current_user);
                  END IF;
                END $$;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var exists = admin.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            exists.Parameters.AddWithValue("name", database);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE {QuoteIdentifier(database)}";
                await create.ExecuteNonQueryAsync(cancellationToken);
                log.LogInformation("Created database {0}", database);
            }
        }
    }

    public DatabaseUpgradeResult Migrate()
    {
        var versioned = DeployChanges.To
            .PostgresqlDatabase(OwnerConnectionString)
            .WithScriptsEmbeddedInAssembly(Scripts, static name => name.StartsWith("migrations.", StringComparison.Ordinal))
            .JournalToPostgresqlTable("ops", "schemaversions")
            .WithTransactionPerScript()
            .LogTo(log)
            .Build();

        // The journal table must exist before the first script runs; DbUp creates it lazily in schema ops,
        // which V0001 creates. Bootstrap the schema first.
        EnsureOpsSchema();

        var result = versioned.PerformUpgrade();
        if (!result.Successful)
        {
            return result;
        }

        var repeatable = DeployChanges.To
            .PostgresqlDatabase(OwnerConnectionString)
            .WithScriptsEmbeddedInAssembly(Scripts, static name => name.StartsWith("repeatable.", StringComparison.Ordinal))
            .JournalTo(new NullJournal())
            .WithTransactionPerScript()
            .LogTo(log)
            .Build();

        return repeatable.PerformUpgrade();
    }

    public DatabaseUpgradeResult Seed()
    {
        var seeds = DeployChanges.To
            .PostgresqlDatabase(OwnerConnectionString)
            .WithScriptsEmbeddedInAssembly(Scripts, static name => name.StartsWith("seed.", StringComparison.Ordinal))
            .JournalTo(new NullJournal())
            .WithTransactionPerScript()
            .LogTo(log)
            .Build();

        return seeds.PerformUpgrade();
    }

    public IReadOnlyList<string> PendingScripts()
    {
        var engine = DeployChanges.To
            .PostgresqlDatabase(OwnerConnectionString)
            .WithScriptsEmbeddedInAssembly(Scripts, static name => name.StartsWith("migrations.", StringComparison.Ordinal))
            .JournalToPostgresqlTable("ops", "schemaversions")
            .Build();
        return engine.GetScriptsToExecute().Select(static s => s.Name).ToList();
    }

    private void EnsureOpsSchema()
    {
        using var connection = new NpgsqlConnection(OwnerConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS ops";
        cmd.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using DbUp.Engine.Output;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Quicker.Migrator.Maintenance;

/// <summary>A table whose row count in the restored database differs from the manifest (null: missing on one side).</summary>
public sealed record TableMismatch(string Table, long? Expected, long? Restored);

public sealed record RestoreResult(string Database, BackupManifest Manifest, IReadOnlyList<TableMismatch> Mismatches, VerifyResult? Verification, TimeSpan Elapsed)
{
    public bool Passed => Mismatches.Count == 0 && Verification is { Passed: true };
}

/// <summary>
/// <c>restore --from FILE --database NAME [--pg-bin DIR]</c> (roadmap 10.3): restores a backup into a new database
/// and proves it. The file must still match the SHA-256 of its manifest; the target must not exist (a restore never
/// writes over a database); the restore is one transaction; then every table's rows are counted against the manifest
/// and the invariant harness runs over every active tenant of the restored database. Nothing is dropped on failure:
/// the database stays for inspection and the report says why it failed.
/// </summary>
public static partial class DatabaseRestore
{
    public static async Task<RestoreResult> RunAsync(string ownerConnection, string appPassword, IConfiguration? deployment, string file, string database, string? pgBin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (string.IsNullOrWhiteSpace(database) || !DatabaseName().IsMatch(database))
        {
            throw new InvalidOperationException("--database takes a new database name of lower-case letters, digits and underscores (at most 63, starting with a letter).");
        }

        var stopwatch = Stopwatch.StartNew();
        var path = Path.GetFullPath(file);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"There is no backup at {path}.");
        }

        var manifest = await BackupManifest.ReadAsync(path, cancellationToken);
        var hash = await BackupManifest.HashAsync(path, cancellationToken);
        if (!string.Equals(hash, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path} does not match its manifest (SHA-256 {hash}, expected {manifest.Sha256}): the file changed or is incomplete.");
        }

        var target = new NpgsqlConnectionStringBuilder(ownerConnection) { Database = database, Pooling = false };
        var admin = new NpgsqlConnectionStringBuilder(ownerConnection) { Database = "postgres", Pooling = false };
        int serverMajor;
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            exists.Parameters.AddWithValue("name", database);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new InvalidOperationException($"The database {database} already exists; a restore goes into a new database only.");
            }

            if (!string.Equals(admin.Username, manifest.OwnerRole, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The backup's objects belong to the role {manifest.OwnerRole}; restore it connected as that role (now {admin.Username}).");
            }

            serverMajor = await PgTools.ServerMajorVersionAsync(connection, cancellationToken);
        }

        if (serverMajor < manifest.ServerMajorVersion)
        {
            throw new InvalidOperationException($"The backup comes from PostgreSQL {manifest.ServerMajorVersion}; this server is {serverMajor}. Restore on {manifest.ServerMajorVersion} or later.");
        }

        var tools = await PgTools.LocateAsync(serverMajor, pgBin, cancellationToken);

        // Roles are cluster-wide and not in the dump; the application role must exist before its grants are restored.
        await new MigrationRunner(target.ConnectionString, new NullUpgradeLog()).EnsureDatabaseAndRolesAsync(appPassword, cancellationToken);
        var (exitCode, errors) = await tools.RunAsync("pg_restore", target.ConnectionString, ["--exit-on-error", "--single-transaction", "--dbname=" + database, path], cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"pg_restore failed (exit {exitCode}); the database {database} was left empty for inspection: {errors}");
        }

        SortedDictionary<string, long> restored;
        await using (var connection = new NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            restored = await BackupManifest.CountRowsAsync(connection, null, cancellationToken);
        }

        var mismatches = manifest.Tables.Keys.Union(restored.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(table => new TableMismatch(table, manifest.Tables.TryGetValue(table, out var e) ? e : null, restored.TryGetValue(table, out var r) ? r : null))
            .Where(static m => m.Expected != m.Restored)
            .ToList();

        var app = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Username = "quicker_app", Password = appPassword };
        var verification = await VerifyBooks.RunAsync(target.ConnectionString, app.ConnectionString, deployment, null, cancellationToken);
        return new RestoreResult(database, manifest, mismatches, verification, stopwatch.Elapsed);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex DatabaseName();

    private sealed class NullUpgradeLog : IUpgradeLog
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

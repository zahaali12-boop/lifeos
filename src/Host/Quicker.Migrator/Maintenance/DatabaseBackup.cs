using System.Data;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace Quicker.Migrator.Maintenance;

public sealed record BackupResult(string File, BackupManifest Manifest, TimeSpan Elapsed);

/// <summary>
/// <c>backup --out FILE [--pg-bin DIR]</c> (ADR-0024 backups, roadmap 10.3): a pg_dump of the whole database in
/// custom format, taken inside a snapshot this command exports, so the row counts in the manifest are exactly what
/// the dump holds even while the system keeps writing. Every tenant's rows are in it, so the role must see past
/// row-level security (superuser or BYPASSRLS); a role that cannot is refused before anything is written.
/// </summary>
public static class DatabaseBackup
{
    public static async Task<BackupResult> RunAsync(string ownerConnection, string file, string? pgBin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var path = Path.GetFullPath(file);
        if (File.Exists(path) || File.Exists(BackupManifest.PathFor(path)))
        {
            throw new InvalidOperationException($"{path} already exists; a backup never overwrites another one.");
        }

        var stopwatch = Stopwatch.StartNew();
        var database = new NpgsqlConnectionStringBuilder(ownerConnection).Database
            ?? throw new InvalidOperationException("The owner connection names no database.");
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var (role, seesEverything) = await connection.QuerySingleAsync<(string, bool)>(
            "SELECT current_user::text, rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user");
        if (!seesEverything)
        {
            throw new InvalidOperationException($"The role {role} is subject to row-level security, so a dump would miss rows; back up with a superuser or a role with BYPASSRLS.");
        }

        var serverMajor = await PgTools.ServerMajorVersionAsync(connection, cancellationToken);
        var tools = await PgTools.LocateAsync(serverMajor, pgBin, cancellationToken);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The exported snapshot lives as long as this transaction: pg_dump imports it, and the counts below read it.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var (snapshot, takenAt) = await connection.QuerySingleAsync<(string, DateTimeOffset)>(new CommandDefinition("SELECT pg_export_snapshot(), now()", transaction: transaction, cancellationToken: cancellationToken));
        var dump = tools.RunAsync("pg_dump", ownerConnection, ["--format=custom", "--snapshot=" + snapshot, "--file=" + path, "--dbname=" + database], cancellationToken);
        SortedDictionary<string, long> tables;
        string lastMigration;
        try
        {
            tables = await BackupManifest.CountRowsAsync(connection, transaction, cancellationToken);
            lastMigration = await BackupManifest.LastMigrationAsync(connection, transaction, cancellationToken);
        }
        catch
        {
            await dump;
            TryDelete(path);
            throw;
        }

        var (exitCode, errors) = await dump;
        await transaction.CommitAsync(cancellationToken);

        if (exitCode != 0)
        {
            TryDelete(path);
            throw new InvalidOperationException($"pg_dump failed (exit {exitCode}): {errors}");
        }

        var manifest = new BackupManifest(
            BackupManifest.CurrentFormat,
            takenAt.ToUniversalTime(),
            database,
            role,
            serverMajor,
            lastMigration,
            await BackupManifest.HashAsync(path, cancellationToken),
            new FileInfo(path).Length,
            tables);
        await manifest.WriteAsync(path, cancellationToken);
        return new BackupResult(path, manifest, stopwatch.Elapsed);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A partial dump left behind is reported by the failure itself.
        }
    }
}

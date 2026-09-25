using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;

namespace Quicker.Migrator.Maintenance;

/// <summary>
/// Written next to every backup as <c>FILE.manifest.json</c>: what the dump holds, counted in the very snapshot the
/// dump was taken in, and the file's SHA-256. A restore refuses a file that no longer matches its hash and proves
/// completeness by counting the same tables again in the restored database.
/// </summary>
public sealed record BackupManifest(
    int FormatVersion,
    DateTimeOffset TakenAt,
    string Database,
    string OwnerRole,
    int ServerMajorVersion,
    string LastMigration,
    string Sha256,
    long SizeBytes,
    IReadOnlyDictionary<string, long> Tables)
{
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string PathFor(string backupFile) => backupFile + ".manifest.json";

    public async Task WriteAsync(string backupFile, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(PathFor(backupFile), JsonSerializer.Serialize(this, Json), cancellationToken);

    public static async Task<BackupManifest> ReadAsync(string backupFile, CancellationToken cancellationToken)
    {
        var path = PathFor(backupFile);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The backup has no manifest ({path}); only backups taken with the backup command can be restored and checked.");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            ?? throw new InvalidOperationException($"The manifest {path} is empty.");
        return manifest.FormatVersion == CurrentFormat
            ? manifest
            : throw new InvalidOperationException($"The manifest {path} has format {manifest.FormatVersion}; this version reads format {CurrentFormat}.");
    }

    public static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// Rows of every table outside the system schemas (partitions counted through their parent, extension tables
    /// left out), keyed <c>schema.table</c>, read inside whatever snapshot the connection's transaction holds.
    /// </summary>
    public static async Task<SortedDictionary<string, long>> CountRowsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var tables = await connection.QueryAsync<(string Schema, string Table)>(new CommandDefinition("""
            SELECT n.nspname, c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
            ORDER BY 1, 2
            """, transaction: transaction, cancellationToken: cancellationToken));
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (schema, table) in tables)
        {
            var name = Quote(schema) + "." + Quote(table);
            counts[schema + "." + table] = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {name}", transaction: transaction, cancellationToken: cancellationToken));
        }

        return counts;
    }

    public static async Task<string> LastMigrationAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT scriptname FROM ops.schemaversions ORDER BY scriptname DESC LIMIT 1", transaction: transaction, cancellationToken: cancellationToken)) ?? string.Empty;

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

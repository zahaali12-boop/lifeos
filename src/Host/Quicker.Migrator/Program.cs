using System.Globalization;
using DbUp.Engine.Output;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Quicker.Migrator.Demo;
using Quicker.Migrator.Maintenance;

namespace Quicker.Migrator;

/// <summary>
/// Usage: quicker-migrate [migrate|seed|demo|status|all]  (default: all)
///   migrate  create roles and database, apply versioned and repeatable scripts
///   seed     apply the reference-data seeds (idempotent)
///   demo     migrate, seed, then rebuild the demo tenant from scratch (ADR-0029)
///   all      migrate, seed, and create the demo tenant only when it is missing
///   rebuild-balances --tenant SLUG [--company CODE]   recompute gl_balances from the journal lines (ADR-0007)
///   operator --email ADDRESS [--revoke]   make a person a platform operator, or no longer one
///   backup --out FILE [--pg-bin DIR]   dump the whole database with a manifest (row counts in the dump's snapshot, SHA-256)
///   restore --from FILE --database NAME [--pg-bin DIR]   restore a backup into a new database, count it against the manifest, run the harness
///   verify [--tenant SLUG]   run the invariant harness over every active tenant (or one)
/// Configuration: QUICKER__DB__OWNERCONNECTION, QUICKER__DB__APPPASSWORD, QUICKER__DB__APPCONNECTION (env) or appsettings.json;
/// QUICKER__BACKUP__PGBIN for the PostgreSQL client directory; storage and audit anchoring settings as the API reads them.
/// (A named entry point rather than top-level statements: test support references this host next to the API host.)
/// </summary>
internal static class MigratorProgram
{
    private static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var ownerConnection = configuration["QUICKER:DB:OWNERCONNECTION"] ?? configuration["Quicker:Db:OwnerConnection"]
            ?? "Host=localhost;Port=5432;Database=quicker;Username=quicker_owner;Password=quicker";
        var appPassword = configuration["QUICKER:DB:APPPASSWORD"] ?? configuration["Quicker:Db:AppPassword"] ?? "quicker";
        var appConnection = configuration["QUICKER:DB:APPCONNECTION"] ?? configuration["Quicker:Db:AppConnection"]
            ?? new NpgsqlConnectionStringBuilder(ownerConnection) { Username = "quicker_app", Password = appPassword }.ConnectionString;
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        var log = new ConsoleUpgradeLog();
        var migrator = new MigrationRunner(ownerConnection, log);

        switch (command)
        {
            case "status":
                foreach (var script in migrator.PendingScripts())
                {
                    Console.WriteLine($"pending: {script}");
                }

                return 0;

            case "migrate":
                await migrator.EnsureDatabaseAndRolesAsync(appPassword);
                return Report(migrator.Migrate());

            case "seed":
                return Report(migrator.Seed());

            case "all":
            case "demo":
                await migrator.EnsureDatabaseAndRolesAsync(appPassword);
                var migrated = migrator.Migrate();
                if (!migrated.Successful)
                {
                    return Report(migrated);
                }

                var seeded = migrator.Seed();
                if (!seeded.Successful)
                {
                    return Report(seeded);
                }

                Report(seeded);
                return ReportDemo(await DemoSeeder.SeedAsync(ownerConnection, appConnection, reseed: command == "demo"));

            case "rebuild-balances":
                return await RebuildBalances.RunAsync(ownerConnection, appConnection, Option(args, "--tenant"), Option(args, "--company"));

            case "operator":
                return await PlatformOperators.RunAsync(ownerConnection, Option(args, "--email"), args.Contains("--revoke", StringComparer.Ordinal));

            case "backup":
                return await GuardedAsync(async () =>
                {
                    var file = Option(args, "--out") ?? throw new InvalidOperationException("backup needs --out <file>.");
                    var backup = await DatabaseBackup.RunAsync(ownerConnection, file, Option(args, "--pg-bin") ?? configuration["Quicker:Backup:PgBin"]);
                    var manifest = backup.Manifest;
                    Console.WriteLine($"Backed up database '{manifest.Database}' to {backup.File}: {Megabytes(manifest.SizeBytes)} MB, {manifest.Tables.Count} tables, {manifest.Tables.Values.Sum().ToString("N0", CultureInfo.InvariantCulture)} rows, schema at {manifest.LastMigration}, in {Seconds(backup.Elapsed)} s.");
                    Console.WriteLine($"Manifest: {BackupManifest.PathFor(backup.File)} (SHA-256 {manifest.Sha256}). Keep both files together.");
                    return 0;
                });

            case "restore":
                return await GuardedAsync(async () =>
                {
                    var file = Option(args, "--from") ?? throw new InvalidOperationException("restore needs --from <file>.");
                    var database = Option(args, "--database") ?? throw new InvalidOperationException("restore needs --database <new database name>.");
                    var restore = await DatabaseRestore.RunAsync(ownerConnection, appPassword, configuration, file, database, Option(args, "--pg-bin") ?? configuration["Quicker:Backup:PgBin"]);
                    Console.WriteLine($"Restored {file} (taken {restore.Manifest.TakenAt:yyyy-MM-dd HH:mm} UTC from '{restore.Manifest.Database}') into '{restore.Database}' in {Seconds(restore.Elapsed)} s.");
                    Console.WriteLine(restore.Mismatches.Count == 0
                        ? $"All {restore.Manifest.Tables.Count} tables hold the rows the manifest counted ({restore.Manifest.Tables.Values.Sum().ToString("N0", CultureInfo.InvariantCulture)})."
                        : $"FAILED: {restore.Mismatches.Count} table(s) differ from the manifest:");
                    foreach (var mismatch in restore.Mismatches)
                    {
                        Console.WriteLine($"  {mismatch.Table}: expected {mismatch.Expected?.ToString(CultureInfo.InvariantCulture) ?? "no table"}, restored {mismatch.Restored?.ToString(CultureInfo.InvariantCulture) ?? "no table"}");
                    }

                    if (restore.Verification is { } verification)
                    {
                        VerifyBooks.Print(verification);
                    }

                    return restore.Passed ? 0 : 1;
                });

            case "verify":
                return await GuardedAsync(async () =>
                {
                    var verification = await VerifyBooks.RunAsync(ownerConnection, appConnection, configuration, Option(args, "--tenant"));
                    VerifyBooks.Print(verification);
                    return verification.Passed ? 0 : 1;
                });

            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Use migrate, seed, demo, status, all, rebuild-balances --tenant <slug> [--company <code>], operator --email <address> [--revoke], backup --out <file>, restore --from <file> --database <name> or verify [--tenant <slug>].");
                return 2;
        }
    }

    /// <summary>Refusals and failures of the maintenance commands are reported as one line, not a stack trace.</summary>
    private static async Task<int> GuardedAsync(Func<Task<int>> command)
    {
        try
        {
            return await command();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1048576m).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Report(DbUp.Engine.DatabaseUpgradeResult result)
    {
        if (result.Successful)
        {
            Console.WriteLine($"OK: {result.Scripts.Count()} script(s) executed.");
            return 0;
        }

        Console.Error.WriteLine($"FAILED at {result.ErrorScript?.Name}: {result.Error}");
        return 1;
    }

    private static int ReportDemo(DemoSeedResult result)
    {
        Console.WriteLine(result.Created
            ? $"Demo tenant '{DemoData.Slug}' seeded in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: {result.Companies} companies, {result.Branches} branches, {result.Users} users, {result.Rates} exchange rates, {result.Journals} journals posted ({result.Entries} ledger entries), {result.PeriodsClosed} period closings, {result.Items} items ({result.Variants} variants) in {result.Warehouses} warehouses with {result.StockLines} opening stock entries ({result.Lots} lots, {result.Serials} serials); the invariant harness passed."
            : $"Demo tenant '{DemoData.Slug}' already exists; run the 'demo' command to rebuild it.");
        Console.WriteLine($"Sign in as {DemoData.Owner.Email} with password {DemoData.Password} (every demo user shares it).");
        return 0;
    }
}

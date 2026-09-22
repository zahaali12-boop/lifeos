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
/// Configuration: QUICKER__DB__OWNERCONNECTION, QUICKER__DB__APPPASSWORD, QUICKER__DB__APPCONNECTION (env) or appsettings.json.
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

            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Use migrate, seed, demo, status, all or rebuild-balances --tenant <slug> [--company <code>].");
                return 2;
        }
    }

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
            ? $"Demo tenant '{DemoData.Slug}' seeded in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: {result.Companies} companies, {result.Branches} branches, {result.Users} users, {result.Rates} exchange rates, {result.Journals} journals posted ({result.Entries} ledger entries), {result.PeriodsClosed} period closings; the invariant harness passed."
            : $"Demo tenant '{DemoData.Slug}' already exists; run the 'demo' command to rebuild it.");
        Console.WriteLine($"Sign in as {DemoData.Owner.Email} with password {DemoData.Password} (every demo user shares it).");
        return 0;
    }
}

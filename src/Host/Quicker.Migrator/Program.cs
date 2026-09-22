using DbUp.Engine.Output;
using Microsoft.Extensions.Configuration;
using Quicker.Migrator;

// Usage: quicker-migrate [migrate|seed|status|all]  (default: all)
// Configuration: QUICKER__DB__OWNERCONNECTION, QUICKER__DB__APPPASSWORD (env) or appsettings.json.
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var ownerConnection = configuration["QUICKER:DB:OWNERCONNECTION"] ?? configuration["Quicker:Db:OwnerConnection"]
    ?? "Host=localhost;Port=5432;Database=quicker;Username=quicker_owner;Password=quicker";
var appPassword = configuration["QUICKER:DB:APPPASSWORD"] ?? configuration["Quicker:Db:AppPassword"] ?? "quicker";
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
        await migrator.EnsureDatabaseAndRolesAsync(appPassword);
        var migrated = migrator.Migrate();
        if (!migrated.Successful)
        {
            return Report(migrated);
        }

        return Report(migrator.Seed());

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use migrate, seed, status or all.");
        return 2;
}

static int Report(DbUp.Engine.DatabaseUpgradeResult result)
{
    if (result.Successful)
    {
        Console.WriteLine($"OK: {result.Scripts.Count()} script(s) executed.");
        return 0;
    }

    Console.Error.WriteLine($"FAILED at {result.ErrorScript?.Name}: {result.Error}");
    return 1;
}

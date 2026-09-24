using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Quicker.Migrator.Maintenance;

/// <summary>
/// Finds and runs the PostgreSQL client programs (pg_dump, pg_restore) for a server. A client older than the server
/// refuses to dump it, so the tools are looked up for the server's major version: an explicit directory first
/// (<c>--pg-bin</c> or <c>Quicker:Backup:PgBin</c>), then the Debian/Ubuntu layout <c>/usr/lib/postgresql/N/bin</c>,
/// then the PATH. The password travels in the child's environment, never on its command line.
/// </summary>
public sealed partial class PgTools
{
    private PgTools(string directory, int majorVersion)
    {
        Directory = directory;
        MajorVersion = majorVersion;
    }

    /// <summary>Where pg_dump and pg_restore were found; empty when they come from the PATH.</summary>
    public string Directory { get; }

    public int MajorVersion { get; }

    public static async Task<PgTools> LocateAsync(int serverMajorVersion, string? explicitDirectory, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            candidates.Add(explicitDirectory);
        }
        else
        {
            var debian = $"/usr/lib/postgresql/{serverMajorVersion.ToString(CultureInfo.InvariantCulture)}/bin";
            if (File.Exists(Path.Combine(debian, "pg_dump")))
            {
                candidates.Add(debian);
            }

            candidates.Add(string.Empty);
        }

        string? lastProblem = null;
        foreach (var directory in candidates)
        {
            int? major;
            try
            {
                major = await ClientMajorAsync(Program(directory, "pg_dump"), cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                lastProblem = directory.Length == 0 ? "pg_dump is not on the PATH" : $"there is no pg_dump in {directory}";
                continue;
            }

            if (major is null)
            {
                lastProblem = $"the version of {Program(directory, "pg_dump")} could not be read";
                continue;
            }

            if (major < serverMajorVersion)
            {
                lastProblem = $"{Program(directory, "pg_dump")} is version {major.Value.ToString(CultureInfo.InvariantCulture)}, older than the server";
                continue;
            }

            return new PgTools(directory, major.Value);
        }

        throw new InvalidOperationException(
            $"No PostgreSQL {serverMajorVersion.ToString(CultureInfo.InvariantCulture)} client tools: {lastProblem}. Install the PostgreSQL {serverMajorVersion.ToString(CultureInfo.InvariantCulture)} client (pg_dump, pg_restore) or point --pg-bin at its bin directory.");
    }

    public static async Task<int> ServerMajorVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Runs a client program against the database of the connection string; stderr is returned for the report.</summary>
    public async Task<(int ExitCode, string Errors)> RunAsync(string program, string connectionString, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(Program(Directory, program))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--host=" + connection.Host);
        start.ArgumentList.Add("--port=" + connection.Port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--username=" + connection.Username);
        start.ArgumentList.Add("--no-password");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;
        start.Environment["PGSSLMODE"] = connection.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer",
        };
        start.Environment["PGAPPNAME"] = "quicker-migrator";

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, ((await errors) + (await output)).Trim());
    }

    private static string Program(string directory, string name) => directory.Length == 0 ? name : Path.Combine(directory, name);

    private static async Task<int?> ClientMajorAsync(string program, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(start)!;
        var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var match = VersionPattern().Match(text);
        return match.Success ? int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"\(PostgreSQL\)\s+(?<major>\d+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 100)]
    private static partial Regex VersionPattern();
}

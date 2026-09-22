using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using DbUp.Engine.Output;
using Npgsql;
using Quicker.Migrator;
using Testcontainers.PostgreSql;

namespace Quicker.Testing;

/// <summary>
/// A real PostgreSQL for tests (ADR-0029): migrations run once per process into a template database, and each
/// test class gets its own database cloned from the template in milliseconds.
/// Uses QUICKER_TEST_CONNECTION (an owner-role connection to any database on the cluster) when set; otherwise starts
/// a postgres:17 container through Testcontainers.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private static readonly SemaphoreSlim TemplateLock = new(1, 1);
    private static string? _templateName;
    private static string? _ownerBase;
    private static PostgreSqlContainer? _container;

    private TestDatabase(string databaseName, string ownerConnection, string appConnection)
    {
        Name = databaseName;
        OwnerConnectionString = ownerConnection;
        AppConnectionString = appConnection;
    }

    public string Name { get; }

    /// <summary>Schema owner connection: for arranging scratch objects and for maintenance-mode assertions.</summary>
    public string OwnerConnectionString { get; }

    /// <summary>Application role connection: what the product itself uses; subject to RLS.</summary>
    public string AppConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var ownerBase = await EnsureTemplateAsync(cancellationToken);
        var name = "quicker_test_" + Guid.NewGuid().ToString("N")[..12];

        await using (var admin = new NpgsqlConnection(ownerBase))
        {
            await admin.OpenAsync(cancellationToken);
            await admin.ExecuteAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{_templateName}\"");
        }

        var owner = new NpgsqlConnectionStringBuilder(ownerBase) { Database = name, Pooling = false }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(ownerBase)
        {
            Database = name,
            Username = "quicker_app",
            Password = "quicker",
            Pooling = false,
        }.ConnectionString;

        return new TestDatabase(name, owner, app);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownerBase is null)
        {
            return;
        }

        await using var admin = new NpgsqlConnection(_ownerBase);
        await admin.OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await admin.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @n AND pid <> pg_backend_pid()", new { n = Name });
                await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
                return;
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.ObjectInUse, StringComparison.Ordinal) && attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private static async Task<string> EnsureTemplateAsync(CancellationToken cancellationToken)
    {
        await TemplateLock.WaitAsync(cancellationToken);
        try
        {
            if (_ownerBase is not null)
            {
                return _ownerBase;
            }

            var ownerBase = Environment.GetEnvironmentVariable("QUICKER_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(ownerBase))
            {
                ownerBase = await StartContainerAsync(cancellationToken);
            }

            var templateName = "quicker_tmpl_" + ScriptsFingerprint();
            await using (var admin = new NpgsqlConnection(ownerBase))
            {
                await admin.OpenAsync(cancellationToken);
                var exists = await admin.ExecuteScalarAsync<int?>("SELECT 1 FROM pg_database WHERE datname = @n", new { n = templateName });
                if (exists is null)
                {
                    await admin.ExecuteAsync($"CREATE DATABASE \"{templateName}\"");
                    var templateOwner = new NpgsqlConnectionStringBuilder(ownerBase) { Database = templateName, Pooling = false }.ConnectionString;
                    var runner = new MigrationRunner(templateOwner, new NoOpUpgradeLog());
                    await runner.EnsureDatabaseAndRolesAsync("quicker", cancellationToken);
                    var migrated = runner.Migrate();
                    if (!migrated.Successful)
                    {
                        throw new InvalidOperationException($"Template migration failed at {migrated.ErrorScript?.Name}: {migrated.Error}");
                    }

                    var seeded = runner.Seed();
                    if (!seeded.Successful)
                    {
                        throw new InvalidOperationException($"Template seed failed at {seeded.ErrorScript?.Name}: {seeded.Error}");
                    }

                    await admin.ExecuteAsync($"UPDATE pg_database SET datistemplate = true WHERE datname = @n", new { n = templateName });
                }
            }

            _templateName = templateName;
            _ownerBase = ownerBase;
            return ownerBase;
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    private static async Task<string> StartContainerAsync(CancellationToken cancellationToken)
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithDatabase("postgres")
            .Build();
        await _container.StartAsync(cancellationToken);

        await using var admin = new NpgsqlConnection(_container.GetConnectionString());
        await admin.OpenAsync(cancellationToken);
        await admin.ExecuteAsync("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'quicker_owner') THEN
                CREATE ROLE quicker_owner LOGIN PASSWORD 'quicker' CREATEDB CREATEROLE;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'quicker_app') THEN
                CREATE ROLE quicker_app LOGIN PASSWORD 'quicker' NOSUPERUSER NOBYPASSRLS;
              END IF;
            END $$;
            """);

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = "quicker_owner",
            Password = "quicker",
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>Template name changes whenever any migration, repeatable or seed script changes.</summary>
    private static string ScriptsFingerprint()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        using var sha = SHA256.Create();
        foreach (var name in assembly.GetManifestResourceNames().Where(static n => n.EndsWith(".sql", StringComparison.Ordinal)).OrderBy(static n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var bytes = Encoding.UTF8.GetBytes(name + "\n" + reader.ReadToEnd());
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..16].ToLower(CultureInfo.InvariantCulture);
    }

    private sealed class NoOpUpgradeLog : IUpgradeLog
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

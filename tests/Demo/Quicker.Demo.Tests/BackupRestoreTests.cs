using Dapper;
using Npgsql;
using Quicker.Integrity.Contracts;
using Quicker.Migrator.Demo;
using Quicker.Migrator.Maintenance;
using Quicker.Testing;

namespace Quicker.Demo.Tests;

/// <summary>The demo tenant seeded once and backed up once, for the restore drill.</summary>
public sealed class BackedUpDemoFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "quicker-backup-tests", Guid.NewGuid().ToString("N"));

    public BackupResult Backup { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Db = await TestDatabase.CreateAsync();
        await DemoSeeder.SeedAsync(Db.OwnerConnectionString, Db.AppConnectionString, reseed: false);
        Backup = await DatabaseBackup.RunAsync(Db.OwnerConnectionString, Path.Combine(Directory, "demo.dump"), pgBin: null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>
/// Roadmap 10.3 acceptance: a backup of the demo tenant restores into a new database that holds every row the
/// backup counted, and the invariant harness passes on it; the same harness fails the restored books once a posted
/// line is taken away, so the drill's verdict is real. A restore refuses a changed file and an existing database,
/// and a backup never overwrites another.
/// </summary>
public sealed class BackupRestoreTests(BackedUpDemoFixture fixture) : IClassFixture<BackedUpDemoFixture>
{
    [Fact]
    public async Task The_demo_tenant_restores_with_every_row_and_passes_the_harness_which_catches_a_missing_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifest = fixture.Backup.Manifest;

        // The manifest counts what the source holds, table by table.
        await using (var source = new NpgsqlConnection(fixture.Db.OwnerConnectionString))
        {
            await source.OpenAsync(ct);
            foreach (var table in new[] { "app.gl_journal_lines", "app.inv_stock_ledger_entries", "app.ap_open_items", "app.aud_events", "control.tenants" })
            {
                manifest.Tables[table].ShouldBe(await source.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}"), table);
            }
        }

        manifest.Tables["app.gl_journal_lines"].ShouldBeGreaterThan(10_000);
        manifest.OwnerRole.ShouldBe("quicker_owner");
        manifest.LastMigration.ShouldStartWith("migrations.V");
        manifest.Sha256.ShouldBe(await BackupManifest.HashAsync(fixture.Backup.File, ct));

        var target = "quicker_restore_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var restore = await DatabaseRestore.RunAsync(fixture.Db.OwnerConnectionString, "quicker", null, fixture.Backup.File, target, pgBin: null, ct);

            restore.Mismatches.ShouldBeEmpty();
            restore.Verification.ShouldNotBeNull();
            var demo = restore.Verification.Tenants.ShouldHaveSingleItem();
            demo.Slug.ShouldBe(DemoData.Slug);
            demo.Report.Checks.Select(static c => c.Code).ShouldBe(InvariantCodes.All);
            demo.Report.FailedCodes.ShouldBeEmpty();
            restore.Passed.ShouldBeTrue();

            // Take one posted line away from the restored books (the append-only guard yields only to maintenance).
            var restoredOwner = new NpgsqlConnectionStringBuilder(fixture.Db.OwnerConnectionString) { Database = target }.ConnectionString;
            await using (var restored = new NpgsqlConnection(restoredOwner))
            {
                await restored.OpenAsync(ct);
                await restored.ExecuteAsync("SET app.maintenance = 'on'");
                (await restored.ExecuteAsync("DELETE FROM app.gl_journal_lines WHERE id = (SELECT id FROM app.gl_journal_lines ORDER BY id LIMIT 1)")).ShouldBe(1);
            }

            var restoredApp = new NpgsqlConnectionStringBuilder(restoredOwner) { Username = "quicker_app", Password = "quicker" }.ConnectionString;
            var after = await VerifyBooks.RunAsync(restoredOwner, restoredApp, null, DemoData.Slug, ct);
            after.Passed.ShouldBeFalse();
            after.Tenants.ShouldHaveSingleItem().Report.FailedCodes.ShouldContain(InvariantCodes.EntriesBalanced);
        }
        finally
        {
            await DropAsync(target);
        }
    }

    [Fact]
    public async Task A_restore_refuses_a_changed_file_and_an_existing_database_and_a_backup_never_overwrites()
    {
        var ct = TestContext.Current.CancellationToken;

        var existing = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRestore.RunAsync(fixture.Db.OwnerConnectionString, "quicker", null, fixture.Backup.File, fixture.Db.Name, pgBin: null, ct));
        existing.Message.ShouldContain("already exists");

        var overwrite = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseBackup.RunAsync(fixture.Db.OwnerConnectionString, fixture.Backup.File, pgBin: null, ct));
        overwrite.Message.ShouldContain("never overwrites");

        // A copy with one byte changed, next to a copy of the manifest.
        var changed = Path.Combine(fixture.Directory, "changed.dump");
        var bytes = await File.ReadAllBytesAsync(fixture.Backup.File, ct);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(changed, bytes, ct);
        File.Copy(BackupManifest.PathFor(fixture.Backup.File), BackupManifest.PathFor(changed));
        var target = "quicker_restore_" + Guid.NewGuid().ToString("N")[..12];
        var tampered = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRestore.RunAsync(fixture.Db.OwnerConnectionString, "quicker", null, changed, target, pgBin: null, ct));
        tampered.Message.ShouldContain("does not match its manifest");

        var unnamed = await Should.ThrowAsync<InvalidOperationException>(() =>
            DatabaseRestore.RunAsync(fixture.Db.OwnerConnectionString, "quicker", null, fixture.Backup.File, "Robert'); DROP TABLE x;--", pgBin: null, ct));
        unnamed.Message.ShouldContain("--database takes a new database name");

        // Neither refusal created a database.
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.Db.OwnerConnectionString) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync(ct);
        (await admin.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_database WHERE datname = @target", new { target })).ShouldBe(0);
    }

    private async Task DropAsync(string database)
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.Db.OwnerConnectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
    }
}

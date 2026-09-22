using Quicker.Testing;

namespace Quicker.Persistence.Tests;

/// <summary>One migrated database per test class.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}

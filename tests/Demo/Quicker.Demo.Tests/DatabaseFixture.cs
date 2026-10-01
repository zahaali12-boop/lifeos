using Quicker.Testing;

namespace Quicker.Demo.Tests;

/// <summary>One migrated database for the seeder suite; the tests run in order inside the class.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }
    }
}

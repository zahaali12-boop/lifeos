using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

/// <summary>One API host and database shared by the identity test classes; each test creates its own tenant.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    // A proxy network is trusted so a forwarded client address can be tested; requests without the test address are unaffected.
    public async ValueTask InitializeAsync() => Api = await ApiFixture.StartAsync(static b => b.UseSetting("Quicker:Api:TrustedProxies", "192.0.2.0/24"));

    public async ValueTask DisposeAsync() => await Api.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "identity-api";
}

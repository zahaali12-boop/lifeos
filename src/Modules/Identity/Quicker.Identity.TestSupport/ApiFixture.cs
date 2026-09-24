using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Testing;

namespace Quicker.Identity.TestSupport;

/// <summary>
/// Boots the real API host against a migrated database of its own, with a controllable clock and a capturing
/// email sender. One fixture per test class; tests create their own tenants through the public API.
/// </summary>
public sealed class ApiFixture : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WebApplicationFactory<Program>? _factory;

    public TestDatabase Db { get; private set; } = null!;

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero));

    public CapturingEmailSender Emails { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Directory of this host's audit anchor file store (one per fixture, removed on dispose).</summary>
    public string AnchorDirectory { get; } = Path.Combine(Path.GetTempPath(), "quicker-tests", "anchors-" + Guid.NewGuid().ToString("N")[..12]);

    public IServiceProvider Services => _factory!.Services;

    /// <param name="configure">Extra host settings a module's tests need (provider URLs, feature options).</param>
    public static async Task<ApiFixture> StartAsync(Action<IWebHostBuilder>? configure = null) => StartOn(await TestDatabase.CreateAsync(), configure);

    /// <summary>Boots the API against a migrated database the caller prepared (a copy of the seeded demo, say); the fixture owns it from then on and drops it on dispose.</summary>
    public static ApiFixture StartOn(TestDatabase database, Action<IWebHostBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var fixture = new ApiFixture();
        fixture.Db = database;
        fixture._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Quicker:Api:RateLimit:Enabled", "false"); // budgets are exercised by their own tests; suites hammer the API from one address
            builder.UseSetting("Quicker:Outbound:AllowedPrivateNetworks", "127.0.0.0/8"); // local webhook receivers and identity providers run on loopback
            configure?.Invoke(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("Quicker:Db:AppConnection", fixture.Db.AppConnectionString);
            builder.UseSetting("Quicker:Db:OwnerConnection", fixture.Db.OwnerConnectionString);
            builder.UseSetting("Quicker:Auth:PublicOrigin", "http://localhost");
            builder.UseSetting("Quicker:Auth:ApiOrigin", "http://localhost");
            builder.UseSetting("Quicker:Audit:Anchoring:Path", fixture.AnchorDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IClock>(fixture.Clock);
                services.AddSingleton<IStartupFilter, ClientAddressFilter>();
            });
        });
        fixture.Client = fixture._factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        fixture.Emails = fixture._factory.Services.GetRequiredService<CapturingEmailSender>();
        return fixture;
    }

    public HttpClient ClientFor(string accessToken)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>A client whose requests come from the given network address (the test host has no real connection).</summary>
    public HttpClient ClientFrom(string address, string? accessToken = null)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ClientAddressFilter.Header, address);
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public HttpClient ClientForApiKey(string apiKey)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    /// <summary>Creates a tenant with an owner through the public sign-up endpoint.</summary>
    public async Task<Workspace> SignupAsync(string? slug = null, string password = "correct-horse-battery-staple")
    {
        slug ??= "ws" + Guid.NewGuid().ToString("N")[^10..];
        var email = $"owner-{slug}@example.test";
        var response = await Client.PostAsJsonAsync("/api/v1/auth/signup", new { tenantName = "Workspace " + slug, slug, ownerEmail = email, ownerName = "Owner " + slug, password }, Json);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Sign-up failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new Workspace(slug, email, password, root.GetProperty("accessToken").GetString()!, root.GetProperty("refreshToken").GetString()!,
            root.GetProperty("tenant").GetProperty("id").GetGuid(), root.GetProperty("membershipId").GetGuid(), root.GetProperty("user").GetProperty("id").GetGuid());
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Db is not null)
        {
            await Db.DisposeAsync();
        }

        try
        {
            Directory.Delete(AnchorDirectory, recursive: true);
        }
        catch (IOException)
        {
            // best effort: temp directory
        }
    }
}

public sealed record Workspace(string Slug, string OwnerEmail, string OwnerPassword, string AccessToken, string RefreshToken, Guid TenantId, Guid MembershipId, Guid UserId);

public static class HttpAssertions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var json = await response.ReadJsonAsync();
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

/// <summary>
/// The in-memory test host gives requests no remote address; this sets it from a test header, first in the pipeline
/// (before forwarded headers), so network rules can be exercised as a real connection would.
/// </summary>
internal sealed class ClientAddressFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(static (context, following) =>
        {
            if (System.Net.IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return following(context);
        });
        next(app);
    };
}

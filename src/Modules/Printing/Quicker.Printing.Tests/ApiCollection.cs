using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;

namespace Quicker.Printing.Tests;

/// <summary>One API host with filesystem object storage in a temporary directory, shared by the printing test classes; each test creates its own tenant.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "quicker-tests", "print-" + Guid.NewGuid().ToString("N")[..12]);

    public async ValueTask InitializeAsync()
    {
        Api = await ApiFixture.StartAsync(builder =>
        {
            builder.UseSetting("Quicker:Storage:Provider", "filesystem");
            builder.UseSetting("Quicker:Storage:Path", StorageRoot);
        });
    }

    /// <summary>Drains the outbox and then the job queue the way the worker would.</summary>
    public async Task RunWorkerAsync()
    {
        var dispatcher = Api.Services.GetRequiredService<OutboxDispatcher>();
        var runner = Api.Services.GetRequiredService<JobRunner>();
        while (await dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        while (await runner.RunOneAsync(CancellationToken.None))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        try
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "printing-api";
}

internal static class PrintingApi
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    public static async Task<JsonElement> PostAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    public static async Task<JsonElement> PutAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    public static async Task<JsonElement> GetOkAsync(this HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
        return json;
    }

    public static async Task<string> GetTextAsync(this HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(expected, text);
        return text;
    }

    public static async Task<(string Code, JsonElement Problem)> PostErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    public static async Task<(string Code, JsonElement Problem)> GetErrorAsync(this HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    /// <summary>The invariant harness after a scenario (ADR-0029).</summary>
    public static async Task AssertInvariantsAsync(this HttpClient client)
    {
        var report = await client.PostAsync("/api/v1/platform/integrity/run", new { }, HttpStatusCode.OK);
        report.GetProperty("passed").GetBoolean().ShouldBeTrue("invariants violated: " + string.Join(" | ", report.GetProperty("checks").EnumerateArray().Where(static c => !c.GetProperty("passed").GetBoolean()).Select(static c => c.GetProperty("code").GetString() + ": " + string.Join("; ", c.GetProperty("problems").EnumerateArray().Select(static p => p.GetString())))));
    }
}

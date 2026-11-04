using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;

namespace Quicker.Reporting.Tests;

/// <summary>One API host and database shared by the reporting test classes; each test creates its own tenant.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Api = await ApiFixture.StartAsync();

    /// <summary>Drains the outbox and runs every queued job in the test host, as the worker would.</summary>
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
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "reporting-api";
}

internal static class ReportingApi
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
        var response = await client.GetAsync(path);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
        return json;
    }

    public static async Task<HttpStatusCode> GetStatusAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return response.StatusCode;
    }

    public static async Task DeleteAsync(this HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.DeleteAsync(path);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
    }

    public static async Task<(string Code, JsonElement Problem)> PutErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
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

    public static async Task<(string Code, JsonElement Problem)> PostErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;

namespace Quicker.Inventory.Tests;

/// <summary>One API host and database shared by the inventory test classes; each test creates its own tenant.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    /// <summary>A re-application walking more than this many entries continues in a background job; low here so the tests exercise both paths.</summary>
    public const int RecostThreshold = 60;

    public async ValueTask InitializeAsync() => Api = await ApiFixture.StartAsync(static builder => builder.UseSetting("Quicker:Inventory:RecostThreshold", RecostThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Drains the outbox and runs every queued job in process (the worker's loop).</summary>
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

    /// <summary>
    /// Stock the way a receipt will bring it (M4) or an opening adjustment (3.4): straight through the stock posting
    /// engine as the system actor, in its own committed unit of work.
    /// </summary>
    public async Task<StockPostingResult> PostStockAsync(Guid tenantId, StockPostingRequest request)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.System(new TenantId(tenantId), "test-stock-" + Guid.CreateVersion7().ToString("N")[^12..]);
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        var result = await services.GetRequiredService<IInventoryPosting>().PostAsync(request, TestContext.Current.CancellationToken);
        if (result.IsFailure)
        {
            await unitOfWork.RollbackAsync(TestContext.Current.CancellationToken);
            throw new InvalidOperationException($"stock posting failed: {result.Error!.Code} {result.Error.Message}");
        }

        await unitOfWork.CommitAsync(TestContext.Current.CancellationToken);
        return result.Value;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "inventory-api";
}

internal static class InventoryApi
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

    public static async Task<(string Code, JsonElement Problem)> PostErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    public static async Task<(string Code, JsonElement Problem)> PutErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    public static decimal Dec(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    /// <summary>The invariant harness after a scenario (ADR-0029): balances equal the ledger, the books, the chain and isolation hold.</summary>
    public static async Task AssertInvariantsAsync(this HttpClient client)
    {
        var report = await client.PostAsync("/api/v1/platform/integrity/run", new { }, HttpStatusCode.OK);
        report.GetProperty("passed").GetBoolean().ShouldBeTrue("invariants violated: " + string.Join(" | ", report.GetProperty("checks").EnumerateArray().Where(static c => !c.GetProperty("passed").GetBoolean()).Select(static c => c.GetProperty("code").GetString() + ": " + string.Join("; ", c.GetProperty("problems").EnumerateArray().Select(static p => p.GetString())))));
    }
}

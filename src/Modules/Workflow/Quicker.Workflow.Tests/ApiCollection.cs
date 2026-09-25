using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Workflow.Tests;

/// <summary>One API host and database shared by the workflow test classes; each test creates its own tenant. A test subject provider stands in for a module's document.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Api = await ApiFixture.StartAsync(static builder => builder.ConfigureServices(static services => services.AddScoped<IWorkflowSubjectProvider, TestDocumentProvider>()));

    /// <summary>Runs work against the engine in process as the system actor, the way a module does inside its own unit of work.</summary>
    public async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var requestId = "test-wf-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = TenantContext.System(new TenantId(tenantId), requestId);
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        var result = await work(services, TestContext.Current.CancellationToken);
        await unitOfWork.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    /// <summary>Enqueues a platform job (no tenant), the way the scheduler does.</summary>
    public async Task EnqueueAsync(string jobType, object? payload)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.Anonymous("test-job-" + Guid.CreateVersion7().ToString("N")[^12..]);
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        await services.GetRequiredService<IJobQueue>().EnqueueAsync(new JobRequest(jobType, payload), TestContext.Current.CancellationToken);
        await unitOfWork.CommitAsync(TestContext.Current.CancellationToken);
    }

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
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "workflow-api";
}

internal static class WorkflowApi
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

    public static async Task<(string Code, JsonElement Problem)> GetErrorAsync(this HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    /// <summary>The one element of a JSON array.</summary>
    public static JsonElement Only(this JsonElement array)
    {
        array.ValueKind.ShouldBe(JsonValueKind.Array, array.ToString());
        array.GetArrayLength().ShouldBe(1, array.ToString());
        return array[0];
    }

    public static void ShouldBeEmptyArray(this JsonElement array)
    {
        array.ValueKind.ShouldBe(JsonValueKind.Array, array.ToString());
        array.GetArrayLength().ShouldBe(0, array.ToString());
    }

    /// <summary>The invariant harness after a scenario (ADR-0029).</summary>
    public static async Task AssertInvariantsAsync(this HttpClient client)
    {
        var report = await client.PostAsync("/api/v1/platform/integrity/run", new { }, HttpStatusCode.OK);
        report.GetProperty("passed").GetBoolean().ShouldBeTrue("invariants violated: " + string.Join(" | ", report.GetProperty("checks").EnumerateArray().Where(static c => !c.GetProperty("passed").GetBoolean()).Select(static c => c.GetProperty("code").GetString() + ": " + string.Join("; ", c.GetProperty("problems").EnumerateArray().Select(static p => p.GetString())))));
    }
}

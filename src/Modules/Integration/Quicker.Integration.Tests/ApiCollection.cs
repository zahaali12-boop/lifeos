using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Events;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;

namespace Quicker.Integration.Tests;

/// <summary>One API host, database and local webhook receiver shared by the integration test classes.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public WebhookReceiver Receiver { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Receiver = WebhookReceiver.Start();
        Api = await ApiFixture.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        Receiver?.Dispose();
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

    public async Task PublishAsync<TEvent>(Guid tenantId, TEvent integrationEvent) where TEvent : IIntegrationEvent
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(TenantContext.System(new TenantId(tenantId), "test-publish"));
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        await scope.ServiceProvider.GetRequiredService<IOutbox>().PublishAsync(integrationEvent);
        await uow.CommitAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "integration-api";
}

public sealed record OrderShipped(Guid AggregateId, string OrderNumber, decimal Quantity) : IIntegrationEvent
{
    public static string EventType => "test.order.shipped";

    public static int EventVersion => 1;

    public string AggregateType => "sales_order";
}

public sealed record ReceivedWebhook(string Path, IReadOnlyDictionary<string, string> Headers, string Body, DateTimeOffset At);

/// <summary>A local HTTP endpoint that records every webhook it receives and answers with the configured status.</summary>
public sealed class WebhookReceiver : IDisposable
{
    private readonly HttpListener _listener = new();
    private Task? _serving;

    public string BaseUrl { get; private set; } = string.Empty;

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    public int ResponseStatus { get; set; } = 200;

    public static WebhookReceiver Start()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var receiver = new WebhookReceiver { BaseUrl = $"http://127.0.0.1:{port}/" };
        receiver._listener.Prefixes.Add(receiver.BaseUrl);
        receiver._listener.Start();
        receiver._serving = Task.Run(receiver.ServeAsync);
        return receiver;
    }

    public async Task<ReceivedWebhook> WaitForAsync(Func<ReceivedWebhook, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = Received.FirstOrDefault(match);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No matching webhook arrived.");
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.AllKeys.Where(static k => k is not null).ToDictionary(static k => k!, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
            Received.Enqueue(new ReceivedWebhook(context.Request.Url?.AbsolutePath ?? "/", headers, body, DateTimeOffset.UtcNow));
            context.Response.StatusCode = ResponseStatus;
            var bytes = Encoding.UTF8.GetBytes(ResponseStatus < 300 ? "{\"ok\":true}" : "{\"error\":\"receiver says no\"}");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _serving?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}

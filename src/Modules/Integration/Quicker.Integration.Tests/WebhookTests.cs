using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Integration.Application;

namespace Quicker.Integration.Tests;

/// <summary>ADR-0012 webhooks: fan-out from the outbox, HMAC-signed delivery, retries with backoff, delivery log, replay, test delivery, isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class WebhookTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private async Task<(Guid Id, string Secret)> SubscribeAsync(HttpClient owner, string name, string path, params string[] eventTypes)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name, url = host.Receiver.BaseUrl + path, eventTypes }, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, json.ToString());
        return (json.GetProperty("subscription").GetProperty("id").GetGuid(), json.GetProperty("secret").GetString()!);
    }

    [Fact]
    public async Task Events_fan_out_to_matching_subscriptions_signed_with_the_secret_shown_once()
    {
        host.Receiver.ResponseStatus = 200;
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (salesId, salesSecret) = await SubscribeAsync(owner, "sales-hook", "sales", "test.order.*");
        var (allId, _) = await SubscribeAsync(owner, "everything", "all", "*");
        var (otherId, _) = await SubscribeAsync(owner, "purchasing", "purchasing", "test.purchase.received");
        salesSecret.ShouldStartWith("whsec_");
        (await (await owner.GetAsync($"/api/v1/integration/webhooks/{salesId}")).ReadJsonAsync()).TryGetProperty("secret", out _).ShouldBeFalse("the secret is never shown again");

        var orderId = Guid.NewGuid();
        await host.PublishAsync(ws.TenantId, new OrderShipped(orderId, "SO-1001", 3m));
        await host.RunWorkerAsync();

        var sales = await host.Receiver.WaitForAsync(r => r.Path == "/sales", TimeSpan.FromSeconds(10));
        var all = await host.Receiver.WaitForAsync(r => r.Path == "/all", TimeSpan.FromSeconds(10));
        host.Receiver.Received.ShouldNotContain(r => r.Path == "/purchasing");

        using var envelope = JsonDocument.Parse(sales.Body);
        envelope.RootElement.GetProperty("type").GetString().ShouldBe("test.order.shipped");
        envelope.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        envelope.RootElement.GetProperty("aggregateId").GetGuid().ShouldBe(orderId);
        envelope.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(ws.TenantId);
        envelope.RootElement.GetProperty("data").GetProperty("orderNumber").GetString().ShouldBe("SO-1001");
        envelope.RootElement.GetProperty("data").GetProperty("quantity").GetDecimal().ShouldBe(3m);
        sales.Headers[WebhookSigning.EventTypeHeader].ShouldBe("test.order.shipped");
        sales.Headers[WebhookSigning.AttemptHeader].ShouldBe("1");
        sales.Headers["User-Agent"].ShouldStartWith("Quicker-Webhooks");
        WebhookSigning.Verify(salesSecret, sales.Headers[WebhookSigning.TimestampHeader], sales.Body, sales.Headers[WebhookSigning.SignatureHeader]).ShouldBeTrue();
        WebhookSigning.Verify("whsec_wrong", sales.Headers[WebhookSigning.TimestampHeader], sales.Body, sales.Headers[WebhookSigning.SignatureHeader]).ShouldBeFalse();
        all.Headers[WebhookSigning.EventIdHeader].ShouldBe(sales.Headers[WebhookSigning.EventIdHeader]);

        var deliveries = (await (await owner.GetAsync($"/api/v1/integration/webhooks/{salesId}/deliveries")).ReadJsonAsync()).EnumerateArray().ToList();
        var delivery = deliveries.ShouldHaveSingleItem();
        delivery.GetProperty("status").GetString().ShouldBe("delivered");
        delivery.GetProperty("responseStatus").GetInt32().ShouldBe(200);
        delivery.GetProperty("attempt").GetInt32().ShouldBe(1);
        delivery.GetProperty("responseExcerpt").GetString()!.ShouldContain("ok");
        (await (await owner.GetAsync($"/api/v1/integration/webhooks/{otherId}/deliveries")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await (await owner.GetAsync($"/api/v1/integration/webhooks/{allId}/deliveries")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);

        // Another tenant sees nothing, and an inactive subscription receives nothing new.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await (await outsider.GetAsync("/api/v1/integration/webhooks")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await outsider.GetAsync($"/api/v1/integration/webhooks/{salesId}/deliveries")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.PutAsJsonAsync($"/api/v1/integration/webhooks/{allId}", new { name = "everything", url = host.Receiver.BaseUrl + "all", eventTypes = new[] { "*" }, active = false }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.PublishAsync(ws.TenantId, new OrderShipped(Guid.NewGuid(), "SO-1002", 1m));
        await host.RunWorkerAsync();
        (await (await owner.GetAsync($"/api/v1/integration/webhooks/{allId}/deliveries")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await (await owner.GetAsync($"/api/v1/integration/webhooks/{salesId}/deliveries")).ReadJsonAsync()).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Failed_deliveries_retry_with_backoff_keep_their_log_and_can_be_replayed_and_tested()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (id, secret) = await SubscribeAsync(owner, "flaky", "flaky", "test.order.shipped");

        host.Receiver.ResponseStatus = 500;
        await host.PublishAsync(ws.TenantId, new OrderShipped(Guid.NewGuid(), "SO-2001", 1m));
        await host.RunWorkerAsync();
        var failed = (await (await owner.GetAsync($"/api/v1/integration/webhooks/{id}/deliveries")).ReadJsonAsync()).EnumerateArray().Single();
        failed.GetProperty("status").GetString().ShouldBe("pending");
        failed.GetProperty("attempt").GetInt32().ShouldBe(1);
        failed.GetProperty("responseStatus").GetInt32().ShouldBe(500);
        failed.GetProperty("lastError").GetString().ShouldBe("HTTP 500");
        failed.GetProperty("nextAttemptAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        var jobId = failed.GetProperty("jobId").GetGuid();
        await using var owner2 = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        (await owner2.QuerySingleAsync<(string State, int Attempts)>("SELECT state, attempts FROM ops.jobs WHERE id = @id", new { id = jobId })).ShouldBe(("queued", 1));

        // The receiver recovers; the backed-off job runs again and the log shows the successful attempt.
        host.Receiver.ResponseStatus = 200;
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await host.RunWorkerAsync();
        var delivered = (await (await owner.GetAsync($"/api/v1/integration/webhooks/deliveries/{failed.GetProperty("id").GetGuid()}")).ReadJsonAsync());
        delivered.GetProperty("status").GetString().ShouldBe("delivered");
        delivered.GetProperty("attempt").GetInt32().ShouldBe(2);
        delivered.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        var second = await host.Receiver.WaitForAsync(r => r.Path == "/flaky" && r.Headers[WebhookSigning.AttemptHeader] == "2", TimeSpan.FromSeconds(10));
        WebhookSigning.Verify(secret, second.Headers[WebhookSigning.TimestampHeader], second.Body, second.Headers[WebhookSigning.SignatureHeader]).ShouldBeTrue();

        // Replay queues the same payload again; test sends a synthetic event.
        var replay = await owner.PostAsync($"/api/v1/integration/webhooks/deliveries/{failed.GetProperty("id").GetGuid()}/replay", null);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.RunWorkerAsync();
        host.Receiver.Received.Count(r => r.Path == "/flaky" && r.Body.Contains("SO-2001", StringComparison.Ordinal)).ShouldBe(3);

        var test = await owner.PostAsync($"/api/v1/integration/webhooks/{id}/test", null);
        test.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.RunWorkerAsync();
        var testHit = await host.Receiver.WaitForAsync(r => r.Path == "/flaky" && r.Headers[WebhookSigning.EventTypeHeader] == "webhook.test", TimeSpan.FromSeconds(10));
        testHit.Body.ShouldContain("Test delivery from Quicker");

        // Rotating the secret invalidates the old one for later deliveries.
        var rotated = await (await owner.PostAsync($"/api/v1/integration/webhooks/{id}/rotate-secret", null)).ReadJsonAsync();
        var newSecret = rotated.GetProperty("secret").GetString()!;
        newSecret.ShouldNotBe(secret);
        await owner.PostAsync($"/api/v1/integration/webhooks/{id}/test", null);
        await host.RunWorkerAsync();
        var afterRotation = host.Receiver.Received.Where(r => r.Path == "/flaky" && r.Headers[WebhookSigning.EventTypeHeader] == "webhook.test").Last();
        WebhookSigning.Verify(newSecret, afterRotation.Headers[WebhookSigning.TimestampHeader], afterRotation.Body, afterRotation.Headers[WebhookSigning.SignatureHeader]).ShouldBeTrue();
        WebhookSigning.Verify(secret, afterRotation.Headers[WebhookSigning.TimestampHeader], afterRotation.Body, afterRotation.Headers[WebhookSigning.SignatureHeader]).ShouldBeFalse();

        // Validation and audit.
        (await (await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name = "bad", url = "ftp://x", eventTypes = new[] { "*" } }, Json)).ErrorCodeAsync()).ShouldBe("webhook.url_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name = "bad", url = host.Receiver.BaseUrl, eventTypes = new[] { "Sales Invoice" } }, Json)).ErrorCodeAsync()).ShouldBe("webhook.event_types_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name = "flaky", url = host.Receiver.BaseUrl, eventTypes = new[] { "*" } }, Json)).ErrorCodeAsync()).ShouldBe("webhook.name_taken");
        var events = (await (await owner.GetAsync($"/api/v1/audit/records/webhook_subscription/{id}")).ReadJsonAsync()).EnumerateArray().ToList();
        events[0].GetProperty("action").GetString().ShouldBe("created");
        events[0].GetProperty("after").TryGetProperty("secretEnc", out _).ShouldBeFalse("secrets never enter the audit log");
        (await owner.DeleteAsync($"/api/v1/integration/webhooks/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetAsync($"/api/v1/integration/webhooks/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

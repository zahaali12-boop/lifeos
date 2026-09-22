using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Integration.Domain;
using Quicker.Integration.Persistence;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;

namespace Quicker.Integration.Application;

/// <summary>What a receiver gets: the event's identity and the module's payload, wrapped once so every webhook looks the same.</summary>
public sealed record WebhookEnvelope(Guid Id, string Type, int Version, DateTimeOffset OccurredAt, Guid? TenantId, string AggregateType, Guid AggregateId, JsonElement Data);

public static class WebhookJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>HMAC-SHA256 over "{timestamp}.{body}" with the subscription secret; the timestamp defeats replay (receivers reject old ones).</summary>
public static class WebhookSigning
{
    public const string SignatureHeader = "X-Quicker-Signature";
    public const string TimestampHeader = "X-Quicker-Timestamp";
    public const string EventIdHeader = "X-Quicker-Event-Id";
    public const string EventTypeHeader = "X-Quicker-Event-Type";
    public const string DeliveryHeader = "X-Quicker-Delivery";
    public const string AttemptHeader = "X-Quicker-Attempt";

    public static string Sign(string secret, string timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body));
        return "v1=" + Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, string timestamp, string body, string? signatureHeader) =>
        signatureHeader is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, timestamp, body)), Encoding.UTF8.GetBytes(signatureHeader));
}

public sealed record WebhookDeliveryPayload(Guid DeliveryId);

/// <summary>Sees every outbox message and creates a delivery per active matching subscription of the message's tenant.</summary>
public sealed class WebhookFanout(IntegrationDbContext db, WebhookService webhooks) : IIntegrationEventObserver
{
    public async Task ObserveAsync(OutboxMessage message, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is null)
        {
            return;
        }

        var subscriptions = (await db.Subscriptions.Where(static s => s.Active).ToListAsync(cancellationToken)).Where(s => s.Matches(message.EventType)).ToList();
        if (subscriptions.Count == 0)
        {
            return;
        }

        using var data = JsonDocument.Parse(message.Payload);
        var envelope = new WebhookEnvelope(message.Id, message.EventType, message.EventVersion, message.OccurredAt, message.TenantId, message.AggregateType, message.AggregateId, data.RootElement.Clone());
        foreach (var subscription in subscriptions)
        {
            await webhooks.EnqueueDeliveryAsync(subscription, envelope, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Posts one delivery. The attempt's outcome is recorded in its own transaction before a failure is thrown, so the
/// delivery log survives the job's rollback; the job's retries with backoff are the webhook's retries.
/// </summary>
public sealed class WebhookDeliveryJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, IUnitOfWorkAccessor unitOfWork, IHttpClientFactory httpClientFactory, ISecretProtector secrets, IClock clock) : IJobHandler<WebhookDeliveryPayload>
{
    public const string HttpClientName = "webhooks";

    public static string JobType => WebhookService.DeliveryJobType;

    public async Task<object?> ExecuteAsync(WebhookDeliveryPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        WebhookDelivery? delivery;
        WebhookSubscription? subscription;
        await using (var scope = scopeFactory.CreateAsyncScope())
        await using (var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken))
        {
            scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == payload.DeliveryId, cancellationToken);
            subscription = delivery is null ? null : await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == delivery.SubscriptionId, cancellationToken);
            await uow.RollbackAsync(cancellationToken);
        }

        if (delivery is null)
        {
            throw new JobFailedException($"Webhook delivery {payload.DeliveryId} no longer exists.");
        }

        if (subscription is null || !subscription.Active)
        {
            await RecordAsync(delivery.Id, context.Attempt, status: "failed", responseStatus: null, excerpt: null, error: "subscription inactive or removed", nextAttempt: null, cancellationToken);
            return new { delivered = false, reason = "inactive" };
        }

        var timestamp = clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var secret = secrets.UnprotectString(subscription.SecretEnc);
        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url);
        request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Quicker-Webhooks", "1"));
        request.Headers.TryAddWithoutValidation(WebhookSigning.EventIdHeader, delivery.EventId.ToString());
        request.Headers.TryAddWithoutValidation(WebhookSigning.EventTypeHeader, delivery.EventType);
        request.Headers.TryAddWithoutValidation(WebhookSigning.DeliveryHeader, delivery.Id.ToString());
        request.Headers.TryAddWithoutValidation(WebhookSigning.AttemptHeader, context.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSigning.TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(WebhookSigning.SignatureHeader, WebhookSigning.Sign(secret, timestamp, delivery.Payload));

        int? status = null;
        string? excerpt = null;
        string? error = null;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            excerpt = body.Length <= 500 ? body : body[..500];
            if (!response.IsSuccessStatusCode)
            {
                error = $"HTTP {status}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        var exhausted = context.Attempt >= WebhookService.DeliveryMaxAttempts;
        var outcome = error is null ? "delivered" : exhausted ? "dead" : "pending";
        await RecordAsync(delivery.Id, context.Attempt, outcome, status, excerpt, error, error is null || exhausted ? null : clock.UtcNow.Add(Backoff.For(context.Attempt)), cancellationToken);
        if (error is not null)
        {
            throw new InvalidOperationException($"Webhook {subscription.Name} attempt {context.Attempt}: {error}");
        }

        return new { delivered = true, status };
    }

    /// <summary>Records the attempt in a unit of work of its own under the job's tenant, so the log commits independently of the job's outcome.</summary>
    private async Task RecordAsync(Guid deliveryId, int attempt, string status, int? responseStatus, string? excerpt, string? error, DateTimeOffset? nextAttempt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == deliveryId, cancellationToken);
        delivery.Attempt = attempt;
        delivery.Status = status;
        delivery.ResponseStatus = responseStatus;
        delivery.ResponseExcerpt = excerpt;
        delivery.LastError = error;
        delivery.AttemptedAt = clock.UtcNow;
        delivery.NextAttemptAt = nextAttempt;
        delivery.DeliveredAt = status == "delivered" ? clock.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }
}

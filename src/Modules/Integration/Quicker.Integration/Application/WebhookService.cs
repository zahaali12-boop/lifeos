using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Integration.Domain;
using Quicker.Integration.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Integration.Application;

public sealed record SaveWebhookRequest(string Name, string Url, IReadOnlyList<string>? EventTypes = null, IReadOnlyDictionary<string, string>? Filters = null, bool Active = true);

public sealed record WebhookSummary(Guid Id, string Name, string Url, IReadOnlyList<string> EventTypes, IReadOnlyDictionary<string, string> Filters, bool Active, Guid? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Returned once, at creation and rotation: the secret is stored encrypted and never shown again.</summary>
public sealed record WebhookCreated(WebhookSummary Subscription, string Secret);

public sealed record DeliverySummary(Guid Id, Guid SubscriptionId, Guid EventId, string EventType, JsonElement Payload, int Attempt, string Status, int? ResponseStatus, string? ResponseExcerpt, string? LastError, Guid? JobId, DateTimeOffset CreatedAt, DateTimeOffset? AttemptedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeliveredAt);

/// <summary>Webhook subscriptions and their delivery log (ADR-0012).</summary>
public sealed class WebhookService(IntegrationDbContext db, IUnitOfWorkAccessor unitOfWork, ISecretProtector secrets, IJobQueue jobs, IClock clock)
{
    public const string DeliveryJobType = "integration.webhook.deliver";

    /// <summary>Retries with backoff (1 s → 1 h) for roughly a day.</summary>
    public const int DeliveryMaxAttempts = 30;

    public async Task<IReadOnlyList<WebhookSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Subscriptions.OrderBy(static s => s.Name).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<WebhookSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return subscription is null ? null : Map(subscription);
    }

    public async Task<Result<WebhookCreated>> CreateAsync(SaveWebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subscription = new WebhookSubscription { Id = Guid.CreateVersion7(), CreatedBy = unitOfWork.Current.Context.UserId?.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(subscription, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var secret = NewSecret();
        subscription.SecretEnc = secrets.ProtectString(secret);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookCreated(Map(subscription), secret);
    }

    public async Task<Result<WebhookSummary>> UpdateAsync(Guid id, SaveWebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return Error.NotFound("webhook", id);
        }

        var applied = await ApplyAsync(subscription, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        subscription.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(subscription);
    }

    public async Task<Result<WebhookCreated>> RotateSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return Error.NotFound("webhook", id);
        }

        var secret = NewSecret();
        subscription.SecretEnc = secrets.ProtectString(secret);
        subscription.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookCreated(Map(subscription), secret);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return Error.NotFound("webhook", id);
        }

        db.Subscriptions.Remove(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result> ApplyAsync(WebhookSubscription subscription, SaveWebhookRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            return Error.Validation("webhook.name_invalid", "A name of up to 100 characters is required.");
        }

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || string.IsNullOrEmpty(url.Host))
        {
            return Error.Validation("webhook.url_invalid", "The URL must be absolute with an http or https scheme.");
        }

        var types = (request.EventTypes ?? ["*"]).Select(static t => t.Trim().ToLowerInvariant()).Where(static t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0 || types.Any(static t => !IsPattern(t)))
        {
            return Error.Validation("webhook.event_types_invalid", "Event types are dotted names such as sales.invoice.posted, prefixes such as sales.*, or *.");
        }

        if (await db.Subscriptions.AnyAsync(s => s.Name == name && s.Id != subscription.Id, cancellationToken))
        {
            return Error.Conflict("webhook.name_taken", $"A webhook named '{name}' already exists.");
        }

        subscription.Name = name;
        subscription.Url = url.ToString();
        subscription.EventTypes = types;
        subscription.Filters = request.Filters is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(request.Filters, StringComparer.Ordinal);
        subscription.Active = request.Active;
        return Result.Success();
    }

    private static bool IsPattern(string type) =>
        type == "*" || type.Split('.').All(static part => part.Length > 0 && (part == "*" || part.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')));

    private static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // ------------------------------------------------------------------ deliveries

    public async Task<Result<Page<DeliverySummary>>> ListDeliveriesAsync(Guid subscriptionId, string? status, PageRequest page, CancellationToken cancellationToken)
    {
        if (!await db.Subscriptions.AnyAsync(s => s.Id == subscriptionId, cancellationToken))
        {
            return Error.NotFound("webhook", subscriptionId);
        }

        var query = db.Deliveries.Where(d => d.SubscriptionId == subscriptionId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var paged = await KeysetPaging.ByIdDescendingAsync(query, static d => d.Id, page, cancellationToken);
        return paged.IsSuccess ? paged.Value.Map(Map) : paged.Error!;
    }

    public async Task<DeliverySummary?> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        return delivery is null ? null : Map(delivery);
    }

    /// <summary>Queues the delivery again with a fresh attempt budget (operator "replay").</summary>
    public async Task<Result<DeliverySummary>> ReplayAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return Error.NotFound("webhook_delivery", deliveryId);
        }

        delivery.Status = "pending";
        delivery.Attempt = 0;
        delivery.LastError = null;
        delivery.NextAttemptAt = null;
        delivery.JobId = await jobs.EnqueueAsync(new JobRequest(DeliveryJobType, new WebhookDeliveryPayload(delivery.Id), MaxAttempts: DeliveryMaxAttempts), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Map(delivery);
    }

    /// <summary>Sends a synthetic <c>webhook.test</c> event so an integrator can check URL, signature and parsing.</summary>
    public async Task<Result<DeliverySummary>> TestAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return Error.NotFound("webhook", subscriptionId);
        }

        var eventId = Guid.CreateVersion7();
        var envelope = new WebhookEnvelope(eventId, "webhook.test", 1, clock.UtcNow, unitOfWork.Current.Context.TenantId.Value, "webhook_subscription", subscription.Id,
            JsonSerializer.SerializeToElement(new { message = "Test delivery from Quicker", subscription = subscription.Name }, WebhookJson.Options));
        var delivery = await EnqueueDeliveryAsync(subscription, envelope, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Map(delivery);
    }

    internal async Task<WebhookDelivery> EnqueueDeliveryAsync(WebhookSubscription subscription, WebhookEnvelope envelope, CancellationToken cancellationToken)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = subscription.Id,
            EventId = envelope.Id,
            EventType = envelope.Type,
            Payload = JsonSerializer.Serialize(envelope, WebhookJson.Options),
            CreatedAt = clock.UtcNow,
        };
        delivery.JobId = await jobs.EnqueueAsync(new JobRequest(DeliveryJobType, new WebhookDeliveryPayload(delivery.Id), MaxAttempts: DeliveryMaxAttempts), cancellationToken);
        db.Deliveries.Add(delivery);
        return delivery;
    }

    // ------------------------------------------------------------------ mapping

    private static WebhookSummary Map(WebhookSubscription s) => new(s.Id, s.Name, s.Url, s.EventTypes, s.Filters, s.Active, s.CreatedBy, s.CreatedAt, s.UpdatedAt);

    private static DeliverySummary Map(WebhookDelivery d) => new(d.Id, d.SubscriptionId, d.EventId, d.EventType, JsonDocument.Parse(d.Payload).RootElement.Clone(), d.Attempt, d.Status, d.ResponseStatus, d.ResponseExcerpt, d.LastError, d.JobId, d.CreatedAt, d.AttemptedAt, d.NextAttemptAt, d.DeliveredAt);
}

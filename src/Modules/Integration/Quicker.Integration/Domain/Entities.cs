using Quicker.Persistence.EntityFramework;

namespace Quicker.Integration.Domain;

public sealed class WebhookSubscription : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string SecretEnc { get; set; } = string.Empty;

    /// <summary>Event type patterns: exact ("sales.invoice.posted"), prefix ("sales.*") or everything ("*").</summary>
    public string[] EventTypes { get; set; } = ["*"];

    public Dictionary<string, string> Filters { get; set; } = new(StringComparer.Ordinal);

    public bool Active { get; set; } = true;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool Matches(string eventType) => EventTypes.Any(pattern =>
        pattern == "*" || string.Equals(pattern, eventType, StringComparison.Ordinal) ||
        (pattern.EndsWith(".*", StringComparison.Ordinal) && eventType.StartsWith(pattern[..^1], StringComparison.Ordinal)));
}

public sealed class WebhookDelivery : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid EventId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    public int Attempt { get; set; }

    public string Status { get; set; } = "pending";

    public int? ResponseStatus { get; set; }

    public string? ResponseExcerpt { get; set; }

    public string? LastError { get; set; }

    public Guid? JobId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? AttemptedAt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }
}

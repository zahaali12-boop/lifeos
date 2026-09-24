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

    /// <summary>
    /// Conditions an event must meet besides its type, all of them: <c>aggregateType</c> matches the record the event is
    /// about, any other key the top-level payload property of that name (compared as text, letter case ignored), such
    /// as <c>companyId</c> or <c>orderNumber</c>. An event without the property does not match.
    /// </summary>
    public Dictionary<string, string> Filters { get; set; } = new(StringComparer.Ordinal);

    public bool Active { get; set; } = true;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool Matches(string eventType) => EventTypes.Any(pattern =>
        pattern == "*" || string.Equals(pattern, eventType, StringComparison.Ordinal) ||
        (pattern.EndsWith(".*", StringComparison.Ordinal) && eventType.StartsWith(pattern[..^1], StringComparison.Ordinal)));

    public bool Accepts(System.Text.Json.JsonElement payload, string aggregateType) => Filters.All(filter =>
        string.Equals(filter.Key, "aggregateType", StringComparison.Ordinal)
            ? string.Equals(filter.Value, aggregateType, StringComparison.OrdinalIgnoreCase)
            : Property(payload, filter.Key) is { } value && string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase));

    private static string? Property(System.Text.Json.JsonElement payload, string name)
    {
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => property.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => property.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }
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

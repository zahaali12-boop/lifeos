namespace Quicker.Kernel.Events;

/// <summary>An in-process fact raised by an aggregate and handled inside the same module and transaction (ADR-0010).</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// A module's public fact, declared in its Contracts project, versioned, and delivered through the outbox
/// (at-least-once). Payloads carry identifiers and the minimum consumers need.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Stable name such as "sales.invoice.posted".</summary>
    static abstract string EventType { get; }

    /// <summary>Schema version of the payload, starting at 1.</summary>
    static abstract int EventVersion { get; }

    Guid AggregateId { get; }

    string AggregateType { get; }
}

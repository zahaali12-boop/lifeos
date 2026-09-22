using System.Text.Json;
using Dapper;
using Quicker.Kernel.Events;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Messaging.Outbox;

/// <summary>Writes integration events to the outbox inside the current unit of work, so they commit with the change they describe.</summary>
public interface IOutbox
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default) where TEvent : IIntegrationEvent;
}

/// <summary>What a handler knows about the delivery: the message, its tenant, who caused it, and the attempt.</summary>
public sealed record EventContext(Guid EventId, TenantId? TenantId, DateTimeOffset OccurredAt, string? CorrelationId, string? CausationId, string Actor, int Attempt);

/// <summary>
/// Reacts to an integration event inside its own unit of work bound to the event's tenant. Delivery is
/// at-least-once; the dispatcher records (handler, event) in <c>ops.inbox</c> in the same transaction as the
/// handler's effect, so a redelivery after a crash is skipped and the effect happens exactly once.
/// </summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, EventContext context, CancellationToken cancellationToken);
}

/// <summary>One registered (event type, handler) pair; the registry is assembled from DI registrations at start-up.</summary>
public sealed record EventHandlerRegistration(string EventType, int EventVersion, Type EventClrType, Type HandlerType)
{
    /// <summary>Stable handler name stored in the inbox: the handler's full type name.</summary>
    public string HandlerName => HandlerType.FullName ?? HandlerType.Name;
}

public sealed class EventHandlerRegistry(IEnumerable<EventHandlerRegistration> registrations)
{
    private readonly ILookup<string, EventHandlerRegistration> _byEventType = registrations.ToLookup(static r => r.EventType, StringComparer.Ordinal);

    public IReadOnlyList<EventHandlerRegistration> For(string eventType) => _byEventType[eventType].ToList();

    public IEnumerable<EventHandlerRegistration> All => _byEventType.SelectMany(static g => g);
}

public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class TransactionalOutbox(IUnitOfWorkAccessor unitOfWork) : IOutbox
{
    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default) where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var uow = unitOfWork.Current;
        var context = uow.Context;
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ops.outbox_messages (id, tenant_id, occurred_at, event_type, event_version, aggregate_type, aggregate_id, payload, correlation_id, causation_id, actor)
            VALUES (@id, @tenant, now(), @type, @version, @aggregateType, @aggregateId, @payload::jsonb, @correlation, @causation, @actor)
            """, new
        {
            id = Uuid7.New(),
            tenant = context.IsAnonymous ? (Guid?)null : context.TenantId.Value,
            type = TEvent.EventType,
            version = TEvent.EventVersion,
            aggregateType = integrationEvent.AggregateType,
            aggregateId = integrationEvent.AggregateId,
            payload = JsonSerializer.Serialize(integrationEvent, OutboxJson.Options),
            correlation = context.CorrelationId ?? context.RequestId,
            causation = context.RequestId,
            actor = $"{context.ActorType}:{context.ActorDisplay}",
        }, uow.Transaction, cancellationToken: cancellationToken));
    }
}

/// <summary>A stored outbox row as the dispatcher and the operator UI see it.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public long Seq { get; set; }

    public Guid? TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string EventType { get; set; } = string.Empty;

    public int EventVersion { get; set; }

    public string AggregateType { get; set; } = string.Empty;

    public Guid AggregateId { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string? CausationId { get; set; }

    public string Actor { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? DeadAt { get; set; }

    public TenantContext ContextFor(string requestId) =>
        (TenantId is { } tenant ? TenantContext.System(new TenantId(tenant), requestId) : TenantContext.Anonymous(requestId)) with { CorrelationId = CorrelationId };
}

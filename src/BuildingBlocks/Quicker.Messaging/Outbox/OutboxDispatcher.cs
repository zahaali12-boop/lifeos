using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;

namespace Quicker.Messaging.Outbox;

/// <summary>
/// Claims pending outbox messages with <c>FOR UPDATE SKIP LOCKED</c> in insertion order, one in flight per aggregate
/// (a message waits until every earlier message of its aggregate is published, dead letters included, so order
/// holds per aggregate and a poison message stops its aggregate until an operator acts), runs every registered
/// handler in its own unit of work bound to the message's tenant, and marks the message published. A handler
/// failure backs off exponentially (1 s → 1 h) and dead-letters after the configured attempts; the inbox makes
/// redelivery to an already-successful handler a no-op.
/// </summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IUnitOfWorkFactory unitOfWorkFactory,
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantContext,
    EventHandlerRegistry registry,
    IOptions<WorkerOptions> options,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Processes one batch; returns how many messages were claimed (0 means the queue is drained).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var claim = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var messages = (await connection.QueryAsync<OutboxMessage>(new CommandDefinition("""
            SELECT id, seq, tenant_id, occurred_at, event_type, event_version, aggregate_type, aggregate_id, payload::text AS payload,
                   correlation_id, causation_id, actor, published_at, attempts, next_attempt_at, last_error, dead_at
            FROM ops.outbox_messages o
            WHERE o.published_at IS NULL AND o.dead_at IS NULL AND o.next_attempt_at <= now()
              AND NOT EXISTS (
                SELECT 1 FROM ops.outbox_messages earlier
                WHERE earlier.aggregate_id = o.aggregate_id AND earlier.published_at IS NULL AND earlier.seq < o.seq)
            ORDER BY o.seq
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
            """, new { batch = options.Value.OutboxBatchSize }, claim, cancellationToken: cancellationToken))).ToList();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await DeliverAsync(message, cancellationToken);
            if (outcome is null)
            {
                await connection.ExecuteAsync(new CommandDefinition("UPDATE ops.outbox_messages SET published_at = now(), last_error = NULL WHERE id = @id", new { id = message.Id }, claim, cancellationToken: cancellationToken));
            }
            else
            {
                var attempts = message.Attempts + 1;
                var dead = attempts >= options.Value.OutboxMaxAttempts;
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE ops.outbox_messages
                    SET attempts = @attempts, next_attempt_at = now() + make_interval(secs => @delay), last_error = @error, dead_at = CASE WHEN @dead THEN now() ELSE NULL END
                    WHERE id = @id
                    """, new { id = message.Id, attempts, delay = Backoff.Seconds(attempts), error = Truncate(outcome), dead }, claim, cancellationToken: cancellationToken));
                if (dead)
                {
                    logger.LogError("Outbox message {EventId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}", message.Id, message.EventType, attempts, outcome);
                }
            }
        }

        await claim.CommitAsync(cancellationToken);
        return messages.Count;
    }

    /// <summary>Runs every handler; returns null when all succeeded, otherwise the first failure's description.</summary>
    private async Task<string?> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var handlers = registry.For(message.EventType);
        foreach (var registration in handlers)
        {
            try
            {
                await HandleAsync(message, registration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Handler {Handler} failed for event {EventId} ({EventType}), attempt {Attempt}", registration.HandlerName, message.Id, message.EventType, message.Attempts + 1);
                return $"{registration.HandlerName}: {ex.GetType().Name}: {ex.Message}";
            }
        }

        return null;
    }

    private async Task HandleAsync(OutboxMessage message, EventHandlerRegistration registration, CancellationToken cancellationToken)
    {
        var requestId = "evt-" + message.Id.ToString("N")[^12..];
        var context = message.ContextFor(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);

        // The inbox row is the idempotency record: inserted with the effect, so both commit or neither does.
        var inserted = await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ops.inbox (handler, event_id) VALUES (@handler, @id) ON CONFLICT DO NOTHING",
            new { handler = registration.HandlerName, id = message.Id }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        if (inserted == 0)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return;
        }

        var payload = JsonSerializer.Deserialize(message.Payload, registration.EventClrType, OutboxJson.Options)
            ?? throw new InvalidOperationException($"Event {message.Id} payload could not be read as {registration.EventClrType.Name}.");
        var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        var eventContext = new EventContext(message.Id, message.TenantId is { } t ? new TenantId(t) : null, message.OccurredAt, message.CorrelationId, message.CausationId, message.Actor, message.Attempts + 1);
        var method = registration.HandlerType.GetMethod("HandleAsync", [registration.EventClrType, typeof(EventContext), typeof(CancellationToken)])
            ?? throw new InvalidOperationException($"{registration.HandlerType.Name} does not implement HandleAsync({registration.EventClrType.Name}).");
        await (Task)method.Invoke(handler, [payload, eventContext, cancellationToken])!;
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];
}

/// <summary>Operator actions on the outbox: dead letters and retries (ADR-0010 "a UI page lists and retries dead letters").</summary>
public sealed class OutboxAdmin(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<OutboxMessage>> ListAsync(string state, Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        var filter = state switch
        {
            "dead" => "dead_at IS NOT NULL",
            "published" => "published_at IS NOT NULL",
            "pending" => "published_at IS NULL AND dead_at IS NULL",
            _ => "TRUE",
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<OutboxMessage>(new CommandDefinition($"""
            SELECT id, seq, tenant_id, occurred_at, event_type, event_version, aggregate_type, aggregate_id, payload::text AS payload,
                   correlation_id, causation_id, actor, published_at, attempts, next_attempt_at, last_error, dead_at
            FROM ops.outbox_messages
            WHERE {filter} AND (@tenant IS NULL OR tenant_id = @tenant)
            ORDER BY seq DESC
            LIMIT @limit
            """, new { tenant = tenantId, limit = Math.Clamp(limit, 1, 500) }, cancellationToken: cancellationToken))).ToList();
    }

    /// <summary>Puts a dead letter back in the queue with a fresh attempt budget; returns false when the id is unknown or not dead.</summary>
    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ops.outbox_messages SET dead_at = NULL, attempts = 0, next_attempt_at = now(), last_error = NULL WHERE id = @id AND dead_at IS NOT NULL",
            new { id }, cancellationToken: cancellationToken)) == 1;
    }

    /// <summary>Removes published messages older than the retention (the archive partition of ADR-0010 is this delete until reporting needs history).</summary>
    public async Task<int> ArchiveAsync(int olderThanDays, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM ops.inbox i USING ops.outbox_messages o WHERE o.id = i.event_id AND o.published_at < now() - make_interval(days => @days)", new { days = olderThanDays }, cancellationToken: cancellationToken));
        return await connection.ExecuteAsync(new CommandDefinition("DELETE FROM ops.outbox_messages WHERE published_at < now() - make_interval(days => @days)", new { days = olderThanDays }, cancellationToken: cancellationToken));
    }
}

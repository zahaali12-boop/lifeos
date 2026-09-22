using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Audit.Application;

internal enum AuditSource
{
    Explicit,
    Captured,
}

/// <summary>An event waiting for the unit of work to commit.</summary>
internal sealed class PendingAuditEvent
{
    public Guid Id { get; } = Uuid7.New();

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The context at the moment of recording (a unit of work may switch tenant later).</summary>
    public required TenantContext Context { get; init; }

    public required AuditSource Source { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public required string EntityDisplay { get; init; }

    public required string Action { get; init; }

    public JsonNode? Before { get; set; }

    public JsonNode? After { get; set; }

    public JsonObject? Diff { get; set; }

    public JsonNode? Details { get; init; }

    public string? Reason { get; init; }

    public Guid? CompanyId { get; init; }
}

/// <summary>
/// The audit sink (ADR-0015). Events are buffered per unit of work and written in one go immediately before the
/// transaction commits, so the per-tenant chain lock is held for microseconds rather than for the whole request.
/// Events recorded while the unit of work was anonymous follow it into the tenant it switches to (sign-up, invitation
/// acceptance, SSO); events of a unit of work that never enters a tenant go to the platform chain.
/// </summary>
public sealed class AuditWriter(IUnitOfWorkAccessor unitOfWork, IClock clock) : IAuditSink
{
    private static readonly ConcurrentDictionary<string, byte> KnownPartitions = new(StringComparer.Ordinal);

    private readonly List<PendingAuditEvent> _pending = [];
    private bool _flushRegistered;

    /// <summary>Events recorded in this unit of work and not yet written (tests and diagnostics).</summary>
    public int PendingCount => _pending.Count;

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.EntityType) || string.IsNullOrWhiteSpace(entry.Action))
        {
            throw new ArgumentException("An audit entry needs an entity type and an action.", nameof(entry));
        }

        var before = AuditJson.Redact(AuditJson.ToNode(entry.Before));
        var after = AuditJson.Redact(AuditJson.ToNode(entry.After));
        Enqueue(new PendingAuditEvent
        {
            OccurredAt = clock.UtcNow,
            Context = unitOfWork.Current.Context,
            Source = AuditSource.Explicit,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            EntityDisplay = entry.EntityDisplay ?? string.Empty,
            Action = entry.Action,
            Before = before,
            After = after,
            Diff = AuditJson.Diff(before, after),
            Details = AuditJson.Redact(AuditJson.ToNode(entry.Details)),
            Reason = entry.Reason,
            CompanyId = entry.CompanyId,
        });
        return Task.CompletedTask;
    }

    /// <summary>Called by the EF change-capture interceptor for every tracked change of an audited entity.</summary>
    internal void Capture(string entityType, Guid entityId, string display, string action, JsonObject? before, JsonObject? after, JsonObject? diff, Guid? companyId)
    {
        Enqueue(new PendingAuditEvent
        {
            OccurredAt = clock.UtcNow,
            Context = unitOfWork.Current.Context,
            Source = AuditSource.Captured,
            EntityType = entityType,
            EntityId = entityId,
            EntityDisplay = display,
            Action = action,
            Before = before,
            After = after,
            Diff = diff,
            CompanyId = companyId,
        });
    }

    private void Enqueue(PendingAuditEvent pending)
    {
        _pending.Add(pending);
        if (!_flushRegistered)
        {
            unitOfWork.Current.BeforeCommit(FlushAsync);
            _flushRegistered = true;
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        _flushRegistered = false;
        if (_pending.Count == 0)
        {
            return;
        }

        var events = Reconcile(_pending);
        _pending.Clear();

        var uow = unitOfWork.Current;
        var home = uow.Context;
        foreach (var pending in events)
        {
            // An event recorded before the unit of work entered its tenant belongs to that tenant and to the actor
            // the unit of work ended up with (the person who just signed up or accepted the invitation).
            var context = pending.Context.IsAnonymous && !home.IsAnonymous ? home : pending.Context;
            if (context.IsAnonymous)
            {
                await InsertPlatformEventAsync(uow, pending, context, cancellationToken);
                continue;
            }

            if (context.TenantId != home.TenantId)
            {
                throw new InvalidOperationException($"Audit event for tenant {context.TenantId} recorded in a unit of work bound to tenant {home.TenantId}.");
            }

            await InsertTenantEventAsync(uow, pending, context, cancellationToken);
        }
    }

    /// <summary>
    /// Explicit events win over captured ones for the same record in the same unit of work: the captured
    /// before/after/diff enrich the explicit event, and the captured row event is dropped.
    /// </summary>
    private static List<PendingAuditEvent> Reconcile(List<PendingAuditEvent> pending)
    {
        var explicitByRecord = pending
            .Where(static e => e.Source == AuditSource.Explicit)
            .ToLookup(static e => (e.EntityType, e.EntityId));

        var result = new List<PendingAuditEvent>(pending.Count);
        foreach (var candidate in pending)
        {
            if (candidate.Source == AuditSource.Captured && explicitByRecord.Contains((candidate.EntityType, candidate.EntityId)))
            {
                foreach (var target in explicitByRecord[(candidate.EntityType, candidate.EntityId)])
                {
                    target.Before ??= candidate.Before;
                    target.After ??= candidate.After;
                    target.Diff ??= candidate.Diff;
                }

                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static async Task InsertTenantEventAsync(IUnitOfWork uow, PendingAuditEvent e, TenantContext context, CancellationToken cancellationToken)
    {
        await EnsurePartitionAsync(uow, e.OccurredAt, cancellationToken);
        const string sql = """
            INSERT INTO app.aud_events (tenant_id, id, occurred_at, actor_type, actor_id, actor_display, actor_ip, user_agent, request_id, correlation_id,
                                        company_id, entity_type, entity_id, entity_display, action, before, after, diff, details, reason)
            VALUES (@tenantId, @id, @occurredAt, @actorType, @actorId, @actorDisplay, @actorIp::inet, @userAgent, @requestId, @correlationId,
                    @companyId, @entityType, @entityId, @entityDisplay, @action, @before::jsonb, @after::jsonb, @diff::jsonb, @details::jsonb, @reason)
            """;
        await uow.Connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenantId = context.TenantId.Value,
            id = e.Id,
            occurredAt = e.OccurredAt,
            actorType = context.ActorType,
            actorId = context.UserId?.Value,
            actorDisplay = context.ActorDisplay,
            actorIp = context.ClientIp,
            userAgent = context.UserAgent,
            requestId = context.RequestId,
            correlationId = context.CorrelationId,
            companyId = e.CompanyId,
            entityType = e.EntityType,
            entityId = e.EntityId,
            entityDisplay = e.EntityDisplay,
            action = e.Action,
            before = AuditJson.Serialize(e.Before),
            after = AuditJson.Serialize(e.After),
            diff = AuditJson.Serialize(e.Diff),
            details = AuditJson.Serialize(e.Details),
            reason = e.Reason,
        }, uow.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task InsertPlatformEventAsync(IUnitOfWork uow, PendingAuditEvent e, TenantContext context, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO control.aud_platform_events (id, occurred_at, actor_type, actor_id, actor_display, actor_ip, user_agent, request_id, correlation_id,
                                                     entity_type, entity_id, entity_display, action, before, after, diff, details, reason)
            VALUES (@id, @occurredAt, @actorType, @actorId, @actorDisplay, @actorIp::inet, @userAgent, @requestId, @correlationId,
                    @entityType, @entityId, @entityDisplay, @action, @before::jsonb, @after::jsonb, @diff::jsonb, @details::jsonb, @reason)
            """;
        await uow.Connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = e.Id,
            occurredAt = e.OccurredAt,
            actorType = context.ActorType,
            actorId = context.UserId?.Value,
            actorDisplay = context.ActorDisplay,
            actorIp = context.ClientIp,
            userAgent = context.UserAgent,
            requestId = context.RequestId,
            correlationId = context.CorrelationId,
            entityType = e.EntityType,
            entityId = e.EntityId,
            entityDisplay = e.EntityDisplay,
            action = e.Action,
            before = AuditJson.Serialize(e.Before),
            after = AuditJson.Serialize(e.After),
            diff = AuditJson.Serialize(e.Diff),
            details = AuditJson.Serialize(e.Details),
            reason = e.Reason,
        }, uow.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>Makes sure the month's partition exists; remembers it per database once its existence is committed.</summary>
    private static async Task EnsurePartitionAsync(IUnitOfWork uow, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var month = occurredAt.UtcDateTime.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
        var key = uow.Connection.Database + ":" + month;
        if (KnownPartitions.ContainsKey(key))
        {
            return;
        }

        var existed = await uow.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT app.aud_ensure_partition(@day::date)", new { day = month }, uow.Transaction, cancellationToken: cancellationToken));
        if (existed)
        {
            KnownPartitions[key] = 1;
        }
    }
}

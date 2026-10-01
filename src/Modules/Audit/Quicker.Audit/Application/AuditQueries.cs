using System.Globalization;
using System.Text.Json;
using Dapper;
using Quicker.Persistence;

namespace Quicker.Audit.Application;

public sealed record AuditActor(string Type, Guid? Id, string Display);

public sealed record AuditEventSummary(
    Guid Id,
    long Seq,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    string EntityType,
    Guid EntityId,
    string EntityDisplay,
    string Action,
    string? Reason,
    Guid? CompanyId,
    string? RequestId,
    bool HasChanges);

public sealed record AuditEventDetail(
    Guid Id,
    long Seq,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    string? ActorIp,
    string? UserAgent,
    string? RequestId,
    string? CorrelationId,
    Guid? CompanyId,
    string EntityType,
    Guid EntityId,
    string EntityDisplay,
    string Action,
    JsonElement? Before,
    JsonElement? After,
    JsonElement? Diff,
    JsonElement? Details,
    string? Reason,
    string Hash,
    string PrevHash);

public sealed record AuditPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Explorer filter; <paramref name="BeforeSeq"/> is the keyset cursor (events are listed newest first).</summary>
public sealed record AuditFilter(
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? ActorId = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? CompanyId = null,
    string? Search = null,
    long? BeforeSeq = null,
    int Limit = 50);

/// <summary>Read side of the audit log: record timelines, the tenant explorer, exports and the platform chain.</summary>
public sealed class AuditQueries(IUnitOfWorkAccessor unitOfWork, AuditOptions options)
{
    private const string Columns = """
        id, seq, occurred_at, actor_type, actor_id, actor_display, actor_ip::text AS actor_ip, user_agent, request_id, correlation_id,
        entity_type, entity_id, entity_display, action, before::text AS before, after::text AS after, diff::text AS diff, details::text AS details,
        reason, prev_hash, hash
        """;

    private sealed class EventRow
    {
        public Guid Id { get; set; }

        public long Seq { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string ActorType { get; set; } = string.Empty;

        public Guid? ActorId { get; set; }

        public string ActorDisplay { get; set; } = string.Empty;

        public string? ActorIp { get; set; }

        public string? UserAgent { get; set; }

        public string? RequestId { get; set; }

        public string? CorrelationId { get; set; }

        public Guid? CompanyId { get; set; }

        public string EntityType { get; set; } = string.Empty;

        public Guid EntityId { get; set; }

        public string EntityDisplay { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string? Before { get; set; }

        public string? After { get; set; }

        public string? Diff { get; set; }

        public string? Details { get; set; }

        public string? Reason { get; set; }

        public byte[] PrevHash { get; set; } = [];

        public byte[] Hash { get; set; } = [];
    }

    private Guid TenantId => unitOfWork.Current.Context.TenantId.Value;

    public Task<AuditPage<AuditEventSummary>> SearchAsync(AuditFilter filter, CancellationToken cancellationToken) =>
        SearchCoreAsync(tenantChain: true, filter, cancellationToken);

    public Task<AuditPage<AuditEventSummary>> SearchPlatformAsync(AuditFilter filter, CancellationToken cancellationToken) =>
        SearchCoreAsync(tenantChain: false, filter, cancellationToken);

    /// <summary>Every event of one record, oldest first, with its changes.</summary>
    public async Task<IReadOnlyList<AuditEventDetail>> TimelineAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<EventRow>(new CommandDefinition(
            $"SELECT {Columns}, company_id FROM app.aud_events WHERE tenant_id = @tenantId AND entity_type = @entityType AND entity_id = @entityId ORDER BY seq LIMIT @limit",
            new { tenantId = TenantId, entityType, entityId, limit = options.ExportMaxRows }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Detail).ToList();
    }

    public async Task<AuditEventDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleOrDefaultAsync<EventRow>(new CommandDefinition(
            $"SELECT {Columns}, company_id FROM app.aud_events WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId = TenantId, id }, uow.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Detail(row);
    }

    /// <summary>Matching events with full payloads, oldest first, capped at the synchronous export limit.</summary>
    public async Task<IReadOnlyList<AuditEventDetail>> ExportAsync(AuditFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (where, parameters) = Where(tenantChain: true, filter);
        parameters.Add("limit", options.ExportMaxRows);
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<EventRow>(new CommandDefinition(
            $"SELECT {Columns}, company_id FROM app.aud_events WHERE {where} ORDER BY seq LIMIT @limit", parameters, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Detail).ToList();
    }

    private async Task<AuditPage<AuditEventSummary>> SearchCoreAsync(bool tenantChain, AuditFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var (where, parameters) = Where(tenantChain, filter);
        var limit = Math.Clamp(filter.Limit, 1, options.PageMaxSize);
        parameters.Add("limit", limit + 1);
        var table = tenantChain ? "app.aud_events" : "control.aud_platform_events";
        var companyColumn = tenantChain ? ", company_id" : ", NULL::uuid AS company_id";
        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<EventRow>(new CommandDefinition(
            $"SELECT {Columns}{companyColumn} FROM {table} WHERE {where} ORDER BY seq DESC LIMIT @limit", parameters, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var items = rows.Take(limit).Select(Summary).ToList();
        var next = rows.Count > limit ? items[^1].Seq.ToString(CultureInfo.InvariantCulture) : null;
        return new AuditPage<AuditEventSummary>(items, next);
    }

    private (string Where, DynamicParameters Parameters) Where(bool tenantChain, AuditFilter filter)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();
        if (tenantChain)
        {
            clauses.Add("tenant_id = @tenantId");
            parameters.Add("tenantId", TenantId);
            if (filter.CompanyId is { } company)
            {
                clauses.Add("company_id = @companyId");
                parameters.Add("companyId", company);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            clauses.Add("entity_type = @entityType");
            parameters.Add("entityType", filter.EntityType.Trim());
        }

        if (filter.EntityId is { } entityId)
        {
            clauses.Add("entity_id = @entityId");
            parameters.Add("entityId", entityId);
        }

        if (filter.ActorId is { } actor)
        {
            clauses.Add("actor_id = @actorId");
            parameters.Add("actorId", actor);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            clauses.Add("action = @action");
            parameters.Add("action", filter.Action.Trim());
        }

        if (filter.From is { } from)
        {
            clauses.Add("occurred_at >= @from");
            parameters.Add("from", from);
        }

        if (filter.To is { } to)
        {
            clauses.Add("occurred_at < @to");
            parameters.Add("to", to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("entity_display ILIKE @search");
            parameters.Add("search", "%" + filter.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        }

        if (filter.BeforeSeq is { } before)
        {
            clauses.Add("seq < @beforeSeq");
            parameters.Add("beforeSeq", before);
        }

        return (clauses.Count == 0 ? "true" : string.Join(" AND ", clauses), parameters);
    }

    private static AuditEventSummary Summary(EventRow row) => new(
        row.Id, row.Seq, row.OccurredAt, new AuditActor(row.ActorType, row.ActorId, row.ActorDisplay), row.EntityType, row.EntityId, row.EntityDisplay,
        row.Action, row.Reason, row.CompanyId, row.RequestId, row.Before is not null || row.After is not null || row.Diff is not null);

    private static AuditEventDetail Detail(EventRow row) => new(
        row.Id, row.Seq, row.OccurredAt, new AuditActor(row.ActorType, row.ActorId, row.ActorDisplay), row.ActorIp, row.UserAgent, row.RequestId, row.CorrelationId,
        row.CompanyId, row.EntityType, row.EntityId, row.EntityDisplay, row.Action,
        AuditJson.Parse(row.Before), AuditJson.Parse(row.After), AuditJson.Parse(row.Diff), AuditJson.Parse(row.Details), row.Reason,
        Convert.ToHexStringLower(row.Hash), Convert.ToHexStringLower(row.PrevHash));
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Audit.Application;
using Quicker.Audit.Contracts;
using Quicker.Web;

namespace Quicker.Audit.Api;

/// <summary>Explorer query string; the cursor is opaque to clients (the sequence number of the last item of the previous page).</summary>
public sealed class AuditSearchQuery
{
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public Guid? ActorId { get; set; }

    public string? Action { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public Guid? CompanyId { get; set; }

    /// <summary>Matches the record display (number, code, email).</summary>
    public string? Q { get; set; }

    public string? Cursor { get; set; }

    public int? Limit { get; set; }

    public AuditFilter ToFilter() => new(EntityType, EntityId, ActorId, Action, From, To, CompanyId, Q,
        long.TryParse(Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor) ? cursor : null, Limit ?? 50);
}

public sealed record ChainStatus(ChainHead? Head, AuditAnchor? LastAnchor, ChainVerification? LastVerification);

/// <summary>The audit surface of /api/v1 (ADR-0015): timelines, explorer, export, chain status, anchoring, verification.</summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var audit = api.MapGroup("/audit").WithTags("Audit").RequireAuthorization();

        audit.MapGet("/events", async ([AsParameters] AuditSearchQuery query, AuditQueries queries, CancellationToken ct) =>
            TypedResults.Ok(await queries.SearchAsync(query.ToFilter(), ct)))
            .RequirePermission(AuditPermissions.EventRead)
            .WithSummary("Tenant audit explorer: newest first, filter by record, actor, action, company and time");

        audit.MapGet("/events/{id:guid}", async (Guid id, AuditQueries queries, CancellationToken ct) =>
        {
            var detail = await queries.GetAsync(id, ct);
            return detail is null ? ApiProblems.From(Kernel.Results.Error.NotFound("audit_event", id)) : Results.Ok(detail);
        }).RequirePermission(AuditPermissions.EventRead)
            .WithSummary("One audit event with its request context, before/after values, field-level diff and chain hashes")
            .Produces<AuditEventDetail>();

        audit.MapGet("/records/{entityType}/{entityId:guid}", async (string entityType, Guid entityId, AuditQueries queries, CancellationToken ct) =>
            TypedResults.Ok(await queries.TimelineAsync(entityType, entityId, ct)))
            .RequirePermission(AuditPermissions.EventRead)
            .WithSummary("Timeline of one record: every event with before/after values and the field-level diff");

        audit.MapGet("/export", async ([AsParameters] AuditSearchQuery query, AuditQueries queries, IAuditSink sink, CurrentPrincipal current, CancellationToken ct) =>
        {
            var filter = query.ToFilter();
            var rows = await queries.ExportAsync(filter, ct);
            var builder = new StringBuilder();
            foreach (var row in rows)
            {
                builder.Append(JsonSerializer.Serialize(row, AuditJson.Options)).Append('\n');
            }

            await sink.RecordAsync(new AuditEntry("audit_log", current.Required.TenantId.Value, "audit log", AuditActions.Exported,
                Details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["filter"] = filter, ["rows"] = rows.Count }), ct);
            return Results.File(Encoding.UTF8.GetBytes(builder.ToString()), "application/x-ndjson", "audit-events.ndjson");
        }).Produces<Stream>(StatusCodes.Status200OK, "application/x-ndjson")
          .RequirePermission(AuditPermissions.EventExport)
          .WithSummary("Export matching events as JSON lines (the export itself is audited)");

        // ---------------------------------------------------------------- chain
        audit.MapGet("/chain", async (ChainAnchoring anchoring, ChainVerifier verifier, CancellationToken ct) =>
            Results.Ok(new ChainStatus(
                await anchoring.TenantHeadAsync(ct),
                First(await anchoring.ListTenantAnchorsAsync(1, ct)),
                First(await verifier.ListTenantVerificationsAsync(1, ct)))))
            .Produces<ChainStatus>()
            .RequirePermission(AuditPermissions.ChainVerify)
            .WithSummary("Chain head, last anchor and last verification of this tenant");

        audit.MapPost("/chain/verify", async (ChainVerifier verifier, CancellationToken ct) => TypedResults.Ok(await verifier.VerifyTenantAsync(ct)))
            .RequirePermission(AuditPermissions.ChainVerify)
            .WithSummary("Recompute the whole chain and compare it with the head and the newest anchor");

        audit.MapPost("/chain/anchor", async (ChainAnchoring anchoring, CancellationToken ct) =>
        {
            var anchor = await anchoring.AnchorTenantAsync(ct);
            return anchor is null ? Results.NoContent() : Results.Ok(anchor);
        }).Produces<AuditAnchor>().Produces(StatusCodes.Status204NoContent)
          .RequirePermission(AuditPermissions.ChainAnchor)
          .WithSummary("Write the current chain head to the anchor store");

        audit.MapGet("/chain/anchors", async (int? limit, ChainAnchoring anchoring, CancellationToken ct) =>
            TypedResults.Ok(await anchoring.ListTenantAnchorsAsync(limit ?? 50, ct)))
            .RequirePermission(AuditPermissions.ChainVerify);

        audit.MapGet("/chain/verifications", async (int? limit, ChainVerifier verifier, CancellationToken ct) =>
            TypedResults.Ok(await verifier.ListTenantVerificationsAsync(limit ?? 50, ct)))
            .RequirePermission(AuditPermissions.ChainVerify);

        // ---------------------------------------------------------------- platform chain (operators)
        var platform = audit.MapGroup("/platform").RequireOperator();

        platform.MapGet("/events", async ([AsParameters] AuditSearchQuery query, AuditQueries queries, CancellationToken ct) =>
            TypedResults.Ok(await queries.SearchPlatformAsync(query.ToFilter(), ct)))
            .WithSummary("Events recorded outside any tenant: sign-in attempts, password resets");

        platform.MapGet("/chain", async (ChainAnchoring anchoring, ChainVerifier verifier, CancellationToken ct) =>
            Results.Ok(new ChainStatus(
                await anchoring.PlatformHeadAsync(ct),
                First(await anchoring.ListPlatformAnchorsAsync(1, ct)),
                First(await verifier.ListPlatformVerificationsAsync(1, ct)))))
            .Produces<ChainStatus>();

        platform.MapPost("/chain/verify", async (ChainVerifier verifier, CancellationToken ct) => TypedResults.Ok(await verifier.VerifyPlatformAsync(ct)));

        platform.MapPost("/chain/anchor", async (ChainAnchoring anchoring, CancellationToken ct) =>
        {
            var anchor = await anchoring.AnchorPlatformAsync(ct);
            return anchor is null ? Results.NoContent() : Results.Ok(anchor);
        }).Produces<AuditAnchor>().Produces(StatusCodes.Status204NoContent);

        return api;
    }

    private static T? First<T>(IReadOnlyList<T> items) where T : class => items.Count == 0 ? null : items[0];
}

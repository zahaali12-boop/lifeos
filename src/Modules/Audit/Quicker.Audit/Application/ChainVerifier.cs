using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Audit.Application;

public static class VerificationStatus
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Broken = "broken";
    public const string Truncated = "truncated";
    public const string AnchorMismatch = "anchor_mismatch";
}

public sealed record ChainVerification(
    Guid Id,
    string Chain,
    Guid? TenantId,
    DateTimeOffset VerifiedAt,
    long FromSeq,
    long ToSeq,
    string Status,
    long? FirstBrokenSeq,
    string? Message,
    long? AnchorSeq,
    bool? AnchorMatched,
    int DurationMs,
    string HeadHash);

/// <summary>
/// Recomputes a chain link by link (ADR-0015): sequence numbers contiguous from 1, each previous-hash equal to the
/// predecessor's hash, each hash equal to SHA-256(prev_hash || canonical text), the stored head equal to the last
/// link, and the newest anchor (database row and external store) equal to the hash at its sequence number.
/// The canonical text is rendered by PostgreSQL from the stored columns; the hashing happens here.
/// </summary>
public sealed class ChainVerifier(IUnitOfWorkAccessor unitOfWork, IClock clock, IAuditAnchorStore store, ChainAnchoring anchoring)
{
    private const int PageSize = 2000;

    private const string CanonicalTenant = """
        app.aud_canonical(id, tenant_id, seq, occurred_at, actor_type, actor_id, actor_display, actor_ip, user_agent, request_id, correlation_id, company_id,
                          entity_type, entity_id, entity_display, action, before, after, diff, details, reason)
        """;

    private const string CanonicalPlatform = """
        app.aud_canonical(id, NULL::uuid, seq, occurred_at, actor_type, actor_id, actor_display, actor_ip, user_agent, request_id, correlation_id, NULL::uuid,
                          entity_type, entity_id, entity_display, action, before, after, diff, details, reason)
        """;

    private sealed class LinkRow
    {
        public long Seq { get; set; }

        public byte[] PrevHash { get; set; } = [];

        public byte[] Hash { get; set; } = [];

        public string Canonical { get; set; } = string.Empty;
    }

    private sealed class VerificationRow
    {
        public Guid Id { get; set; }

        public string Chain { get; set; } = string.Empty;

        public Guid? TenantId { get; set; }

        public DateTimeOffset VerifiedAt { get; set; }

        public long FromSeq { get; set; }

        public long ToSeq { get; set; }

        public string Status { get; set; } = string.Empty;

        public long? FirstBrokenSeq { get; set; }

        public string? Message { get; set; }

        public long? AnchorSeq { get; set; }

        public bool? AnchorMatched { get; set; }

        public int DurationMs { get; set; }
    }

    private Guid TenantId => unitOfWork.Current.Context.TenantId.Value;

    public Task<ChainVerification> VerifyTenantAsync(CancellationToken cancellationToken) => VerifyAsync(ChainKind.Tenant, TenantId, cancellationToken);

    public Task<ChainVerification> VerifyPlatformAsync(CancellationToken cancellationToken) => VerifyAsync(ChainKind.Platform, null, cancellationToken);

    public Task<IReadOnlyList<ChainVerification>> ListTenantVerificationsAsync(int limit, CancellationToken cancellationToken) => ListAsync(ChainKind.Tenant, TenantId, limit, cancellationToken);

    public Task<IReadOnlyList<ChainVerification>> ListPlatformVerificationsAsync(int limit, CancellationToken cancellationToken) => ListAsync(ChainKind.Platform, null, limit, cancellationToken);

    private async Task<ChainVerification> VerifyAsync(string chain, Guid? tenantId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var uow = unitOfWork.Current;
        var head = await anchoring.HeadAsync(chain, tenantId, cancellationToken);
        var anchor = await anchoring.LatestAsync(chain, tenantId, cancellationToken);

        var sql = chain == ChainKind.Tenant
            ? $"SELECT seq, prev_hash, hash, {CanonicalTenant} AS canonical FROM app.aud_events WHERE tenant_id = @tenantId AND seq > @after ORDER BY seq LIMIT @page"
            : $"SELECT seq, prev_hash, hash, {CanonicalPlatform} AS canonical FROM control.aud_platform_events WHERE seq > @after ORDER BY seq LIMIT @page";

        long expected = 1;
        var previous = Array.Empty<byte>();
        long last = 0;
        var lastHash = Array.Empty<byte>();
        byte[]? hashAtAnchor = null;
        long? brokenAt = null;
        string? message = null;

        while (brokenAt is null)
        {
            var page = (await uow.Connection.QueryAsync<LinkRow>(new CommandDefinition(sql, new { tenantId, after = last, page = PageSize }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
            foreach (var link in page)
            {
                if (link.Seq != expected)
                {
                    (brokenAt, message) = (expected, $"Expected sequence {expected}, found {link.Seq}: an event is missing.");
                    break;
                }

                if (!link.PrevHash.AsSpan().SequenceEqual(previous))
                {
                    (brokenAt, message) = (link.Seq, "The previous-hash link does not match the preceding event.");
                    break;
                }

                if (!Link(previous, link.Canonical).AsSpan().SequenceEqual(link.Hash))
                {
                    (brokenAt, message) = (link.Seq, "The stored hash does not match the recomputed hash: the event was altered.");
                    break;
                }

                if (anchor is not null && link.Seq == anchor.Seq)
                {
                    hashAtAnchor = link.Hash;
                }

                previous = link.Hash;
                lastHash = link.Hash;
                last = link.Seq;
                expected++;
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        string status;
        if (brokenAt is not null)
        {
            status = VerificationStatus.Broken;
        }
        else if (last == 0)
        {
            status = head is null || head.Seq == 0 ? VerificationStatus.Empty : VerificationStatus.Truncated;
            message = status == VerificationStatus.Truncated ? $"The chain head records {head!.Seq} events but none exist." : null;
        }
        else if (head is null || head.Seq != last || !string.Equals(head.HeadHash, Convert.ToHexStringLower(lastHash), StringComparison.Ordinal))
        {
            status = VerificationStatus.Truncated;
            message = head is null ? "The chain head is missing." : $"The chain head records sequence {head.Seq} but the events end at {last}: events were removed or the head was altered.";
        }
        else
        {
            status = VerificationStatus.Ok;
        }

        bool? anchorMatched = null;
        if (anchor is not null)
        {
            var external = await store.ReadAsync(anchor.Reference, cancellationToken);
            anchorMatched = hashAtAnchor is not null
                && string.Equals(Convert.ToHexStringLower(hashAtAnchor), anchor.HeadHash, StringComparison.Ordinal)
                && external is not null
                && external.Seq == anchor.Seq
                && string.Equals(external.HeadHash, anchor.HeadHash, StringComparison.Ordinal);
            if (status == VerificationStatus.Ok && anchorMatched == false)
            {
                status = VerificationStatus.AnchorMismatch;
                message = external is null
                    ? $"The anchor at sequence {anchor.Seq} cannot be read back from the {anchor.Store} store."
                    : $"The hash at sequence {anchor.Seq} differs from the anchored head: the chain was rewritten.";
            }
        }

        stopwatch.Stop();
        var verification = new ChainVerification(Uuid7.New(), chain, tenantId, clock.UtcNow, last == 0 ? 0 : 1, last, status, brokenAt, message, anchor?.Seq, anchorMatched,
            (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue), head?.HeadHash ?? string.Empty);

        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO control.aud_verifications (id, chain, tenant_id, verified_at, from_seq, to_seq, status, first_broken_seq, message, anchor_seq, anchor_matched, duration_ms)
            VALUES (@id, @chain, @tenantId, @verifiedAt, @fromSeq, @toSeq, @status, @firstBrokenSeq, @message, @anchorSeq, @anchorMatched, @durationMs)
            """, new
        {
            id = verification.Id,
            chain,
            tenantId,
            verifiedAt = verification.VerifiedAt,
            fromSeq = verification.FromSeq,
            toSeq = verification.ToSeq,
            status,
            firstBrokenSeq = brokenAt,
            message,
            anchorSeq = anchor?.Seq,
            anchorMatched,
            durationMs = verification.DurationMs,
        }, uow.Transaction, cancellationToken: cancellationToken));

        return verification;
    }

    private async Task<IReadOnlyList<ChainVerification>> ListAsync(string chain, Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<VerificationRow>(new CommandDefinition("""
            SELECT id, chain, tenant_id, verified_at, from_seq, to_seq, status, first_broken_seq, message, anchor_seq, anchor_matched, duration_ms
            FROM control.aud_verifications WHERE chain = @chain AND tenant_id IS NOT DISTINCT FROM @tenantId ORDER BY verified_at DESC LIMIT @limit
            """, new { chain, tenantId, limit = Math.Clamp(limit, 1, 500) }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(static r => new ChainVerification(r.Id, r.Chain, r.TenantId, r.VerifiedAt, r.FromSeq, r.ToSeq, r.Status, r.FirstBrokenSeq, r.Message, r.AnchorSeq, r.AnchorMatched, r.DurationMs, string.Empty)).ToList();
    }

    /// <summary>hash = SHA-256(prev_hash || UTF-8(canonical)); the same rule the database trigger applies.</summary>
    public static byte[] Link(ReadOnlySpan<byte> previousHash, string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var text = Encoding.UTF8.GetBytes(canonical);
        var input = new byte[previousHash.Length + text.Length];
        previousHash.CopyTo(input);
        text.CopyTo(input, previousHash.Length);
        return SHA256.HashData(input);
    }
}

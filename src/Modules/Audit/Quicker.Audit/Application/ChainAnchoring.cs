using Dapper;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Audit.Application;

public sealed record ChainHead(long Seq, string HeadHash, DateTimeOffset UpdatedAt);

public sealed record AuditAnchor(Guid Id, string Chain, Guid? TenantId, long Seq, string HeadHash, DateTimeOffset AnchoredAt, string Store, string Reference, string Receipt);

/// <summary>Writes chain heads to the anchor store and remembers where (ADR-0015).</summary>
public sealed class ChainAnchoring(IUnitOfWorkAccessor unitOfWork, IClock clock, IAuditAnchorStore store)
{
    private sealed class HeadRow
    {
        public long Seq { get; set; }

        public byte[] HeadHash { get; set; } = [];

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class AnchorRow
    {
        public Guid Id { get; set; }

        public string Chain { get; set; } = string.Empty;

        public Guid? TenantId { get; set; }

        public long Seq { get; set; }

        public byte[] HeadHash { get; set; } = [];

        public DateTimeOffset AnchoredAt { get; set; }

        public string Store { get; set; } = string.Empty;

        public string Reference { get; set; } = string.Empty;

        public string Receipt { get; set; } = string.Empty;
    }

    private const string AnchorColumns = "id, chain, tenant_id, seq, head_hash, anchored_at, store, reference, receipt";

    private Guid TenantId => unitOfWork.Current.Context.TenantId.Value;

    public Task<ChainHead?> TenantHeadAsync(CancellationToken cancellationToken) => HeadAsync(ChainKind.Tenant, TenantId, cancellationToken);

    public Task<ChainHead?> PlatformHeadAsync(CancellationToken cancellationToken) => HeadAsync(ChainKind.Platform, null, cancellationToken);

    public Task<AuditAnchor?> AnchorTenantAsync(CancellationToken cancellationToken) => AnchorAsync(ChainKind.Tenant, TenantId, cancellationToken);

    public Task<AuditAnchor?> AnchorPlatformAsync(CancellationToken cancellationToken) => AnchorAsync(ChainKind.Platform, null, cancellationToken);

    public Task<IReadOnlyList<AuditAnchor>> ListTenantAnchorsAsync(int limit, CancellationToken cancellationToken) => ListAsync(ChainKind.Tenant, TenantId, limit, cancellationToken);

    public Task<IReadOnlyList<AuditAnchor>> ListPlatformAnchorsAsync(int limit, CancellationToken cancellationToken) => ListAsync(ChainKind.Platform, null, limit, cancellationToken);

    /// <summary>The newest anchor of a chain, or null when it was never anchored.</summary>
    internal async Task<AuditAnchor?> LatestAsync(string chain, Guid? tenantId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleOrDefaultAsync<AnchorRow>(new CommandDefinition(
            $"SELECT {AnchorColumns} FROM control.aud_anchors WHERE chain = @chain AND tenant_id IS NOT DISTINCT FROM @tenantId ORDER BY seq DESC LIMIT 1",
            new { chain, tenantId }, uow.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    internal async Task<ChainHead?> HeadAsync(string chain, Guid? tenantId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var sql = chain == ChainKind.Tenant
            ? "SELECT seq, head_hash, updated_at FROM app.aud_chain_heads WHERE tenant_id = @tenantId"
            : "SELECT seq, head_hash, updated_at FROM control.aud_platform_chain_head";
        var row = await uow.Connection.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(sql, new { tenantId }, uow.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : new ChainHead(row.Seq, Convert.ToHexStringLower(row.HeadHash), row.UpdatedAt);
    }

    private async Task<AuditAnchor?> AnchorAsync(string chain, Guid? tenantId, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(chain, tenantId, cancellationToken);
        if (head is null || head.Seq == 0)
        {
            return null;
        }

        var latest = await LatestAsync(chain, tenantId, cancellationToken);
        if (latest is not null && latest.Seq == head.Seq)
        {
            return latest; // nothing new since the last anchor
        }

        var record = new AnchorRecord(chain, tenantId, head.Seq, head.HeadHash, clock.UtcNow);
        var receipt = await store.WriteAsync(record, cancellationToken);

        var uow = unitOfWork.Current;
        var id = Uuid7.New();
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO control.aud_anchors (id, chain, tenant_id, seq, head_hash, anchored_at, store, reference, receipt)
            VALUES (@id, @chain, @tenantId, @seq, decode(@headHash, 'hex'), @anchoredAt, @store, @reference, @receipt)
            """, new
        {
            id,
            chain,
            tenantId,
            seq = head.Seq,
            headHash = head.HeadHash,
            anchoredAt = record.AnchoredAt,
            store = receipt.Store,
            reference = receipt.Reference,
            receipt = receipt.Receipt,
        }, uow.Transaction, cancellationToken: cancellationToken));

        return new AuditAnchor(id, chain, tenantId, head.Seq, head.HeadHash, record.AnchoredAt, receipt.Store, receipt.Reference, receipt.Receipt);
    }

    private async Task<IReadOnlyList<AuditAnchor>> ListAsync(string chain, Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<AnchorRow>(new CommandDefinition(
            $"SELECT {AnchorColumns} FROM control.aud_anchors WHERE chain = @chain AND tenant_id IS NOT DISTINCT FROM @tenantId ORDER BY seq DESC LIMIT @limit",
            new { chain, tenantId, limit = Math.Clamp(limit, 1, 500) }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    private static AuditAnchor Map(AnchorRow row) =>
        new(row.Id, row.Chain, row.TenantId, row.Seq, Convert.ToHexStringLower(row.HeadHash), row.AnchoredAt, row.Store, row.Reference, row.Receipt);
}

public static class ChainKind
{
    public const string Tenant = "tenant";
    public const string Platform = "platform";
}

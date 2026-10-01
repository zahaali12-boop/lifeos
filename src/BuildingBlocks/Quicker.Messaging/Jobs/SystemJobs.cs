using Dapper;
using Npgsql;
using Quicker.Messaging.Outbox;

namespace Quicker.Messaging.Jobs;

public sealed record OutboxArchivePayload(int OlderThanDays = 30);

/// <summary>Removes published outbox messages (and their inbox records) older than the retention.</summary>
public sealed class OutboxArchiveJob(OutboxAdmin outbox) : IJobHandler<OutboxArchivePayload>
{
    public static string JobType => "ops.outbox_archive";

    public async Task<object?> ExecuteAsync(OutboxArchivePayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var removed = await outbox.ArchiveAsync(Math.Max(1, payload.OlderThanDays), cancellationToken);
        return new { removed };
    }
}

public sealed record IdempotencySweepPayload;

/// <summary>Drops expired idempotency keys (ADR-0009: keys live 24 hours).</summary>
public sealed class IdempotencySweepJob(NpgsqlDataSource dataSource) : IJobHandler<IdempotencySweepPayload>
{
    public static string JobType => "ops.idempotency_sweep";

    public async Task<object?> ExecuteAsync(IdempotencySweepPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var removed = await connection.ExecuteAsync(new CommandDefinition("DELETE FROM ops.idempotency_keys WHERE expires_at < now()", cancellationToken: cancellationToken));
        return new { removed };
    }
}

using System.Data;
using Npgsql;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;

namespace Quicker.Persistence;

/// <summary>
/// One database transaction per request or job, opened under the application role with the tenant session applied.
/// Modules receive the connection and transaction; nothing runs outside a unit of work.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    NpgsqlConnection Connection { get; }

    NpgsqlTransaction Transaction { get; }

    TenantContext Context { get; }

    /// <summary>
    /// Moves the transaction into a tenant (sign-up, SSO, invitations start anonymous and continue inside the new
    /// tenant). Re-applies the session variables so RLS and audit see the new tenant and actor from here on.
    /// <paramref name="actorDisplay"/> names the actor for the audit log (an email); the user id when omitted.
    /// </summary>
    Task SwitchTenantAsync(TenantId tenantId, UserId? userId = null, MembershipId? membershipId = null, string? actorDisplay = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// When true, the host commits this unit of work even though the request ends in an error response. Security
    /// bookkeeping sets it (failed-login counters, lockouts, refresh-token reuse revocation, audit of refusals) so a
    /// refusal can never roll back its own evidence. Services set it only when every write so far is safe to keep.
    /// </summary>
    bool CommitOnFailure { get; set; }

    /// <summary>
    /// Registers work that runs inside the transaction immediately before it commits (audit flush, outbox append).
    /// Callbacks run in registration order; a failure aborts the commit and the transaction rolls back.
    /// </summary>
    void BeforeCommit(Func<CancellationToken, Task> callback);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(TenantContext context, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default);
}

public sealed class UnitOfWorkFactory(NpgsqlDataSource appDataSource, DbOptions options) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(TenantContext context, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = await appDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            await TenantSession.ApplyAsync(connection, transaction, context, options, cancellationToken);
            return new UnitOfWork(connection, transaction, context, options);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantContext context, DbOptions options) : IUnitOfWork
    {
        private readonly List<Func<CancellationToken, Task>> _beforeCommit = [];
        private bool _completed;

        public NpgsqlConnection Connection => connection;

        public NpgsqlTransaction Transaction => transaction;

        public TenantContext Context { get; private set; } = context;

        public bool CommitOnFailure { get; set; }

        public async Task SwitchTenantAsync(TenantId tenantId, UserId? userId = null, MembershipId? membershipId = null, string? actorDisplay = null, CancellationToken cancellationToken = default)
        {
            var actorType = userId is null ? TenantContext.ActorSystem : TenantContext.ActorUser;
            Context = Context with
            {
                TenantId = tenantId,
                UserId = userId ?? Context.UserId,
                MembershipId = membershipId ?? Context.MembershipId,
                ActorType = actorType,
                ActorDisplay = actorDisplay ?? userId?.Value.ToString() ?? Context.ActorDisplay,
            };
            await TenantSession.ApplyAsync(connection, transaction, Context, options, cancellationToken);
        }

        public void BeforeCommit(Func<CancellationToken, Task> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _beforeCommit.Add(callback);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            // A callback may register another (an audit event written by the outbox flush, say): drain until empty.
            while (_beforeCommit.Count > 0)
            {
                var pending = _beforeCommit.ToArray();
                _beforeCommit.Clear();
                foreach (var callback in pending)
                {
                    await callback(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await transaction.RollbackAsync(cancellationToken);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch (InvalidOperationException)
                {
                    // Transaction already completed by the server (for example after a serialization failure).
                }
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

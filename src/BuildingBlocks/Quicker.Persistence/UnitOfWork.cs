using System.Data;
using Npgsql;
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
            return new UnitOfWork(connection, transaction, context);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantContext context) : IUnitOfWork
    {
        private bool _completed;

        public NpgsqlConnection Connection => connection;

        public NpgsqlTransaction Transaction => transaction;

        public TenantContext Context => context;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
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

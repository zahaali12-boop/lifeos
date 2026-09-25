using System.Data;
using Dapper;
using Npgsql;
using Quicker.Kernel.Tenancy;

namespace Quicker.Persistence;

/// <summary>
/// Applies a <see cref="TenantContext"/> to an open transaction through SET LOCAL session variables. Row-level
/// security policies read app.tenant_id; audit and defaults read app.user_id and app.request_id. SET LOCAL is
/// transaction-scoped, so a pooled connection never leaks a tenant to the next user.
/// </summary>
public static class TenantSession
{
    public static async Task ApplyAsync(NpgsqlConnection connection, IDbTransaction transaction, TenantContext context, DbOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        // set_config with is_local = true is the parameterisable form of SET LOCAL.
        const string sql = """
            SELECT set_config('app.tenant_id', @tenant, true),
                   set_config('app.user_id', @user, true),
                   set_config('app.membership_id', @membership, true),
                   set_config('app.request_id', @request, true),
                   set_config('app.actor', @actor, true),
                   set_config('statement_timeout', @statementTimeout, true),
                   set_config('lock_timeout', @lockTimeout, true)
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            // An anonymous context (Guid.Empty) sets an empty tenant id: RLS then yields no rows for app tables.
            tenant = context.TenantId.Value == Guid.Empty ? string.Empty : context.TenantId.Value.ToString(),
            user = context.UserId?.Value.ToString() ?? string.Empty,
            membership = context.MembershipId?.Value.ToString() ?? string.Empty,
            request = context.RequestId,
            actor = $"{context.ActorType}:{context.ActorDisplay}",
            statementTimeout = $"{options.StatementTimeoutSeconds}s",
            lockTimeout = $"{options.LockTimeoutSeconds}s",
        }, transaction, cancellationToken: cancellationToken));
    }
}

using System.Text.Json;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Tenancy;

namespace Quicker.Api;

/// <summary>
/// Interim audit sink for M1.4: writes structured log lines. The Audit module (M1.5) replaces it with the
/// hash-chained, append-only <c>aud_events</c> table; callers do not change.
/// </summary>
public sealed class LoggingAuditSink(ILogger<LoggingAuditSink> logger, ITenantContextAccessor tenant) : IAuditSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var context = tenant.Current;
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return Task.CompletedTask;
        }

        logger.LogInformation("audit tenant={Tenant} actor={Actor} {EntityType}/{EntityId} ({Display}) {Action} reason={Reason} before={Before} after={After}",
            context?.TenantId, context?.ActorDisplay, entry.EntityType, entry.EntityId, entry.EntityDisplay, entry.Action, entry.Reason,
            entry.Before is null ? null : JsonSerializer.Serialize(entry.Before, Json),
            entry.After is null ? null : JsonSerializer.Serialize(entry.After, Json));
        return Task.CompletedTask;
    }
}

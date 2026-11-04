using Quicker.Identity.Contracts;

namespace Quicker.Audit;

public static class AuditPermissions
{
    public const string EventRead = "audit.event.read";
    public const string EventExport = "audit.event.export";
    public const string ChainVerify = "audit.chain.verify";
    public const string ChainAnchor = "audit.chain.anchor";

    public static readonly PermissionDefinition[] All =
    [
        new(EventRead, "audit", "Read the audit log: record timelines and the explorer"),
        new(EventExport, "audit", "Export audit events", IsSensitive: true),
        new(ChainVerify, "audit", "Verify the audit chain and read its status"),
        new(ChainAnchor, "audit", "Anchor the audit chain head to external storage"),
    ];
}

namespace Quicker.Audit.Contracts;

/// <summary>Well-known audit actions. Modules may add their own strings; these keep the common ones consistent.</summary>
public static class AuditActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string StateChanged = "state_changed";
    public const string Posted = "posted";
    public const string Reversed = "reversed";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Override = "override";
    public const string Login = "login";
    public const string LoginFailed = "login_failed";
    public const string Logout = "logout";
    public const string MfaEnrolled = "mfa_enrolled";
    public const string MfaRemoved = "mfa_removed";
    public const string PasswordChanged = "password_changed";
    public const string PermissionChanged = "permission_changed";
    public const string Exported = "exported";
    public const string Printed = "printed";
    public const string ViewedSensitive = "viewed_sensitive";
}

/// <summary>
/// One audit event: who did what to which record, with before/after values and a reason when policy requires one.
/// Written inside the caller's unit of work so it commits with the change it describes.
/// </summary>
public sealed record AuditEntry(
    string EntityType,
    Guid EntityId,
    string EntityDisplay,
    string Action,
    object? Before = null,
    object? After = null,
    string? Reason = null,
    Guid? CompanyId = null,
    IReadOnlyDictionary<string, object?>? Details = null);

public interface IAuditSink
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

using Quicker.Identity.Contracts;

namespace Quicker.Collaboration;

public static class CollaborationPermissions
{
    public const string NotificationAnnounce = "collaboration.notification.announce";
    public const string AttachmentRead = "collaboration.attachment.read";
    public const string AttachmentManage = "collaboration.attachment.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(NotificationAnnounce, "collaboration", "Send an announcement to every member"),
        new(AttachmentRead, "collaboration", "See and download attachments"),
        new(AttachmentManage, "collaboration", "Upload and remove attachments"),
    ];
}

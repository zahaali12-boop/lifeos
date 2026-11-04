using Quicker.Identity.Contracts;

namespace Quicker.Collaboration;

public static class CollaborationPermissions
{
    public const string NotificationAnnounce = "collaboration.notification.announce";
    public const string AttachmentRead = "collaboration.attachment.read";
    public const string AttachmentManage = "collaboration.attachment.manage";
    public const string CommentRead = "collaboration.comment.read";
    public const string CommentWrite = "collaboration.comment.write";
    public const string CommentManage = "collaboration.comment.manage";
    public const string ActivityRead = "collaboration.activity.read";
    public const string LinkRead = "collaboration.link.read";
    public const string LinkManage = "collaboration.link.manage";
    public const string ViewManage = "collaboration.view.manage";
    public const string CustomFieldManage = "collaboration.custom_field.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(NotificationAnnounce, "collaboration", "Send an announcement to every member"),
        new(AttachmentRead, "collaboration", "See and download attachments"),
        new(AttachmentManage, "collaboration", "Upload and remove attachments"),
        new(CommentRead, "collaboration", "Read comments on records"),
        new(CommentWrite, "collaboration", "Comment on records and edit or remove own comments"),
        new(CommentManage, "collaboration", "Edit or remove anyone's comments"),
        new(ActivityRead, "collaboration", "See a record's activity timeline"),
        new(LinkRead, "collaboration", "See links between documents"),
        new(LinkManage, "collaboration", "Link and unlink documents"),
        new(ViewManage, "collaboration", "Edit or remove views shared by others"),
        new(CustomFieldManage, "collaboration", "Define custom fields and their indexes", IsSensitive: true),
    ];
}

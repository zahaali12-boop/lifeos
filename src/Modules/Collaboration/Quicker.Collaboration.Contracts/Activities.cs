using Quicker.Kernel.Text;

namespace Quicker.Collaboration.Contracts;

/// <summary>Entity-type names shared by comments, activities, links, views and custom fields: dotted lower-case, up to 64 characters.</summary>
public static class EntityTypes
{
    public static bool IsValid(string? type) =>
        !string.IsNullOrEmpty(type) && type.Length <= 64 && type[0] != '.' && type[^1] != '.' &&
        type.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.');
}

public static class ActivityKinds
{
    public const string CommentAdded = "comment.added";
    public const string CommentEdited = "comment.edited";
    public const string CommentDeleted = "comment.deleted";
    public const string AttachmentAdded = "attachment.added";
    public const string AttachmentRemoved = "attachment.removed";
    public const string LinkAdded = "link.added";
    public const string LinkRemoved = "link.removed";
    public const string StatusChanged = "status.changed";

    public static bool IsValid(string? kind) => NotificationKinds.IsValid(kind);
}

/// <summary>One line of a record's timeline: who did what, summarised bilingually, with structured data for the UI.</summary>
public sealed record ActivityEntry(string EntityType, Guid EntityId, string Kind, LocalizedText Summary, object? Data = null);

/// <summary>Writes activities inside the caller's unit of work; every module records its record-level events here.</summary>
public interface IActivityLog
{
    Task RecordAsync(ActivityEntry entry, CancellationToken cancellationToken = default);
}

using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Collaboration.Application;

public sealed record AddCommentRequest(string EntityType, Guid EntityId, string Body, IReadOnlyList<Guid>? Mentions = null, Guid? ParentId = null);

public sealed record EditCommentRequest(string Body, IReadOnlyList<Guid>? Mentions = null);

public sealed record CommentView(Guid Id, string EntityType, Guid EntityId, Guid AuthorMembershipId, string AuthorName, string Body, IReadOnlyList<Guid> Mentions, Guid? ParentId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt);

/// <summary>A member a comment can mention: the name only, so commenting does not reveal the workspace's email addresses.</summary>
public sealed record MentionableMember(Guid MembershipId, string DisplayName);

/// <summary>Comments on records with @mentions: each comment is an activity, and every mentioned member is notified.</summary>
public sealed class CommentService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, IMemberDirectory members, IActivityLog activities, INotifier notifier, IClock clock, RecordAccess access)
{
    public const int MaxBodyLength = 4000;
    public const int MaxMentions = 20;

    private Guid? Me => unitOfWork.Current.Context.MembershipId?.Value;

    public async Task<Result<IReadOnlyList<CommentView>>> ListAsync(string? entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var type = entityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type) || entityId == Guid.Empty)
        {
            return Error.Validation("comment.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        if (!access.MayRead(type))
        {
            return access.Refusal(type);
        }

        var rows = await db.Comments.Where(c => c.EntityType == type && c.EntityId == entityId).OrderBy(static c => c.Id).ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.Select(static c => c.AuthorMembershipId), cancellationToken);
        return rows.Select(c => Map(c, names)).ToList();
    }

    /// <summary>The active members, by name: whom a comment can mention (the member list itself needs <c>identity.user.read</c>).</summary>
    public async Task<IReadOnlyList<MentionableMember>> MentionableAsync(CancellationToken cancellationToken) =>
        (await members.ListActiveAsync(cancellationToken))
            .OrderBy(static m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static m => new MentionableMember(m.MembershipId.Value, m.DisplayName))
            .ToList();

    public async Task<Result<CommentView>> AddAsync(AddCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Me is not { } me)
        {
            return Error.Forbidden("comment.membership_required", "Only members comment.");
        }

        var type = request.EntityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type) || request.EntityId == Guid.Empty)
        {
            return Error.Validation("comment.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        if (!access.MayRead(type))
        {
            return access.Refusal(type);
        }

        var body = Body(request.Body);
        if (body.IsFailure)
        {
            return body.Error!;
        }

        var mentions = await MentionsAsync(request.Mentions, cancellationToken);
        if (mentions.IsFailure)
        {
            return mentions.Error!;
        }

        if (request.ParentId is { } parentId && !await db.Comments.AnyAsync(c => c.Id == parentId && c.EntityType == type && c.EntityId == request.EntityId, cancellationToken))
        {
            return Error.NotFound("comment", parentId);
        }

        var author = await members.FindAsync(new MembershipId(me), cancellationToken);
        var comment = new Comment
        {
            Id = Guid.CreateVersion7(),
            EntityType = type,
            EntityId = request.EntityId,
            AuthorMembershipId = me,
            Body = body.Value,
            Mentions = mentions.Value,
            ParentId = request.ParentId,
            CreatedAt = clock.UtcNow,
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);

        var name = author?.DisplayName ?? principal.Required.DisplayName;
        await activities.RecordAsync(new ActivityEntry(type, request.EntityId, ActivityKinds.CommentAdded, LocalizedText.Bilingual($"{name} commented", $"علّق {name}"), new { commentId = comment.Id, excerpt = Excerpt(comment.Body) }), cancellationToken);
        if (mentions.Value.Length > 0)
        {
            await notifier.NotifyAsync(new NotificationRequest(NotificationKinds.Mention, LocalizedText.Bilingual($"{name} mentioned you", $"أشار إليك {name}"), LocalizedText.Bilingual(Excerpt(comment.Body), Excerpt(comment.Body)),
                mentions.Value.Select(static m => new MembershipId(m)).ToList(), EntityType: type, EntityId: request.EntityId, Data: new { commentId = comment.Id }), cancellationToken);
        }

        return Map(comment, new Dictionary<Guid, string> { [me] = name });
    }

    public async Task<Result<CommentView>> EditAsync(Guid id, EditCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, cancellationToken);
        if (comment is null || !access.MayRead(comment.EntityType))
        {
            return Error.NotFound("comment", id);
        }

        var allowed = MayChange(comment);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var body = Body(request.Body);
        if (body.IsFailure)
        {
            return body.Error!;
        }

        var mentions = await MentionsAsync(request.Mentions, cancellationToken);
        if (mentions.IsFailure)
        {
            return mentions.Error!;
        }

        comment.Body = body.Value;
        comment.Mentions = mentions.Value;
        comment.EditedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await activities.RecordAsync(new ActivityEntry(comment.EntityType, comment.EntityId, ActivityKinds.CommentEdited, LocalizedText.Bilingual("Comment edited", "عُدّل تعليق"), new { commentId = comment.Id }), cancellationToken);
        return Map(comment, await NamesAsync([comment.AuthorMembershipId], cancellationToken));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, cancellationToken);
        if (comment is null || !access.MayRead(comment.EntityType))
        {
            return Error.NotFound("comment", id);
        }

        var allowed = MayChange(comment);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        comment.Body = string.Empty;
        comment.Mentions = [];
        comment.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await activities.RecordAsync(new ActivityEntry(comment.EntityType, comment.EntityId, ActivityKinds.CommentDeleted, LocalizedText.Bilingual("Comment removed", "حُذف تعليق"), new { commentId = comment.Id }), cancellationToken);
        return Result.Success();
    }

    private Result MayChange(Comment comment) =>
        comment.AuthorMembershipId == Me || principal.Required.Has(CollaborationPermissions.CommentManage)
            ? Result.Success()
            : Error.Forbidden("comment.not_author", "Only the author, or a member with collaboration.comment.manage, can change this comment.");

    private static Result<string> Body(string? raw)
    {
        var body = raw?.Trim() ?? string.Empty;
        return body.Length is 0 or > MaxBodyLength
            ? Error.Validation("comment.body_invalid", $"A comment has between 1 and {MaxBodyLength} characters.")
            : body;
    }

    private async Task<Result<Guid[]>> MentionsAsync(IReadOnlyList<Guid>? mentions, CancellationToken cancellationToken)
    {
        var ids = (mentions ?? []).Distinct().ToArray();
        if (ids.Length > MaxMentions)
        {
            return Error.Validation("comment.too_many_mentions", $"At most {MaxMentions} members can be mentioned.");
        }

        foreach (var id in ids)
        {
            if (await members.FindAsync(new MembershipId(id), cancellationToken) is not { IsActive: true })
            {
                return Error.Validation("comment.mention_unknown", "Every mention must be an active member of this workspace.").WithWhy(("membershipId", id));
            }
        }

        return ids;
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> membershipIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var id in membershipIds.Distinct())
        {
            names[id] = (await members.FindAsync(new MembershipId(id), cancellationToken))?.DisplayName ?? "Former member";
        }

        return names;
    }

    private static string Excerpt(string body) => body.Length <= 140 ? body : body[..140] + "…";

    private static CommentView Map(Comment c, IReadOnlyDictionary<Guid, string> names) =>
        new(c.Id, c.EntityType, c.EntityId, c.AuthorMembershipId, names.GetValueOrDefault(c.AuthorMembershipId, "Former member"), c.Body, c.Mentions, c.ParentId, c.CreatedAt, c.EditedAt, c.DeletedAt);
}

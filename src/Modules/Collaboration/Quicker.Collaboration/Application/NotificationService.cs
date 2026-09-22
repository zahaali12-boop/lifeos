using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Collaboration.Application;

public sealed record NotificationView(Guid Id, string Kind, IReadOnlyDictionary<string, string> Title, IReadOnlyDictionary<string, string> Body, string? Link, string? EntityType, Guid? EntityId, JsonElement Data, Guid? ActorMembershipId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record PreferenceInput(string Kind, bool InApp, bool Email);

public sealed record PreferenceView(string Kind, bool InApp, bool Email);

public sealed record AnnounceRequest(IReadOnlyDictionary<string, string> Title, IReadOnlyDictionary<string, string>? Body = null, string? Link = null);

/// <summary>In-app notifications of the current member, channel preferences, and the notifier other modules call.</summary>
public sealed class NotificationService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, IMemberDirectory members, IJobQueue jobs, IClock clock) : INotifier
{
    public const string EmailJobType = "collaboration.email.send";

    /// <summary>Retries with backoff (1 s → 512 s) for about a quarter of an hour before the email is marked failed.</summary>
    public const int EmailMaxAttempts = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid? CurrentMembership => unitOfWork.Current.Context.MembershipId?.Value;

    // ------------------------------------------------------------------ notifier

    public async Task<NotificationOutcome> NotifyAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NotificationKinds.IsValid(request.Kind))
        {
            throw new ArgumentException($"'{request.Kind}' is not a notification kind (dotted lower-case name).", nameof(request));
        }

        if (request.Title.IsEmpty)
        {
            throw new ArgumentException("A notification needs a title.", nameof(request));
        }

        var recipients = request.Recipients.Select(static r => r.Value).Distinct().ToList();
        if (recipients.Count == 0)
        {
            return new NotificationOutcome(0, 0);
        }

        var preferences = await db.Preferences.Where(p => recipients.Contains(p.MembershipId)).ToListAsync(cancellationToken);
        var data = request.Data is null ? "{}" : JsonSerializer.Serialize(request.Data, Json);
        var actor = CurrentMembership;
        var inApp = 0;
        var emails = 0;
        foreach (var membershipId in recipients)
        {
            var (wantsInApp, wantsEmail) = Resolve(preferences, membershipId, request.Kind);
            Guid? notificationId = null;
            if (wantsInApp)
            {
                var notification = new Notification
                {
                    Id = Guid.CreateVersion7(),
                    MembershipId = membershipId,
                    Kind = request.Kind,
                    Title = request.Title,
                    Body = request.Body,
                    Link = request.Link,
                    EntityType = request.EntityType,
                    EntityId = request.EntityId,
                    Data = data,
                    ActorMembershipId = actor,
                    CreatedAt = clock.UtcNow,
                };
                db.Notifications.Add(notification);
                notificationId = notification.Id;
                inApp++;
            }

            if (!wantsEmail)
            {
                continue;
            }

            var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
            if (member is not { IsActive: true })
            {
                continue;
            }

            var body = request.Body.Resolve(member.Locale);
            var email = new EmailLog
            {
                Id = Guid.CreateVersion7(),
                NotificationId = notificationId,
                MembershipId = membershipId,
                ToAddress = member.Email,
                Subject = request.Title.Resolve(member.Locale),
                TextBody = request.Link is null ? body : body + "\n\n" + request.Link,
                CreatedAt = clock.UtcNow,
            };
            email.JobId = await jobs.EnqueueAsync(new JobRequest(EmailJobType, new EmailSendPayload(email.Id), MaxAttempts: EmailMaxAttempts), cancellationToken);
            db.Emails.Add(email);
            emails++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new NotificationOutcome(inApp, emails);
    }

    /// <summary>The kind's own row, else the member's <c>*</c> row, else both channels on.</summary>
    private static (bool InApp, bool Email) Resolve(List<NotificationPreference> preferences, Guid membershipId, string kind)
    {
        var mine = preferences.Where(p => p.MembershipId == membershipId).ToList();
        var row = mine.Find(p => string.Equals(p.Kind, kind, StringComparison.Ordinal)) ?? mine.Find(static p => p.Kind == NotificationKinds.Any);
        return row is null ? (true, true) : (row.InApp, row.Email);
    }

    // ------------------------------------------------------------------ my notifications

    public async Task<Result<Page<NotificationView>>> ListMineAsync(bool unreadOnly, PageRequest page, CancellationToken cancellationToken)
    {
        if (CurrentMembership is not { } me)
        {
            return NoMembership();
        }

        var query = db.Notifications.Where(n => n.MembershipId == me);
        if (unreadOnly)
        {
            query = query.Where(static n => n.ReadAt == null);
        }

        // Ids are version-7 GUIDs: their byte order is creation order, which makes them the paging key.
        var paged = await KeysetPaging.ByIdDescendingAsync(query, static n => n.Id, page, cancellationToken);
        return paged.IsSuccess ? paged.Value.Map(Map) : paged.Error!;
    }

    public async Task<Result<int>> UnreadCountAsync(CancellationToken cancellationToken) =>
        CurrentMembership is { } me ? await db.Notifications.CountAsync(n => n.MembershipId == me && n.ReadAt == null, cancellationToken) : NoMembership();

    public async Task<Result<NotificationView>> MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (CurrentMembership is not { } me)
        {
            return NoMembership();
        }

        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.MembershipId == me && n.Id == id, cancellationToken);
        if (notification is null)
        {
            return Error.NotFound("notification", id);
        }

        notification.ReadAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(notification);
    }

    public async Task<Result<int>> MarkAllReadAsync(CancellationToken cancellationToken)
    {
        if (CurrentMembership is not { } me)
        {
            return NoMembership();
        }

        var now = clock.UtcNow;
        return await db.Notifications.Where(n => n.MembershipId == me && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(static n => n.ReadAt, now), cancellationToken);
    }

    // ------------------------------------------------------------------ preferences

    public async Task<Result<IReadOnlyList<PreferenceView>>> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        if (CurrentMembership is not { } me)
        {
            return NoMembership();
        }

        var rows = await db.Preferences.Where(p => p.MembershipId == me).OrderBy(static p => p.Kind).ToListAsync(cancellationToken);
        var views = rows.Select(static p => new PreferenceView(p.Kind, p.InApp, p.Email)).ToList();
        if (!views.Exists(static v => v.Kind == NotificationKinds.Any))
        {
            views.Insert(0, new PreferenceView(NotificationKinds.Any, true, true));
        }

        return views;
    }

    /// <summary>Replaces the member's preference rows with the given set (a kind left out returns to the <c>*</c> row or the defaults).</summary>
    public async Task<Result<IReadOnlyList<PreferenceView>>> SetPreferencesAsync(IReadOnlyList<PreferenceInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (CurrentMembership is not { } me)
        {
            return NoMembership();
        }

        var kinds = inputs.Select(static i => i.Kind?.Trim() ?? string.Empty).ToList();
        if (kinds.Any(static k => k != NotificationKinds.Any && !NotificationKinds.IsValid(k)) || kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)
        {
            return Error.Validation("notification.preference_invalid", "Each preference names a distinct kind such as collaboration.mention, or * for the default.");
        }

        var existing = await db.Preferences.Where(p => p.MembershipId == me).ToListAsync(cancellationToken);
        db.Preferences.RemoveRange(existing);
        foreach (var input in inputs)
        {
            db.Preferences.Add(new NotificationPreference { Id = Guid.CreateVersion7(), MembershipId = me, Kind = input.Kind.Trim(), InApp = input.InApp, Email = input.Email, UpdatedAt = clock.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetPreferencesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ announcements

    /// <summary>A <c>system.announcement</c> to every active member.</summary>
    public async Task<Result<NotificationOutcome>> AnnounceAsync(AnnounceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = ToText(request.Title);
        if (title.IsEmpty || title.Values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("announcement.title_required", "An announcement needs a title in at least one language.");
        }

        var recipients = (await members.ListActiveAsync(cancellationToken)).Select(static m => m.MembershipId).ToList();
        return await NotifyAsync(new NotificationRequest(NotificationKinds.Announcement, title, ToText(request.Body), recipients, request.Link), cancellationToken);
    }

    private static LocalizedText ToText(IReadOnlyDictionary<string, string>? values) =>
        values is null ? new LocalizedText() : new LocalizedText(values.Where(static kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null));

    private static Error NoMembership() => Error.Forbidden("notification.membership_required", "Only members have notifications.");

    private static NotificationView Map(Notification n) =>
        new(n.Id, n.Kind, n.Title.Values, n.Body.Values, n.Link, n.EntityType, n.EntityId, JsonDocument.Parse(n.Data).RootElement.Clone(), n.ActorMembershipId, n.CreatedAt, n.ReadAt);
}

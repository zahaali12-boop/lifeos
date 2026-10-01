using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;

namespace Quicker.Collaboration.Contracts;

/// <summary>Notification kinds are dotted lower-case names; members set channel preferences per kind or for all (<c>*</c>).</summary>
public static class NotificationKinds
{
    public const string Any = "*";
    public const string Announcement = "system.announcement";
    public const string Mention = "collaboration.mention";
    public const string Assignment = "collaboration.assignment";
    public const string ApprovalRequested = "workflow.approval_requested";
    public const string JobFinished = "platform.job_finished";

    /// <summary>At least two segments of [a-z0-9_] joined by dots.</summary>
    public static bool IsValid(string? kind)
    {
        if (string.IsNullOrEmpty(kind) || kind.Length > 100)
        {
            return false;
        }

        var parts = kind.Split('.');
        return parts.Length >= 2 && parts.All(static p => p.Length > 0 && p.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'));
    }
}

/// <summary>A notification to a set of members of the current tenant; the title and body are bilingual and resolved per member for email.</summary>
public sealed record NotificationRequest(
    string Kind,
    LocalizedText Title,
    LocalizedText Body,
    IReadOnlyList<MembershipId> Recipients,
    string? Link = null,
    string? EntityType = null,
    Guid? EntityId = null,
    object? Data = null);

/// <summary>How many in-app rows were written and how many emails were queued after preferences.</summary>
public sealed record NotificationOutcome(int InApp, int Emails);

/// <summary>Writes in-app notifications and queues emails inside the caller's unit of work, honouring each recipient's preferences.</summary>
public interface INotifier
{
    Task<NotificationOutcome> NotifyAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

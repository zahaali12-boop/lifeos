using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>
/// Calls, meetings, emails and tasks planned with a partner (optionally about one of its opportunities or with one of
/// its contacts), assigned to a member, then done with an outcome or cancelled; notes are logged as done. A member
/// assigned someone else's activity is notified. Activities of a company are read within the companies the member's
/// customer read permission reaches.
/// </summary>
public sealed class CrmActivityService(
    PartnersDbContext db,
    ICompanyDirectory companies,
    IMemberDirectory members,
    INotifier notifier,
    ICurrentPrincipal principal,
    CustomerService customers,
    IClock clock)
{
    private const int SubjectMaxLength = 200;

    public async Task<IReadOnlyList<CrmActivitySummary>> ListAsync(Guid? partnerId, Guid? opportunityId, string? status, bool? mine, string? due, CancellationToken cancellationToken)
    {
        var query = db.CrmActivities.AsNoTracking().AsQueryable();
        if (customers.ReadableCompanies() is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(a => a.CompanyId == null || ids.Contains(a.CompanyId.Value));
        }

        if (partnerId is { } partner)
        {
            query = query.Where(a => a.PartnerId == partner);
        }

        if (opportunityId is { } opportunity)
        {
            query = query.Where(a => a.OpportunityId == opportunity);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(a => a.Status == s);
        }

        if (mine == true)
        {
            var me = principal.Principal?.MembershipId.Value ?? Guid.Empty;
            query = query.Where(a => a.AssignedMembershipId == me);
        }

        var now = clock.UtcNow;
        switch (due?.Trim().ToLowerInvariant())
        {
            case "overdue":
                query = query.Where(a => a.Status == CrmActivityStatuses.Open && a.DueAt < now);
                break;
            case "upcoming":
                query = query.Where(a => a.Status == CrmActivityStatuses.Open && (a.DueAt == null || a.DueAt >= now));
                break;
        }

        var rows = await query
            .OrderBy(static a => a.Status == CrmActivityStatuses.Open ? 0 : 1)
            .ThenBy(static a => a.DueAt == null)
            .ThenBy(static a => a.DueAt)
            .ThenByDescending(static a => a.CompletedAt)
            .ThenByDescending(static a => a.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);
        return await MapAsync(rows, cancellationToken);
    }

    public async Task<Result<CrmActivitySummary>> SaveAsync(Guid? activityId, SaveCrmActivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CrmActivity? activity = null;
        if (activityId is { } id)
        {
            activity = await db.CrmActivities.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (activity is null || (activity.CompanyId is { } c && !customers.MayManageIn(c)))
            {
                return Error.NotFound("crm_activity", id);
            }

            if (activity.Status != CrmActivityStatuses.Open)
            {
                return Error.Conflict("crm_activity.closed", "A done or cancelled activity is kept as it was.").WithWhy(("status", activity.Status));
            }

            if (activity.PartnerId != request.PartnerId)
            {
                return Error.Validation("crm_activity.partner_locked", "An activity stays with its partner.");
            }
        }

        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.PartnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound(PartnerService.EntityType, request.PartnerId);
        }

        var kind = Validation.OneOf(request.Kind, "crm_activity.kind", CrmActivityKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (activity is not null && (kind.Value == CrmActivityKinds.Note) != (activity.Kind == CrmActivityKinds.Note))
        {
            return Error.Validation("crm_activity.kind_locked", "A planned activity cannot become a note, nor a note a planned activity.");
        }

        var subject = request.Subject?.Trim() ?? string.Empty;
        if (subject.Length is 0 or > SubjectMaxLength)
        {
            return Error.Validation("crm_activity.subject_invalid", $"A subject is 1–{SubjectMaxLength} characters.");
        }

        var companyId = request.CompanyId;
        if (request.OpportunityId is { } opportunityId)
        {
            var opportunity = await db.Opportunities.AsNoTracking().SingleOrDefaultAsync(o => o.Id == opportunityId, cancellationToken);
            if (opportunity is null || opportunity.PartnerId != partner.Id)
            {
                return Error.Validation("crm_activity.opportunity_unknown", "The opportunity is not one of the partner's.").WithWhy(("opportunityId", opportunityId));
            }

            if (companyId is { } given && given != opportunity.CompanyId)
            {
                return Error.Validation("crm_activity.company_mismatch", "The activity belongs to the opportunity's company.").WithWhy(("companyId", given));
            }

            companyId = opportunity.CompanyId;
        }

        if (companyId is { } company && (await companies.FindAsync(new CompanyId(company), cancellationToken) is null || !customers.MayManageIn(company)))
        {
            return Error.NotFound("company", company);
        }

        if (request.ContactId is { } contact && !await db.Contacts.AnyAsync(c => c.Id == contact && c.PartnerId == partner.Id, cancellationToken))
        {
            return Error.Validation("crm_activity.contact_unknown", "The contact is not one of the partner's.").WithWhy(("contactId", contact));
        }

        MemberInfo? assignee = null;
        if (request.AssignedMembershipId is { } assigned)
        {
            assignee = await members.FindAsync(new MembershipId(assigned), cancellationToken);
            if (assignee is null || !assignee.IsActive)
            {
                return Error.Validation("crm_activity.assignee_unknown", "The member is not an active member of this workspace.").WithWhy(("assignedMembershipId", assigned));
            }
        }

        var isNew = activity is null;
        var previousAssignee = activity?.AssignedMembershipId;
        activity ??= new CrmActivity { Id = Guid.CreateVersion7(), PartnerId = partner.Id, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow };
        activity.Kind = kind.Value;
        activity.Subject = subject;
        activity.Body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body.Trim();
        activity.DueAt = kind.Value == CrmActivityKinds.Note ? null : request.DueAt;
        activity.AssignedMembershipId = request.AssignedMembershipId;
        activity.OpportunityId = request.OpportunityId;
        activity.ContactId = request.ContactId;
        activity.CompanyId = companyId;
        activity.UpdatedAt = clock.UtcNow;
        if (isNew && kind.Value == CrmActivityKinds.Note)
        {
            // A note records something that happened: it is logged done.
            activity.Status = CrmActivityStatuses.Done;
            activity.CompletedAt = clock.UtcNow;
            activity.CompletedBy = principal.Principal?.MembershipId.Value;
        }

        if (isNew)
        {
            db.CrmActivities.Add(activity);
        }

        await db.SaveChangesAsync(cancellationToken);

        var me = principal.Principal?.MembershipId.Value;
        if (assignee is not null && assignee.MembershipId.Value != me && assignee.MembershipId.Value != previousAssignee && activity.Status == CrmActivityStatuses.Open)
        {
            await notifier.NotifyAsync(new NotificationRequest(
                NotificationKinds.Assignment,
                LocalizedText.Bilingual($"New {activity.Kind} with {partner.LegalName.Resolve("en")}", $"نشاط جديد مع {partner.LegalName.Resolve("ar")}"),
                LocalizedText.Bilingual(activity.Subject, activity.Subject),
                [assignee.MembershipId],
                $"/customers/{partner.Id}",
                PartnerService.EntityType,
                partner.Id,
                new { activityId = activity.Id }), cancellationToken);
        }

        return (await MapAsync([activity], cancellationToken))[0];
    }

    public async Task<Result<CrmActivitySummary>> CompleteAsync(Guid activityId, CompleteCrmActivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await CloseAsync(activityId, CrmActivityStatuses.Done, request.Outcome, cancellationToken);
    }

    public Task<Result<CrmActivitySummary>> CancelAsync(Guid activityId, CancellationToken cancellationToken) => CloseAsync(activityId, CrmActivityStatuses.Cancelled, null, cancellationToken);

    private async Task<Result<CrmActivitySummary>> CloseAsync(Guid activityId, string status, string? outcome, CancellationToken cancellationToken)
    {
        var activity = await db.CrmActivities.SingleOrDefaultAsync(a => a.Id == activityId, cancellationToken);
        if (activity is null || (activity.CompanyId is { } c && !customers.MayManageIn(c)))
        {
            return Error.NotFound("crm_activity", activityId);
        }

        if (activity.Status != CrmActivityStatuses.Open)
        {
            return Error.Conflict("crm_activity.closed", "The activity is already done or cancelled.").WithWhy(("status", activity.Status));
        }

        activity.Status = status;
        activity.Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
        activity.CompletedAt = clock.UtcNow;
        activity.CompletedBy = principal.Principal?.MembershipId.Value;
        activity.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync([activity], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<CrmActivitySummary>> MapAsync(IReadOnlyList<CrmActivity> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var partnerIds = rows.Select(static a => a.PartnerId).Distinct().ToList();
        var partners = await db.Partners.AsNoTracking().Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(static p => p.Id, cancellationToken);
        var opportunityIds = rows.Where(static a => a.OpportunityId != null).Select(static a => a.OpportunityId!.Value).Distinct().ToList();
        var numbers = await db.Opportunities.AsNoTracking().Where(o => opportunityIds.Contains(o.Id)).ToDictionaryAsync(static o => o.Id, static o => o.Number, cancellationToken);
        var names = rows.Any(static a => a.AssignedMembershipId != null)
            ? (await members.ListActiveAsync(cancellationToken)).ToDictionary(static m => m.MembershipId.Value, static m => m.DisplayName)
            : [];
        var now = clock.UtcNow;
        return rows.Select(a =>
        {
            var partner = partners[a.PartnerId];
            return new CrmActivitySummary(
                a.Id, a.PartnerId, partner.Code, partner.LegalName.Values, a.CompanyId, a.OpportunityId, a.OpportunityId is { } o ? numbers.GetValueOrDefault(o) : null,
                a.ContactId, a.Kind, a.Subject, a.Body, a.DueAt, a.Status == CrmActivityStatuses.Open && a.DueAt is { } dueAt && dueAt < now,
                a.AssignedMembershipId, a.AssignedMembershipId is { } m ? names.GetValueOrDefault(m) : null,
                a.Status, a.Outcome, a.CompletedAt, a.CreatedBy, a.CreatedAt, a.UpdatedAt);
        }).ToList();
    }
}

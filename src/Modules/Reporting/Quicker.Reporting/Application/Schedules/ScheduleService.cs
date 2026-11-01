using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;
using Quicker.Storage;

namespace Quicker.Reporting.Application.Schedules;

/// <summary>
/// Schedules (founder programme item 9, slice 9d; ADR-0034 §4): a saved report or a statement run as its owner on a
/// cron, in CSV, XLSX or PDF, emailed to members and addresses, with every run kept. Each schedule is mirrored by a
/// tenant entry in <c>ops.schedules</c> for the job <c>reporting.run</c>, so the platform scheduler fires it and the
/// platform screens show it like any other schedule; "run now" enqueues the same job at once.
/// </summary>
public sealed class ScheduleService(
    ReportingDbContext db,
    ReportService reports,
    LayoutService layouts,
    ICompanyDirectory companies,
    IMemberDirectory members,
    ICurrentPrincipal principal,
    Scheduler scheduler,
    IJobQueue jobs,
    IObjectStorage storage,
    IUnitOfWorkAccessor unitOfWork,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = "report_schedule";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReportScheduleSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var query = db.Schedules.AsNoTracking().AsQueryable();
        if (!who.IsOwner)
        {
            var mine = who.MembershipId.Value;
            query = query.Where(s => s.OwnerMembershipId == mine);
        }

        if (companyId is { } company)
        {
            query = query.Where(s => s.CompanyId == company);
        }

        var list = await query.OrderBy(static s => s.CreatedAt).ToListAsync(cancellationToken);
        if (list.Count == 0)
        {
            return [];
        }

        var platform = (await scheduler.ListAsync(unitOfWork.Current.Context.TenantId.Value, allTenants: false, cancellationToken)).ToDictionary(static s => s.Id);
        var ids = list.Select(static s => s.Id).ToList();
        var lastRuns = await db.Runs.AsNoTracking().Where(r => ids.Contains(r.ScheduleId))
            .GroupBy(static r => r.ScheduleId)
            .Select(static g => g.OrderByDescending(static r => r.StartedAt).First())
            .ToListAsync(cancellationToken);
        var last = lastRuns.ToDictionary(static r => r.ScheduleId);
        var result = new List<ReportScheduleSummary>();
        foreach (var schedule in list)
        {
            result.Add(await MapAsync(schedule, who, platform, last.GetValueOrDefault(schedule.Id), cancellationToken));
        }

        return result;
    }

    public async Task<Result<ReportScheduleSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null || !CanSee(schedule, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var platform = (await scheduler.ListAsync(unitOfWork.Current.Context.TenantId.Value, allTenants: false, cancellationToken)).ToDictionary(static s => s.Id);
        var last = await db.Runs.AsNoTracking().Where(r => r.ScheduleId == id).OrderByDescending(static r => r.StartedAt).FirstOrDefaultAsync(cancellationToken);
        return await MapAsync(schedule, who, platform, last, cancellationToken);
    }

    public async Task<Result<ReportScheduleSummary>> SaveAsync(Guid? id, SaveReportScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var who = principal.Required;
        var name = new LocalizedText(request.Name);
        if (string.IsNullOrWhiteSpace(name.Resolve("en", "ar")))
        {
            return Error.Validation("schedule.name_required", "A schedule needs a name.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (!WidgetSources.All.Contains(request.Source, StringComparer.Ordinal))
        {
            return Error.Validation("schedule.source_invalid", "A schedule runs a saved report or a statement.").WithWhy(("source", request.Source));
        }

        if (!RunFormats.All.Contains(request.Format, StringComparer.Ordinal))
        {
            return Error.Validation("schedule.format_invalid", "The file is csv, xlsx or pdf.").WithWhy(("format", request.Format), ("known", RunFormats.All));
        }

        if (!RelativePeriods.All.Contains(request.Period, StringComparer.Ordinal) || request.Period == RelativePeriods.Custom)
        {
            return Error.Validation("schedule.period_invalid", "A schedule names a relative period such as last_month.").WithWhy(("period", request.Period), ("known", RelativePeriods.All.Where(static p => p != RelativePeriods.Custom).ToList()));
        }

        var cron = CronExpression.Parse(request.Cron);
        if (cron.IsFailure)
        {
            return cron.Error!;
        }

        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? company.TimeZone : request.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            return Error.Validation("schedule.time_zone_invalid", "Use an IANA time zone id such as Asia/Baghdad.").WithWhy(("timeZone", timeZone));
        }

        var recipients = await ValidateRecipientsAsync(request.Recipients, cancellationToken);
        if (recipients.IsFailure)
        {
            return recipients.Error!;
        }

        ReportSchedule schedule;
        object? before = null;
        if (id is { } existing)
        {
            var found = await db.Schedules.SingleOrDefaultAsync(s => s.Id == existing, cancellationToken);
            if (found is null || !CanSee(found, who))
            {
                return Error.NotFound(AuditEntity, existing);
            }

            before = Snapshot(found);
            schedule = found;
        }
        else
        {
            schedule = new ReportSchedule { Id = Guid.CreateVersion7(), OwnerMembershipId = who.MembershipId.Value, CreatedAt = clock.UtcNow };
            db.Schedules.Add(schedule);
        }

        var source = await ResolveSourceAsync(request, schedule, cancellationToken);
        if (source.IsFailure)
        {
            return source.Error!;
        }

        schedule.Name = name;
        schedule.CompanyId = company.Id.Value;
        schedule.Source = request.Source;
        schedule.Period = request.Period;
        schedule.Cron = cron.Value.Text;
        schedule.TimeZone = timeZone;
        schedule.Format = request.Format;
        schedule.Recipients = JsonSerializer.Serialize(recipients.Value, Json);
        schedule.OnlyIfRows = request.OnlyIfRows;
        schedule.IncludeClosing = request.IncludeClosing;
        schedule.Enabled = request.Enabled;
        schedule.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // The platform entry that fires the job: upserted by code so an edit keeps the same row.
        var platform = await scheduler.SaveAsync(unitOfWork.Current.Context.TenantId.Value,
            new SaveScheduleRequest(PlatformCode(schedule.Id), ReportRunJob.JobType, schedule.Cron, schedule.TimeZone, new ReportRunPayload(schedule.Id), schedule.Enabled),
            [ReportRunJob.JobType], cancellationToken);
        if (platform.IsFailure)
        {
            return platform.Error!;
        }

        schedule.PlatformScheduleId = platform.Value.Id;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, schedule.Id, schedule.Name.Resolve("en", "ar"), id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(schedule), CompanyId: schedule.CompanyId), cancellationToken);
        return await GetAsync(schedule.Id, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var schedule = await db.Schedules.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null || !CanSee(schedule, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var runs = await db.Runs.Where(r => r.ScheduleId == id).ToListAsync(cancellationToken);
        foreach (var run in runs.Where(static r => r.StorageKey is not null))
        {
            await storage.DeleteAsync(run.StorageKey!, cancellationToken: cancellationToken);
        }

        if (schedule.PlatformScheduleId is { } platformId)
        {
            await scheduler.DeleteAsync(platformId, unitOfWork.Current.Context.TenantId.Value, allTenants: false, cancellationToken);
        }

        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, schedule.Id, schedule.Name.Resolve("en", "ar"), AuditActions.Deleted, Before: Snapshot(schedule), CompanyId: schedule.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Enqueues the schedule's job now; the run is recorded like a scheduled one, marked manual.</summary>
    public async Task<Result<ScheduleRunRequested>> RunNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null || !CanSee(schedule, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var jobId = await jobs.EnqueueAsync(new JobRequest(ReportRunJob.JobType, new ReportRunPayload(schedule.Id, RunTriggers.Manual), MaxAttempts: 1), cancellationToken);
        return new ScheduleRunRequested(schedule.Id, jobId);
    }

    public async Task<Result<IReadOnlyList<ReportRunSummary>>> RunsAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (schedule is null || !CanSee(schedule, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var runs = await db.Runs.AsNoTracking().Where(r => r.ScheduleId == id).OrderByDescending(static r => r.StartedAt).Take(100).ToListAsync(cancellationToken);
        return runs.Select(MapRun).ToList();
    }

    public async Task<Result<RunFile>> OpenRunFileAsync(Guid runId, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound("report_run", runId);
        }

        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == run.ScheduleId, cancellationToken);
        if (schedule is null || !CanSee(schedule, who))
        {
            return Error.NotFound("report_run", runId);
        }

        if (run.StorageKey is null || run.FileName is null)
        {
            return Error.Conflict("report_run.no_file", "This run produced no file.");
        }

        var stored = await storage.GetAsync(run.StorageKey, cancellationToken: cancellationToken);
        return stored is null
            ? Error.NotFound("report_run_file", runId)
            : new RunFile(stored.Content, run.ContentType ?? "application/octet-stream", run.FileName, stored.Info.Length);
    }

    public static string PlatformCode(Guid scheduleId) => $"reporting.run.{scheduleId:N}";

    public static IReadOnlyList<ScheduleRecipient> Recipients(ReportSchedule schedule) =>
        JsonSerializer.Deserialize<List<ScheduleRecipient>>(schedule.Recipients, Json) ?? [];

    internal static ReportRunSummary MapRun(ReportRun r) =>
        new(r.Id, r.ScheduleId, r.Trigger, r.Status, r.RunAsMembershipId, r.StartedAt, r.FinishedAt, r.PeriodFrom, r.PeriodTo, r.RowCount, r.FileName, r.ContentType, r.SizeBytes,
            JsonSerializer.Deserialize<List<string>>(r.Recipients, Json) ?? [], r.Error, r.StorageKey is not null);

    private async Task<Result<IReadOnlyList<ScheduleRecipient>>> ValidateRecipientsAsync(IReadOnlyList<ScheduleRecipient>? recipients, CancellationToken cancellationToken)
    {
        var result = new List<ScheduleRecipient>();
        foreach (var recipient in recipients ?? [])
        {
            if (recipient.MembershipId is { } membershipId)
            {
                var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
                if (member is null || !member.IsActive)
                {
                    return Error.Validation("schedule.recipient_unknown", "A recipient is an active member or an email address.").WithWhy(("membershipId", membershipId));
                }

                result.Add(new ScheduleRecipient(membershipId));
            }
            else
            {
                var email = recipient.Email?.Trim() ?? string.Empty;
                if (email.Length is 0 or > 254 || !email.Contains('@', StringComparison.Ordinal) || email.Contains(' ', StringComparison.Ordinal))
                {
                    return Error.Validation("schedule.recipient_invalid", "A recipient is an active member or an email address.").WithWhy(("email", recipient.Email));
                }

                result.Add(new ScheduleRecipient(null, email));
            }
        }

        return result.Count == 0 ? Error.Validation("schedule.recipients_required", "A schedule sends to at least one member or address.") : result;
    }

    private async Task<Result> ResolveSourceAsync(SaveReportScheduleRequest request, ReportSchedule schedule, CancellationToken cancellationToken)
    {
        schedule.ReportId = null;
        schedule.LayoutId = null;
        schedule.LayoutCode = null;
        if (request.Source == WidgetSources.Report)
        {
            if (request.ReportId is not { } reportId)
            {
                return Error.Validation("schedule.report_required", "A report schedule names a saved report.");
            }

            var report = await reports.GetAsync(reportId, cancellationToken);
            if (report.IsFailure)
            {
                return report.Error!;
            }

            schedule.ReportId = reportId;
            return Result.Success();
        }

        if (request.LayoutId is { } layoutId)
        {
            var layout = await layouts.GetAsync(layoutId, cancellationToken);
            if (layout.IsFailure)
            {
                return layout.Error!;
            }

            schedule.LayoutId = layoutId;
            return Result.Success();
        }

        if (!string.IsNullOrWhiteSpace(request.LayoutCode) && ShippedLayouts.Find(request.LayoutCode) is { } shipped)
        {
            schedule.LayoutCode = shipped.Code;
            return Result.Success();
        }

        return Error.Validation("schedule.layout_required", "A statement schedule names a layout by id or a shipped layout by code.").WithWhy(("layoutCode", request.LayoutCode));
    }

    private async Task<ReportScheduleSummary> MapAsync(ReportSchedule s, Principal who, IReadOnlyDictionary<Guid, ScheduleRecord> platform, ReportRun? last, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? sourceName = null;
        if (s.ReportId is { } reportId)
        {
            var report = await reports.GetAsync(reportId, cancellationToken);
            sourceName = report.IsSuccess ? report.Value.Name : null;
        }
        else if (s.LayoutId is { } layoutId)
        {
            var layout = await layouts.GetAsync(layoutId, cancellationToken);
            sourceName = layout.IsSuccess ? layout.Value.Name : null;
        }
        else if (s.LayoutCode is { } code)
        {
            sourceName = ShippedLayouts.Find(code)?.Name;
        }

        var entry = s.PlatformScheduleId is { } platformId ? platform.GetValueOrDefault(platformId) : null;
        return new ReportScheduleSummary(s.Id, s.CompanyId, s.Name.Values, s.OwnerMembershipId, s.OwnerMembershipId == who.MembershipId.Value, s.Source, s.ReportId, s.LayoutId, s.LayoutCode, sourceName,
            s.Period, s.Cron, s.TimeZone, s.Format, Recipients(s), s.OnlyIfRows, s.IncludeClosing, s.Enabled, entry?.NextRunAt, last?.StartedAt ?? entry?.LastRunAt, last?.Status, s.CreatedAt, s.UpdatedAt);
    }

    private static bool CanSee(ReportSchedule schedule, Principal who) => who.IsOwner || schedule.OwnerMembershipId == who.MembershipId.Value;

    private static object Snapshot(ReportSchedule s) => new { name = s.Name.Values, s.CompanyId, s.Source, s.ReportId, s.LayoutId, s.LayoutCode, s.Period, s.Cron, s.TimeZone, s.Format, recipients = Recipients(s), s.OnlyIfRows, s.IncludeClosing, s.Enabled };
}

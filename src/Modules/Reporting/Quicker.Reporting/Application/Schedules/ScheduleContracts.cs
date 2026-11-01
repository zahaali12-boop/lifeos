namespace Quicker.Reporting.Application.Schedules;

/// <summary>Who a schedule sends to: a member (by membership, at the address on their profile at run time) or an outside address.</summary>
public sealed record ScheduleRecipient(Guid? MembershipId = null, string? Email = null);

public sealed record SaveReportScheduleRequest(
    IReadOnlyDictionary<string, string> Name,
    Guid CompanyId,
    string Source,
    string Cron,
    string Format,
    IReadOnlyList<ScheduleRecipient> Recipients,
    Guid? ReportId = null,
    Guid? LayoutId = null,
    string? LayoutCode = null,
    string Period = "last_month",
    string? TimeZone = null,
    bool OnlyIfRows = false,
    bool? IncludeClosing = null,
    bool Enabled = true);

public sealed record ReportScheduleSummary(
    Guid Id,
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Name,
    Guid OwnerMembershipId,
    bool IsOwner,
    string Source,
    Guid? ReportId,
    Guid? LayoutId,
    string? LayoutCode,
    IReadOnlyDictionary<string, string>? SourceName,
    string Period,
    string Cron,
    string TimeZone,
    string Format,
    IReadOnlyList<ScheduleRecipient> Recipients,
    bool OnlyIfRows,
    bool? IncludeClosing,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ReportRunSummary(
    Guid Id,
    Guid ScheduleId,
    string Trigger,
    string Status,
    Guid RunAsMembershipId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    int? RowCount,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    IReadOnlyList<string> Recipients,
    string? Error,
    bool HasFile);

/// <summary>What a "run now" returns: the job that will run the schedule.</summary>
public sealed record ScheduleRunRequested(Guid ScheduleId, Guid JobId);

/// <summary>The file of a run, streamed back.</summary>
public sealed record RunFile(Stream Content, string ContentType, string FileName, long? Length);

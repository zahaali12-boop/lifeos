using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Reporting.Domain;

/// <summary>A schedule (DOMAIN_MODEL §21): a saved report or a statement run as its owner on a cron, in a format, to recipients; mirrored by a platform schedule that enqueues the job.</summary>
public sealed class ReportSchedule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public LocalizedText Name { get; set; } = new();

    public Guid OwnerMembershipId { get; set; }

    public string Source { get; set; } = WidgetSources.Report;

    public Guid? ReportId { get; set; }

    public Guid? LayoutId { get; set; }

    public string? LayoutCode { get; set; }

    public string Period { get; set; } = RelativePeriods.LastMonth;

    public string Cron { get; set; } = string.Empty;

    public string TimeZone { get; set; } = "UTC";

    public string Format { get; set; } = RunFormats.Xlsx;

    /// <summary>Recipients as JSON (a list of <c>ScheduleRecipient</c>: a member or an address).</summary>
    public string Recipients { get; set; } = "[]";

    public bool OnlyIfRows { get; set; }

    public bool? IncludeClosing { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The <c>ops.schedules</c> row that fires the job.</summary>
    public Guid? PlatformScheduleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One execution of a schedule: when, as whom, for which window, what came out (the file, the recipients) or why not.</summary>
public sealed class ReportRun : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ScheduleId { get; set; }

    public Guid CompanyId { get; set; }

    public string Source { get; set; } = WidgetSources.Report;

    public Guid? ReportId { get; set; }

    public Guid? LayoutId { get; set; }

    public string? LayoutCode { get; set; }

    public Guid RunAsMembershipId { get; set; }

    public string Trigger { get; set; } = RunTriggers.Schedule;

    public Guid? JobId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string Status { get; set; } = RunStatuses.Running;

    public DateOnly? PeriodFrom { get; set; }

    public DateOnly? PeriodTo { get; set; }

    public int? RowCount { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public string? StorageKey { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>The addresses the file went to, as a JSON list of strings.</summary>
    public string Recipients { get; set; } = "[]";

    public string? Error { get; set; }
}

public static class RunFormats
{
    public const string Csv = "csv";
    public const string Xlsx = "xlsx";
    public const string Pdf = "pdf";

    public static readonly IReadOnlyList<string> All = [Csv, Xlsx, Pdf];
}

public static class RunTriggers
{
    public const string Schedule = "schedule";
    public const string Manual = "manual";
}

public static class RunStatuses
{
    public const string Running = "running";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

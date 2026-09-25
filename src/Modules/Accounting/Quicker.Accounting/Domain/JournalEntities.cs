using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Accounting.Domain;

public static class JournalStatuses
{
    public const string Draft = "draft";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Posted = "posted";
    public const string Cancelled = "cancelled";
}

public static class JournalKinds
{
    public const string Manual = "manual";
    public const string Recurring = "recurring";
    public const string Reversing = "reversing";
    public const string Accrual = "accrual";
    public const string Opening = "opening";
    public const string Allocation = "allocation";

    public static readonly IReadOnlyList<string> All = [Manual, Recurring, Reversing, Accrual, Opening, Allocation];
}

public sealed class ManualJournal : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string? Number { get; set; }

    public string Kind { get; set; } = JournalKinds.Manual;

    public DateOnly PostingDate { get; set; }

    public DateOnly DocumentDate { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string RateType { get; set; } = "spot";

    public decimal? RateOverride { get; set; }

    public string? RateOverrideReason { get; set; }

    public Guid? BranchId { get; set; }

    public LocalizedText Description { get; set; } = new();

    public string? Reference { get; set; }

    public string Status { get; set; } = JournalStatuses.Draft;

    public bool AutoReverse { get; set; }

    public DateOnly? AutoReverseOn { get; set; }

    public Guid? TemplateId { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? CorrectsJournalId { get; set; }

    public Guid? CorrectedByJournalId { get; set; }

    public string? CorrectionReason { get; set; }

    public string CustomFields { get; set; } = "{}";

    public Guid? SubmittedBy { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public string? RejectionReason { get; set; }

    public Guid? PostedBy { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ManualJournalLine> Lines { get; } = [];
}

public sealed class ManualJournalLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid JournalId { get; set; }

    public int LineNo { get; set; }

    public Guid AccountId { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public Dictionary<string, Guid> Dimensions { get; set; } = new(StringComparer.Ordinal);

    public Guid? PartnerId { get; set; }

    public string? SubledgerType { get; set; }

    public Guid? SubledgerRef { get; set; }

    public Guid? TaxCodeId { get; set; }

    public LocalizedText Description { get; set; } = new();

    public DateOnly? DueDate { get; set; }
}

public sealed class RecurringTemplate : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Cron { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public DateOnly? NextRunOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public string AmountMode { get; set; } = "fixed";

    public decimal? BaseAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary>JSON array of template lines (account code, debit/credit or percent, dimensions, description).</summary>
    public string Lines { get; set; } = "[]";

    public LocalizedText Description { get; set; } = new();

    public bool RequiresReview { get; set; } = true;

    public bool AutoReverse { get; set; }

    public bool IsActive { get; set; } = true;

    public DateOnly? LastGeneratedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeferralSchedule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Kind { get; set; } = "prepayment";

    public string? SourceDocumentType { get; set; }

    public Guid? SourceLineId { get; set; }

    public Guid BalanceAccountId { get; set; }

    public Guid TargetAccountId { get; set; }

    public DateOnly StartsOn { get; set; }

    public int Periods { get; set; }

    public string Method { get; set; } = "straight_line";

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public Dictionary<string, Guid> Dimensions { get; set; } = new(StringComparer.Ordinal);

    public LocalizedText Description { get; set; } = new();

    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<DeferralLine> Lines { get; } = [];
}

public sealed class DeferralLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ScheduleId { get; set; }

    public int Sequence { get; set; }

    public Guid? FiscalPeriodId { get; set; }

    public DateOnly PostingDate { get; set; }

    public decimal Amount { get; set; }

    public Guid? JournalEntryId { get; set; }

    public string Status { get; set; } = "planned";
}

/// <summary>One run of the daily routines for one company: what it posted or generated and what waited (append-only).</summary>
public sealed class RoutineRun : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public DateOnly AsOf { get; set; }

    /// <summary><c>schedule</c> (the platform's daily job) or <c>manual</c> (someone ran them now).</summary>
    public string Trigger { get; set; } = RoutineTriggers.Schedule;

    public Guid? RunBy { get; set; }

    public string? RunByName { get; set; }

    public DateTimeOffset RanAt { get; set; }

    public int Posted { get; set; }

    public int Waiting { get; set; }

    /// <summary>JSON array of <see cref="Application.RoutineRunItem"/>.</summary>
    public string Items { get; set; } = "[]";
}

public static class RoutineTriggers
{
    public const string Schedule = "schedule";
    public const string Manual = "manual";
}


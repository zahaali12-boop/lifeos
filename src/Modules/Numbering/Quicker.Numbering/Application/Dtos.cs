namespace Quicker.Numbering.Application;

public sealed record SaveSeriesRequest(
    string Code,
    string DocumentType,
    Guid CompanyId,
    string Template,
    Guid? BranchId = null,
    Guid? FiscalYearId = null,
    long StartNumber = 1,
    bool Gapless = true,
    string ResetPolicy = "never",
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsDefault = false,
    bool IsActive = true);

public sealed record CounterSummary(string PeriodKey, long NextNumber, long Allocated, DateTimeOffset UpdatedAt);

public sealed record SeriesSummary(
    Guid Id,
    string Code,
    string DocumentType,
    Guid CompanyId,
    Guid? BranchId,
    Guid? FiscalYearId,
    string Template,
    long StartNumber,
    bool Gapless,
    string ResetPolicy,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsDefault,
    bool IsActive,
    IReadOnlyList<CounterSummary> Counters);

/// <summary>Moves a counter; moving it backwards needs the reset permission and a reason and never below a number already issued.</summary>
public sealed record SetCounterRequest(long NextNumber, string PeriodKey = "", string? Reason = null);

public sealed record AllocationSummary(Guid Id, Guid SeriesId, string PeriodKey, long Number, string Text, string DocumentType, Guid DocumentId, Guid? AllocatedBy, DateTimeOffset AllocatedAt);

public sealed record AllocateRequest(string DocumentType, Guid CompanyId, DateOnly Date, Guid DocumentId, Guid? BranchId = null, Guid? SeriesId = null);

/// <summary>Gapless audit for one reset period of a series: what was issued and which numbers are missing (expected: none).</summary>
public sealed record PeriodGaps(string PeriodKey, long First, long Last, long Allocated, long NextNumber, IReadOnlyList<long> Missing);

public sealed record SeriesGapReport(Guid SeriesId, string Code, bool Gapless, IReadOnlyList<PeriodGaps> Periods);

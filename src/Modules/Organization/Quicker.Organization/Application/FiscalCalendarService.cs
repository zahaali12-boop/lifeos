using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Persistence;

namespace Quicker.Organization.Application;

/// <summary>Fiscal calendars, years with 12 or 13 periods, and per-company per-module period states (ADR-0011, ADR-0026).</summary>
public sealed class FiscalCalendarService(OrganizationDbContext db, IUnitOfWorkAccessor unitOfWork, IRoleDirectory roles, IAuditSink audit, IClock clock) : IFiscalPeriodResolver, IPostingWindows
{
    private static readonly string[] SettableStates = [PeriodStates.Open, PeriodStates.SoftClosed, PeriodStates.HardClosed];

    private Guid? ActorUserId => unitOfWork.Current.Context.UserId?.Value;

    // ------------------------------------------------------------------ calendars

    public async Task<IReadOnlyList<FiscalCalendarSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await Query().OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<FiscalCalendarSummary?> GetAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        return calendar is null ? null : Map(calendar);
    }

    public async Task<Result<FiscalCalendarSummary>> CreateAsync(SaveFiscalCalendarRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.LowerCode(request.Code, "fiscal_calendar");
        var name = Validation.Name(request.Name, "fiscal_calendar");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (request.StartMonth is < 1 or > 12)
        {
            return Error.Validation("fiscal_calendar.start_month_invalid", "The start month is 1 to 12.");
        }

        if (request.PeriodsPerYear is not (12 or 13))
        {
            return Error.Validation("fiscal_calendar.periods_invalid", "A fiscal year has 12 periods, or 13 with an adjustment period.");
        }

        if (await db.FiscalCalendars.AnyAsync(c => c.Code == code.Value, cancellationToken))
        {
            return Error.Conflict("fiscal_calendar.code_taken", $"A fiscal calendar with code '{code.Value}' already exists.");
        }

        var now = clock.UtcNow;
        var calendar = new FiscalCalendar { Id = Guid.CreateVersion7(), Code = code.Value, Name = name.Value, StartMonth = request.StartMonth, PeriodsPerYear = request.PeriodsPerYear, CreatedAt = now, UpdatedAt = now };
        db.FiscalCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        _resolveCache.Clear();
        return Map(calendar);
    }

    public async Task<Result<FiscalCalendarSummary>> UpdateAsync(Guid calendarId, SaveFiscalCalendarRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        if (calendar is null)
        {
            return Error.NotFound("fiscal_calendar", calendarId);
        }

        var code = Validation.LowerCode(request.Code, "fiscal_calendar");
        var name = Validation.Name(request.Name, "fiscal_calendar");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (calendar.Years.Count > 0 && (calendar.StartMonth != request.StartMonth || calendar.PeriodsPerYear != request.PeriodsPerYear))
        {
            return Error.Conflict("fiscal_calendar.in_use", "The start month and period count cannot change once a fiscal year exists.");
        }

        if (request.StartMonth is < 1 or > 12 || request.PeriodsPerYear is not (12 or 13))
        {
            return Error.Validation("fiscal_calendar.invalid", "The start month is 1 to 12 and a year has 12 or 13 periods.");
        }

        if (await db.FiscalCalendars.AnyAsync(c => c.Code == code.Value && c.Id != calendarId, cancellationToken))
        {
            return Error.Conflict("fiscal_calendar.code_taken", $"A fiscal calendar with code '{code.Value}' already exists.");
        }

        calendar.Code = code.Value;
        calendar.Name = name.Value;
        calendar.StartMonth = request.StartMonth;
        calendar.PeriodsPerYear = request.PeriodsPerYear;
        calendar.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _resolveCache.Clear();
        return Map(calendar);
    }

    /// <summary>The tenant's default calendar: the system one created at sign-up (or the first by code).</summary>
    public async Task<Guid> DefaultCalendarIdAsync(CancellationToken cancellationToken) =>
        await db.FiscalCalendars.OrderBy(static c => c.IsSystem ? 0 : 1).ThenBy(static c => c.Code).Select(static c => c.Id).FirstAsync(cancellationToken);

    // ------------------------------------------------------------------ years

    public async Task<Result<FiscalYearSummary>> OpenYearAsync(Guid calendarId, OpenFiscalYearRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        if (calendar is null)
        {
            return Error.NotFound("fiscal_calendar", calendarId);
        }

        if (request.StartYear is < 1900 or > 2200)
        {
            return Error.Validation("fiscal_year.start_year_invalid", "The start year is a four-digit year.");
        }

        var status = Validation.OneOf(request.Status, "fiscal_year.status", "open", "future");
        if (status.IsFailure)
        {
            return status.Error!;
        }

        var startsOn = new DateOnly(request.StartYear, calendar.StartMonth, 1);
        if (calendar.Years.Exists(y => y.StartsOn <= startsOn.AddYears(1).AddDays(-1) && y.EndsOn >= startsOn))
        {
            return Error.Conflict("fiscal_year.overlaps", $"A fiscal year already covers {startsOn:yyyy-MM-dd}.").WithWhy(("startsOn", startsOn));
        }

        var year = BuildYear(calendar, startsOn, status.Value);
        calendar.Years.Add(year);
        await db.SaveChangesAsync(cancellationToken);
        _resolveCache.Clear();
        return Map(year);
    }

    /// <summary>Makes sure the calendar has a year covering the date (opened automatically when a company is created).</summary>
    public async Task<FiscalYear> EnsureYearCoversAsync(Guid calendarId, DateOnly date, CancellationToken cancellationToken)
    {
        var calendar = await Query().SingleAsync(c => c.Id == calendarId, cancellationToken);
        var existing = calendar.Years.Find(y => y.Covers(date));
        if (existing is not null)
        {
            return existing;
        }

        var startsOn = new DateOnly(date.Year, calendar.StartMonth, 1);
        if (startsOn > date)
        {
            startsOn = startsOn.AddYears(-1);
        }

        var year = BuildYear(calendar, startsOn, "open");
        calendar.Years.Add(year);
        await db.SaveChangesAsync(cancellationToken);
        _resolveCache.Clear();
        return year;
    }

    private FiscalYear BuildYear(FiscalCalendar calendar, DateOnly startsOn, string status)
    {
        var endsOn = startsOn.AddYears(1).AddDays(-1);
        var now = clock.UtcNow;
        var year = new FiscalYear
        {
            Id = Guid.CreateVersion7(),
            CalendarId = calendar.Id,
            Code = YearCode(startsOn, endsOn),
            StartsOn = startsOn,
            EndsOn = endsOn,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        for (var number = 1; number <= 12; number++)
        {
            var periodStart = startsOn.AddMonths(number - 1);
            year.Periods.Add(new FiscalPeriod { Id = Guid.CreateVersion7(), FiscalYearId = year.Id, Number = number, StartsOn = periodStart, EndsOn = periodStart.AddMonths(1).AddDays(-1) });
        }

        if (calendar.PeriodsPerYear == 13)
        {
            // The adjustment period shares the last day of the year and is chosen explicitly, never by date.
            year.Periods.Add(new FiscalPeriod { Id = Guid.CreateVersion7(), FiscalYearId = year.Id, Number = 13, StartsOn = endsOn, EndsOn = endsOn, IsAdjustment = true });
        }

        return year;
    }

    /// <summary>FY2026 for a calendar year; FY2026/27 for a year that spans two calendar years.</summary>
    public static string YearCode(DateOnly startsOn, DateOnly endsOn) =>
        startsOn.Year == endsOn.Year ? $"FY{startsOn.Year}" : $"FY{startsOn.Year}/{endsOn.Year % 100:00}";

    // ------------------------------------------------------------------ period states

    public async Task<Result<IReadOnlyList<PeriodStateSummary>>> ListStatesAsync(Guid periodId, Guid companyId, CancellationToken cancellationToken)
    {
        var period = await db.FiscalPeriods.SingleOrDefaultAsync(p => p.Id == periodId, cancellationToken);
        if (period is null)
        {
            return Error.NotFound("fiscal_period", periodId);
        }

        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            return Error.NotFound("company", companyId);
        }

        var year = await db.FiscalYears.SingleAsync(y => y.Id == period.FiscalYearId, cancellationToken);
        var rows = await db.PeriodStates.Where(s => s.PeriodId == periodId && s.CompanyId == companyId).ToListAsync(cancellationToken);
        return PostingModules.All.Select(module =>
        {
            var row = rows.Find(r => r.Module == module);
            return row is null
                ? new PeriodStateSummary(periodId, companyId, module, DefaultState(year), false, null, null, null)
                : new PeriodStateSummary(periodId, companyId, module, row.State, true, row.ChangedBy, row.ChangedAt, row.Reason);
        }).ToList();
    }

    public Task<Result<IReadOnlyList<PeriodStateSummary>>> SetStateAsync(Guid periodId, SetPeriodStateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeStateAsync(periodId, request.CompanyId, request.Modules, request.State, request.Reason, reopening: false, cancellationToken);
    }

    /// <summary>Hard-closed → open or soft-closed, with a mandatory reason; a separate permission and audited as an override.</summary>
    public Task<Result<IReadOnlyList<PeriodStateSummary>>> ReopenAsync(Guid periodId, ReopenPeriodRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.Reason)
            ? Task.FromResult<Result<IReadOnlyList<PeriodStateSummary>>>(Error.Validation("period.reason_required", "Reopening a hard-closed period requires a reason."))
            : ChangeStateAsync(periodId, request.CompanyId, request.Modules, request.State, request.Reason, reopening: true, cancellationToken);
    }

    private async Task<Result<IReadOnlyList<PeriodStateSummary>>> ChangeStateAsync(Guid periodId, Guid companyId, IReadOnlyList<string> modules, string state, string? reason, bool reopening, CancellationToken cancellationToken)
    {
        var period = await db.FiscalPeriods.SingleOrDefaultAsync(p => p.Id == periodId, cancellationToken);
        if (period is null)
        {
            return Error.NotFound("fiscal_period", periodId);
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var target = Validation.OneOf(state, "period.state", reopening ? [PeriodStates.Open, PeriodStates.SoftClosed] : SettableStates);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var requested = (modules ?? []).Select(static m => m.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count == 0 || requested.Exists(static m => !PostingModules.IsValid(m)))
        {
            return Error.Validation("period.module_invalid", $"Modules are one or more of {string.Join(", ", PostingModules.All)}.");
        }

        var year = await db.FiscalYears.SingleAsync(y => y.Id == period.FiscalYearId, cancellationToken);
        if (year.Status != "open")
        {
            return Error.Conflict("period.year_not_open", $"Fiscal year {year.Code} is {year.Status}; period states can only change in an open year.").WithWhy(("fiscalYear", year.Code), ("status", year.Status));
        }

        var rows = await db.PeriodStates.Where(s => s.PeriodId == periodId && s.CompanyId == companyId).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        foreach (var module in requested)
        {
            var row = rows.Find(r => r.Module == module);
            var current = row?.State ?? DefaultState(year);
            if (current == target.Value)
            {
                continue;
            }

            if (current == PeriodStates.HardClosed && !reopening)
            {
                return Error.Conflict("period.hard_closed", $"Period {period.Number} of {year.Code} is hard-closed for {module}; reopening needs the reopen permission and a reason.")
                    .WithWhy(("period", period.Number), ("fiscalYear", year.Code), ("module", module), ("state", current), ("requiredPermission", OrganizationPermissions.PeriodReopen));
            }

            if (current != PeriodStates.HardClosed && reopening)
            {
                return Error.Conflict("period.not_hard_closed", $"Period {period.Number} of {year.Code} is {current} for {module}; use the ordinary state change.")
                    .WithWhy(("module", module), ("state", current));
            }

            if (row is null)
            {
                row = new PeriodModuleState { PeriodId = periodId, CompanyId = companyId, Module = module };
                db.PeriodStates.Add(row);
                rows.Add(row);
            }

            row.State = target.Value;
            row.ChangedBy = ActorUserId;
            row.ChangedAt = now;
            row.Reason = reason?.Trim();
            await audit.RecordAsync(new AuditEntry("fiscal_period", periodId, $"{year.Code} P{period.Number} {module}", reopening ? AuditActions.Override : AuditActions.StateChanged,
                Before: new { module, state = current }, After: new { module, state = target.Value }, Reason: row.Reason, CompanyId: companyId), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        _resolveCache.Clear();
        return await ListStatesAsync(periodId, companyId, cancellationToken);
    }

    private static string DefaultState(FiscalYear year) => year.Status switch
    {
        "open" => PeriodStates.Open,
        "closed" => PeriodStates.HardClosed,
        _ => PeriodStates.NeverOpened,
    };

    // ------------------------------------------------------------------ posting windows (ADR-0026)

    public async Task<Result<IReadOnlyList<PostingWindowSummary>>> ListWindowsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            return Error.NotFound("company", companyId);
        }

        var rows = await db.PostingWindows.Where(w => w.CompanyId == companyId).OrderBy(static w => w.RoleId == null ? 0 : 1).ThenBy(static w => w.CreatedAt).ToListAsync(cancellationToken);
        return rows.Select(MapWindow).ToList();
    }

    /// <summary>Replaces the company's windows: at most one for everyone and one per role, each with at least one bound.</summary>
    public async Task<Result<IReadOnlyList<PostingWindowSummary>>> ReplaceWindowsAsync(Guid companyId, IReadOnlyList<PostingWindowRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            return Error.NotFound("company", companyId);
        }

        var seen = new HashSet<Guid?>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (request.AllowFrom is null && request.AllowTo is null)
            {
                return Error.Validation("posting_window.bounds_required", $"Window {i + 1}: give an allow-from date, an allow-to date or both.").WithWhy(("window", i + 1));
            }

            if (request.AllowFrom is { } from && request.AllowTo is { } to && from > to)
            {
                return Error.Validation("posting_window.bounds_inverted", $"Window {i + 1}: allow-from {from:yyyy-MM-dd} is after allow-to {to:yyyy-MM-dd}.").WithWhy(("window", i + 1), ("allowFrom", from), ("allowTo", to));
            }

            if (!seen.Add(request.RoleId))
            {
                return Error.Validation("posting_window.duplicate", $"Window {i + 1}: {(request.RoleId is null ? "everyone" : "the role")} already has a window in this request.").WithWhy(("window", i + 1), ("roleId", request.RoleId));
            }

            if (request.RoleId is { } roleId && await roles.FindRoleAsync(roleId, cancellationToken) is null)
            {
                return Error.NotFound("role", roleId).WithWhy(("window", i + 1));
            }
        }

        var existing = await db.PostingWindows.Where(w => w.CompanyId == companyId).ToListAsync(cancellationToken);
        var before = existing.Select(MapWindow).ToList();
        db.PostingWindows.RemoveRange(existing);
        var now = clock.UtcNow;
        foreach (var request in requests)
        {
            db.PostingWindows.Add(new PostingWindow
            {
                Id = Guid.CreateVersion7(),
                CompanyId = companyId,
                RoleId = request.RoleId,
                AllowFrom = request.AllowFrom,
                AllowTo = request.AllowTo,
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                ChangedBy = ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        _resolveCache.Clear();
        var after = await ListWindowsAsync(companyId, cancellationToken);
        await audit.RecordAsync(new AuditEntry("company", companyId, "posting windows", AuditActions.Updated, Before: new { windows = before }, After: new { windows = after.Value }, CompanyId: companyId), cancellationToken);
        return after;
    }

    public async Task<PostingWindowInfo?> EffectiveAsync(CompanyId companyId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var rows = await db.PostingWindows.AsNoTracking().Where(w => w.CompanyId == companyId.Value && (w.RoleId == null || roleIds.Contains(w.RoleId.Value))).ToListAsync(cancellationToken);
        var byRole = rows.Where(static w => w.RoleId != null).ToList();
        if (byRole.Count > 0)
        {
            // The widest of the actor's role windows: an open bound on any role opens that side.
            var from = byRole.Exists(static w => w.AllowFrom == null) ? null : byRole.Min(static w => w.AllowFrom);
            var to = byRole.Exists(static w => w.AllowTo == null) ? null : byRole.Max(static w => w.AllowTo);
            return new PostingWindowInfo(companyId.Value, byRole.Count == 1 ? byRole[0].RoleId : null, from, to);
        }

        var everyone = rows.Find(static w => w.RoleId == null);
        return everyone is null ? null : new PostingWindowInfo(companyId.Value, null, everyone.AllowFrom, everyone.AllowTo);
    }

    private static PostingWindowSummary MapWindow(PostingWindow w) => new(w.Id, w.CompanyId, w.RoleId, w.AllowFrom, w.AllowTo, w.Reason, w.ChangedBy, w.UpdatedAt);

    // ------------------------------------------------------------------ resolver (contract)

    // Period resolution runs for every posting; memoised for the life of this scoped service, dropped after every write it makes.
    private readonly Dictionary<(Guid Company, DateOnly Date, string Module), Result<PeriodState>> _resolveCache = new();

    public async Task<Result<PeriodState>> ResolveAsync(CompanyId companyId, DateOnly postingDate, string module, CancellationToken cancellationToken = default)
    {
        var key = (companyId.Value, postingDate, module?.Trim().ToUpperInvariant() ?? string.Empty);
        if (_resolveCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveUncachedAsync(companyId, postingDate, module, cancellationToken);
        _resolveCache[key] = resolved;
        return resolved;
    }

    private async Task<Result<PeriodState>> ResolveUncachedAsync(CompanyId companyId, DateOnly postingDate, string? module, CancellationToken cancellationToken)
    {
        var normalizedModule = module?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!PostingModules.IsValid(normalizedModule))
        {
            return Error.Validation("period.module_invalid", $"Modules are one of {string.Join(", ", PostingModules.All)}.");
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId.Value, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId.Value);
        }

        var year = await db.FiscalYears.SingleOrDefaultAsync(y => y.CalendarId == company.FiscalCalendarId && y.StartsOn <= postingDate && y.EndsOn >= postingDate, cancellationToken);
        if (year is null)
        {
            return Error.Conflict("period.no_fiscal_year", $"No fiscal year of company {company.Code} covers {postingDate:yyyy-MM-dd}.")
                .WithWhy(("company", company.Code), ("date", postingDate), ("fiscalCalendarId", company.FiscalCalendarId));
        }

        var period = await db.FiscalPeriods.SingleAsync(p => p.FiscalYearId == year.Id && !p.IsAdjustment && p.StartsOn <= postingDate && p.EndsOn >= postingDate, cancellationToken);
        var row = await db.PeriodStates.SingleOrDefaultAsync(s => s.PeriodId == period.Id && s.CompanyId == company.Id && s.Module == normalizedModule, cancellationToken);
        var info = new PeriodInfo(period.Id, year.Id, year.Code, period.Number, period.StartsOn, period.EndsOn, period.IsAdjustment, year.Status);
        return new PeriodState(info, normalizedModule, row?.State ?? DefaultState(year));
    }

    public async Task<Result<PeriodInfo>> FirstOpenPeriodAsync(CompanyId companyId, DateOnly fromDate, string module, CancellationToken cancellationToken = default)
    {
        var normalizedModule = module?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!PostingModules.IsValid(normalizedModule))
        {
            return Error.Validation("period.module_invalid", $"Modules are one of {string.Join(", ", PostingModules.All)}.");
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId.Value, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId.Value);
        }

        var years = await db.FiscalYears.Where(y => y.CalendarId == company.FiscalCalendarId && y.EndsOn >= fromDate).OrderBy(static y => y.StartsOn).ToListAsync(cancellationToken);
        var yearIds = years.Select(static y => y.Id).ToList();
        var periods = await db.FiscalPeriods.Where(p => yearIds.Contains(p.FiscalYearId) && !p.IsAdjustment && p.EndsOn >= fromDate).OrderBy(static p => p.StartsOn).ToListAsync(cancellationToken);
        var periodIds = periods.Select(static p => p.Id).ToList();
        var states = await db.PeriodStates.Where(s => periodIds.Contains(s.PeriodId) && s.CompanyId == company.Id && s.Module == normalizedModule).ToDictionaryAsync(static s => s.PeriodId, static s => s.State, cancellationToken);
        foreach (var period in periods)
        {
            var year = years.Single(y => y.Id == period.FiscalYearId);
            if ((states.GetValueOrDefault(period.Id) ?? DefaultState(year)) == PeriodStates.Open)
            {
                return new PeriodInfo(period.Id, year.Id, year.Code, period.Number, period.StartsOn, period.EndsOn, period.IsAdjustment, year.Status);
            }
        }

        return Error.Conflict("period.none_open", $"No period of company {company.Code} on or after {fromDate:yyyy-MM-dd} is open for {normalizedModule}.")
            .WithWhy(("company", company.Code), ("from", fromDate), ("module", normalizedModule));
    }

    // ------------------------------------------------------------------ mapping

    private IQueryable<FiscalCalendar> Query() => db.FiscalCalendars.Include(static c => c.Years).ThenInclude(static y => y.Periods).AsSplitQuery();

    private static FiscalCalendarSummary Map(FiscalCalendar c) => new(c.Id, c.Code, c.Name.Values, c.StartMonth, c.PeriodsPerYear, c.IsSystem,
        c.Years.OrderBy(static y => y.StartsOn).Select(Map).ToList());

    private static FiscalYearSummary Map(FiscalYear y) => new(y.Id, y.CalendarId, y.Code, y.StartsOn, y.EndsOn, y.Status,
        y.Periods.OrderBy(static p => p.Number).Select(static p => new FiscalPeriodSummary(p.Id, p.Number, p.StartsOn, p.EndsOn, p.IsAdjustment)).ToList());
}

using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;

namespace Quicker.Organization.Application;

/// <summary>Working weeks and public holidays per calendar; due-date arithmetic for companies (A-005).</summary>
public sealed class BusinessCalendarService(OrganizationDbContext db, IClock clock) : IWorkingDayCalendar
{
    public async Task<IReadOnlyList<BusinessCalendarSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await Query().OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<BusinessCalendarSummary?> GetAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        return calendar is null ? null : Map(calendar);
    }

    public async Task<Result<BusinessCalendarSummary>> CreateAsync(SaveBusinessCalendarRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = Validate(request);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, name, days) = validated.Value;
        if (await db.BusinessCalendars.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict("business_calendar.code_taken", $"A business calendar with code '{code}' already exists.");
        }

        var now = clock.UtcNow;
        var calendar = new BusinessCalendar { Id = Guid.CreateVersion7(), Code = code, Name = name, WorkingDays = days, CreatedAt = now, UpdatedAt = now };
        db.BusinessCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return Map(calendar);
    }

    public async Task<Result<BusinessCalendarSummary>> UpdateAsync(Guid calendarId, SaveBusinessCalendarRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        if (calendar is null)
        {
            return Error.NotFound("business_calendar", calendarId);
        }

        var validated = Validate(request);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, name, days) = validated.Value;
        if (calendar.IsSystem && code != calendar.Code)
        {
            return Error.Forbidden("business_calendar.system_locked", "System calendars keep their code.");
        }

        if (await db.BusinessCalendars.AnyAsync(c => c.Code == code && c.Id != calendarId, cancellationToken))
        {
            return Error.Conflict("business_calendar.code_taken", $"A business calendar with code '{code}' already exists.");
        }

        calendar.Code = code;
        calendar.Name = name;
        calendar.WorkingDays = days;
        calendar.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(calendar);
    }

    public async Task<Result<HolidaySummary>> AddHolidayAsync(Guid calendarId, SaveHolidayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        if (calendar is null)
        {
            return Error.NotFound("business_calendar", calendarId);
        }

        var name = Validation.Name(request.Name, "holiday");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var existing = calendar.Holidays.Find(h => h.OnDate == request.OnDate);
        if (existing is not null)
        {
            existing.Name = name.Value;
            await db.SaveChangesAsync(cancellationToken);
            return Map(existing);
        }

        var holiday = new Holiday { Id = Guid.CreateVersion7(), CalendarId = calendarId, OnDate = request.OnDate, Name = name.Value };
        calendar.Holidays.Add(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return Map(holiday);
    }

    public async Task<Result> RemoveHolidayAsync(Guid calendarId, Guid holidayId, CancellationToken cancellationToken)
    {
        var holiday = await db.Holidays.SingleOrDefaultAsync(h => h.CalendarId == calendarId && h.Id == holidayId, cancellationToken);
        if (holiday is null)
        {
            return Error.NotFound("holiday", holidayId);
        }

        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary><c>working</c>: add N working days; <c>calendar</c>: add N calendar days then move to the next working day (due dates).</summary>
    public async Task<Result<WorkingDayComputation>> ComputeAsync(Guid calendarId, DateOnly from, int days, string mode, CancellationToken cancellationToken)
    {
        var calendar = await Query().SingleOrDefaultAsync(c => c.Id == calendarId, cancellationToken);
        if (calendar is null)
        {
            return Error.NotFound("business_calendar", calendarId);
        }

        return Compute(calendar, from, days, mode);
    }

    public async Task<Result<WorkingDayComputation>> ComputeForCompanyAsync(Guid companyId, DateOnly from, int days, string mode, CancellationToken cancellationToken)
    {
        var calendar = await ForCompanyAsync(new CompanyId(companyId), cancellationToken);
        return calendar is null ? Error.NotFound("company", companyId) : Compute(calendar, from, days, mode);
    }

    private static Result<WorkingDayComputation> Compute(BusinessCalendar calendar, DateOnly from, int days, string mode)
    {
        var normalized = Validation.OneOf(mode, "working_days.mode", "working", "calendar");
        if (normalized.IsFailure)
        {
            return normalized.Error!;
        }

        if (days < 0)
        {
            return Error.Validation("working_days.negative", "Days must not be negative.");
        }

        var result = normalized.Value == "working" ? calendar.AddWorkingDays(from, days) : calendar.NextWorkingDay(from.AddDays(days));
        return new WorkingDayComputation(from, calendar.IsWorkingDay(from), days, normalized.Value, result);
    }

    // ------------------------------------------------------------------ contract

    public async Task<bool> IsWorkingDayAsync(CompanyId companyId, DateOnly date, CancellationToken cancellationToken = default) =>
        (await RequireAsync(companyId, cancellationToken)).IsWorkingDay(date);

    public async Task<DateOnly> NextWorkingDayAsync(CompanyId companyId, DateOnly date, CancellationToken cancellationToken = default) =>
        (await RequireAsync(companyId, cancellationToken)).NextWorkingDay(date);

    public async Task<DateOnly> AddWorkingDaysAsync(CompanyId companyId, DateOnly date, int workingDays, CancellationToken cancellationToken = default) =>
        (await RequireAsync(companyId, cancellationToken)).AddWorkingDays(date, workingDays);

    public async Task<DateOnly> DueDateAsync(CompanyId companyId, DateOnly from, int calendarDays, CancellationToken cancellationToken = default) =>
        (await RequireAsync(companyId, cancellationToken)).NextWorkingDay(from.AddDays(calendarDays));

    private async Task<BusinessCalendar> RequireAsync(CompanyId companyId, CancellationToken cancellationToken) =>
        await ForCompanyAsync(companyId, cancellationToken) ?? throw new KeyNotFoundException($"Company {companyId.Value} was not found in this tenant.");

    private async Task<BusinessCalendar?> ForCompanyAsync(CompanyId companyId, CancellationToken cancellationToken)
    {
        var calendarId = await db.Companies.Where(c => c.Id == companyId.Value).Select(static c => (Guid?)c.BusinessCalendarId).SingleOrDefaultAsync(cancellationToken);
        return calendarId is null ? null : await Query().SingleAsync(c => c.Id == calendarId, cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private static Result<(string Code, Kernel.Text.LocalizedText Name, int[] Days)> Validate(SaveBusinessCalendarRequest request)
    {
        var code = Validation.LowerCode(request.Code, "business_calendar");
        var name = Validation.Name(request.Name, "business_calendar");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        var days = (request.WorkingDays ?? []).Distinct().OrderBy(static d => d).ToArray();
        return days.Length is 0 or > 7 || days.Any(static d => d is < 0 or > 6)
            ? Error.Validation("business_calendar.working_days_invalid", "Working days are 1 to 7 distinct values from 0 (Sunday) to 6 (Saturday).")
            : (code.Value, name.Value, days);
    }

    private IQueryable<BusinessCalendar> Query() => db.BusinessCalendars.Include(static c => c.Holidays);

    private static BusinessCalendarSummary Map(BusinessCalendar c) => new(c.Id, c.Code, c.Name.Values, c.WorkingDays, c.IsSystem, c.Holidays.OrderBy(static h => h.OnDate).Select(Map).ToList());

    private static HolidaySummary Map(Holiday h) => new(h.Id, h.OnDate, h.Name.Values);
}

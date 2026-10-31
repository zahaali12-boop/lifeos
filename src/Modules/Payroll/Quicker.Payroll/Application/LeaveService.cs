using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Partners.Contracts;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Domain;
using Quicker.Payroll.Persistence;

namespace Quicker.Payroll.Application;

/// <summary>Leave (A-181): annual, sick or unpaid, recorded as approved; unpaid days reduce the basic pay of the period they fall in; annual days count against the yearly entitlement.</summary>
public sealed class LeaveService(PayrollDbContext db, IPartnerDirectory partners, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public const string AuditEntity = "leave";

    public async Task<IReadOnlyList<LeaveSummary>> ListAsync(Guid companyId, Guid? employeeId, int? year, CancellationToken cancellationToken)
    {
        var query = db.Leaves.AsNoTracking().Where(l => l.CompanyId == companyId);
        if (employeeId is { } e)
        {
            query = query.Where(l => l.EmployeeId == e);
        }

        if (year is { } y)
        {
            var start = new DateOnly(y, 1, 1);
            var end = new DateOnly(y, 12, 31);
            query = query.Where(l => l.FromDate <= end && l.ToDate >= start);
        }

        var leaves = await query.OrderByDescending(static l => l.FromDate).Take(1000).ToListAsync(cancellationToken);
        var employeeIds = leaves.Select(static l => l.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(x => employeeIds.Contains(x.Id)).ToDictionaryAsync(static x => x.Id, cancellationToken);
        var result = new List<LeaveSummary>(leaves.Count);
        foreach (var leave in leaves)
        {
            result.Add(await MapAsync(leave, employees.GetValueOrDefault(leave.EmployeeId), cancellationToken));
        }

        return result;
    }

    public async Task<Result<LeaveSummary>> RecordAsync(SaveLeaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = Validation.OneOf(request.Kind, "leave.kind", LeaveKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (request.ToDate < request.FromDate)
        {
            return Error.Validation("leave.dates", "Leave ends on or after it starts.");
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return Error.NotFound(EmployeeService.AuditEntity, request.EmployeeId);
        }

        var calendarDays = request.ToDate.DayNumber - request.FromDate.DayNumber + 1;
        var days = request.Days ?? calendarDays;
        if (days <= 0m || days > calendarDays)
        {
            return Error.Validation("leave.days_invalid", "The days of leave are above zero and at most the calendar days between the dates.").WithWhy(("days", days), ("calendarDays", calendarDays));
        }

        if (await db.Leaves.AnyAsync(l => l.EmployeeId == employee.Id && l.Status == "approved" && l.FromDate <= request.ToDate && l.ToDate >= request.FromDate, cancellationToken))
        {
            return Error.Conflict("leave.overlaps", "The employee already has leave in those days.");
        }

        var now = clock.UtcNow;
        var leave = new Leave
        {
            Id = Guid.CreateVersion7(),
            CompanyId = employee.CompanyId,
            EmployeeId = employee.Id,
            Kind = kind.Value,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            Days = days,
            Status = "approved",
            Notes = Validation.Trim(request.Notes),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Leaves.Add(leave);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, leave.Id, $"{leave.Kind} {leave.FromDate:yyyy-MM-dd}", AuditActions.Created, After: new { employee = employee.Number, leave.Kind, leave.FromDate, leave.ToDate, leave.Days }, CompanyId: leave.CompanyId), cancellationToken);
        return await MapAsync(leave, employee, cancellationToken);
    }

    public async Task<Result<LeaveSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var leave = await db.Leaves.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (leave is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (leave.Status != "approved")
        {
            return Error.Conflict("leave.not_approved", "The leave is already cancelled.");
        }

        // Leave inside a period that was already paid stays: the run that paid it is the record of what was deducted.
        var paid = await db.Runs.AnyAsync(r => r.CompanyId == leave.CompanyId && (r.Status == PayrollRunStatuses.Posted || r.Status == PayrollRunStatuses.Paid) && r.PeriodStart <= leave.ToDate && r.PeriodEnd >= leave.FromDate, cancellationToken);
        if (paid && leave.Kind == LeaveKinds.Unpaid)
        {
            return Error.Conflict("leave.period_paid", "The period of this leave has been paid; reverse the run first.");
        }

        leave.Status = "cancelled";
        leave.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, leave.Id, $"{leave.Kind} {leave.FromDate:yyyy-MM-dd}", AuditActions.StateChanged, After: new { status = leave.Status }, CompanyId: leave.CompanyId), cancellationToken);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == leave.EmployeeId, cancellationToken);
        return await MapAsync(leave, employee, cancellationToken);
    }

    private async Task<LeaveSummary> MapAsync(Leave l, Employee? employee, CancellationToken cancellationToken)
    {
        var partner = employee is null ? null : await partners.FindAsync(employee.PartnerId, cancellationToken);
        return new LeaveSummary(l.Id, l.CompanyId, l.EmployeeId, employee?.Number ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.Kind, l.FromDate, l.ToDate, l.Days, l.Status, l.Notes, l.CreatedAt);
    }
}

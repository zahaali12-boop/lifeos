using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Domain;
using Quicker.Payroll.Persistence;

namespace Quicker.Payroll.Application;

/// <summary>
/// Employees (A-181): a partner with the employee role, hired into a company with a job, a basic salary in the
/// company's currency, the components it carries and how it is paid. The net pay is paid through Banking as a
/// supplier payment, so an employee that is to be paid also has a supplier account in the company.
/// </summary>
public sealed class EmployeeService(
    PayrollDbContext db,
    ICompanyDirectory companies,
    IPartnerDirectory partners,
    IDimensionDirectory dimensions,
    IDimensionSets dimensionSets,
    INumberAllocator numbering,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = PayrollDocumentTypes.Employee;

    public async Task<IReadOnlyList<EmployeeSummary>> ListAsync(Guid companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking().Include(static e => e.Components).Where(e => e.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(e => e.Status == status);
        }

        var employees = await query.OrderBy(static e => e.Number).ToListAsync(cancellationToken);
        var result = new List<EmployeeSummary>(employees.Count);
        foreach (var employee in employees)
        {
            result.Add(await MapAsync(employee, cancellationToken));
        }

        return result;
    }

    public async Task<Result<EmployeeSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().Include(static e => e.Components).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return employee is null ? Error.NotFound(AuditEntity, id) : await MapAsync(employee, cancellationToken);
    }

    public async Task<Result<EmployeeSummary>> SaveAsync(Guid? id, SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var partner = await partners.FindAsync(request.PartnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound("partner", request.PartnerId);
        }

        if (!partner.IsEmployee)
        {
            return Error.Validation("employee.partner_not_employee", "The partner does not have the employee role; give it the role first.").WithWhy(("partner", partner.Code));
        }

        if (request.BasicSalary < 0m || !new Money(request.BasicSalary, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("employee.basic_salary_invalid", "The basic salary is zero or above, in the company's currency.");
        }

        if (request.TerminationDate is { } ended && ended < request.HireDate)
        {
            return Error.Validation("employee.termination_before_hire", "The termination date is on or after the hire date.");
        }

        if (request.PaymentMethod is not ("transfer" or "cash"))
        {
            return Error.Validation("employee.payment_method_invalid", "An employee is paid by transfer or in cash.").WithWhy(("paymentMethod", request.PaymentMethod));
        }

        if (request.LeaveEntitlementDays is < 0 or > 365)
        {
            return Error.Validation("employee.leave_entitlement_invalid", "The yearly leave entitlement is between 0 and 365 days.");
        }

        if (request.BranchId is { } branchId)
        {
            var branch = await companies.FindBranchAsync(new BranchId(branchId), cancellationToken);
            if (branch is null || branch.CompanyId.Value != request.CompanyId)
            {
                return Error.NotFound("branch", branchId);
            }
        }

        Guid? dimensionSetId = null;
        if (request.DepartmentValueId is { } valueId)
        {
            var value = await dimensions.FindValueAsync(valueId, cancellationToken);
            var dimension = value is null ? null : (await dimensions.ListAsync(cancellationToken)).FirstOrDefault(d => d.Id == value.DimensionId);
            if (value is null || dimension is null)
            {
                return Error.NotFound("dimension_value", valueId);
            }

            var set = await dimensionSets.GetOrCreateAsync(new Dictionary<string, Guid>(StringComparer.Ordinal) { [dimension.Code] = value.Id }, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error!;
            }

            dimensionSetId = set.Value;
        }

        var componentIds = (request.Components ?? []).Select(static c => c.ComponentId).Distinct().ToList();
        var components = await db.Components.AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        foreach (var c in request.Components ?? [])
        {
            if (!components.TryGetValue(c.ComponentId, out var component) || component.CompanyId != request.CompanyId)
            {
                return Error.NotFound(ComponentService.AuditEntity, c.ComponentId);
            }

            if (component.Calculation == PayComponentCalculations.Basic)
            {
                return Error.Validation("employee.basic_not_assignable", "The basic salary is the employee's own field, not a component to assign.");
            }

            if (c.Amount is < 0m || c.RatePct is < 0m or > 100m)
            {
                return Error.Validation("employee.component_amount_invalid", "A component's amount is zero or above and its rate between 0 and 100 percent.").WithWhy(("component", component.Code));
            }

            if (c.ValidFrom is { } from && c.ValidTo is { } to && to < from)
            {
                return Error.Validation("employee.component_dates", "A component's validity ends on or after it starts.").WithWhy(("component", component.Code));
            }
        }

        Employee? employee = null;
        if (id is { } existing)
        {
            employee = await db.Employees.Include(static e => e.Components).SingleOrDefaultAsync(e => e.Id == existing, cancellationToken);
            if (employee is null)
            {
                return Error.NotFound(AuditEntity, existing);
            }

            if (employee.CompanyId != request.CompanyId)
            {
                return Error.Validation("employee.company_fixed", "An employee stays in the company that hired them.");
            }
        }

        if (await db.Employees.AnyAsync(e => e.CompanyId == request.CompanyId && e.PartnerId == request.PartnerId && (employee == null || e.Id != employee.Id), cancellationToken))
        {
            return Error.Conflict("employee.already_hired", "The partner is already an employee of this company.").WithWhy(("partner", partner.Code));
        }

        var before = employee is null ? null : Snapshot(employee);
        var now = clock.UtcNow;
        employee ??= new Employee { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = now };
        if (employee.Number.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(request.Number))
            {
                var code = Validation.UpperCode(request.Number, "employee");
                if (code.IsFailure)
                {
                    return code.Error!;
                }

                if (await db.Employees.AnyAsync(e => e.Number == code.Value, cancellationToken))
                {
                    return Error.Conflict("employee.number_taken", $"Employee number '{code.Value}' is already used.");
                }

                employee.Number = code.Value;
            }
            else
            {
                await numbering.EnsureDefaultSeriesAsync(PayrollDocumentTypes.Employee, company.Id, "EMP", "EMP-{seq:4}", "never", cancellationToken);
                var number = await numbering.AllocateAsync(new NumberRequest(PayrollDocumentTypes.Employee, company.Id, null, request.HireDate, employee.Id), cancellationToken);
                if (number.IsFailure)
                {
                    return number.Error!;
                }

                employee.Number = number.Value.Text;
            }
        }

        employee.PartnerId = request.PartnerId;
        employee.BranchId = request.BranchId;
        employee.DepartmentValueId = request.DepartmentValueId;
        employee.DimensionSetId = dimensionSetId;
        employee.JobTitle = Validation.Text(request.JobTitle);
        employee.HireDate = request.HireDate;
        employee.TerminationDate = request.TerminationDate;
        employee.BasicSalary = request.BasicSalary;
        employee.Currency = company.FunctionalCurrency.Code;
        employee.PaymentMethod = request.PaymentMethod;
        employee.BankName = Validation.Trim(request.BankName);
        employee.Iban = Validation.Trim(request.Iban);
        employee.NationalId = Validation.Trim(request.NationalId);
        employee.LeaveEntitlementDays = request.LeaveEntitlementDays;
        employee.Status = request.TerminationDate is { } t && t <= clock.TodayIn(company.TimeZone) ? "left" : "active";
        employee.Notes = Validation.Trim(request.Notes);
        employee.UpdatedAt = now;
        employee.Components.Clear();
        foreach (var c in request.Components ?? [])
        {
            employee.Components.Add(new EmployeeComponent { Id = Guid.CreateVersion7(), EmployeeId = employee.Id, ComponentId = c.ComponentId, Amount = c.Amount, RatePct = c.RatePct, ValidFrom = c.ValidFrom, ValidTo = c.ValidTo });
        }

        if (id is null)
        {
            db.Employees.Add(employee);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, employee.Id, employee.Number, id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(employee), CompanyId: employee.CompanyId), cancellationToken);
        return await MapAsync(employee, cancellationToken);
    }

    internal async Task<EmployeeSummary> MapAsync(Employee e, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(e.PartnerId, cancellationToken);
        var supplier = await partners.FindSupplierAsync(e.CompanyId, e.PartnerId, cancellationToken);
        var department = e.DepartmentValueId is { } valueId ? await dimensions.FindValueAsync(valueId, cancellationToken) : null;
        var componentIds = e.Components.Select(static c => c.ComponentId).ToList();
        var components = componentIds.Count == 0 ? new Dictionary<Guid, PayComponent>() : await db.Components.AsNoTracking().Where(c => componentIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(e.CompanyId), cancellationToken);
        var year = clock.TodayIn(company?.TimeZone ?? "UTC").Year;
        var yearStart = new DateOnly(year, 1, 1);
        var yearEnd = new DateOnly(year, 12, 31);
        var taken = await db.Leaves.AsNoTracking().Where(l => l.EmployeeId == e.Id && l.Kind == LeaveKinds.Annual && l.Status == "approved" && l.FromDate <= yearEnd && l.ToDate >= yearStart).SumAsync(static l => l.Days, cancellationToken);
        var assigned = e.Components.Select(c =>
        {
            var component = components.GetValueOrDefault(c.ComponentId);
            return new EmployeeComponentSummary(c.Id, c.ComponentId, component?.Code ?? string.Empty, component?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), component?.Kind ?? string.Empty, component?.Calculation ?? string.Empty, c.Amount, c.RatePct, c.ValidFrom, c.ValidTo);
        }).ToList();
        return new EmployeeSummary(e.Id, e.CompanyId, e.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), e.Number, e.BranchId, e.DepartmentValueId, department?.Code, e.JobTitle.Values,
            e.HireDate, e.TerminationDate, e.BasicSalary, e.Currency, e.PaymentMethod, e.BankName, e.Iban, e.NationalId, e.LeaveEntitlementDays, taken, e.LeaveEntitlementDays - taken, e.Status, e.Notes, supplier is not null && supplier.IsActive, assigned, e.CreatedAt, e.UpdatedAt);
    }

    private static object Snapshot(Employee e) => new { e.Number, e.PartnerId, e.BranchId, e.DepartmentValueId, jobTitle = e.JobTitle, e.HireDate, e.TerminationDate, e.BasicSalary, e.PaymentMethod, e.LeaveEntitlementDays, e.Status, components = e.Components.Select(static c => new { c.ComponentId, c.Amount, c.RatePct }).ToList() };
}

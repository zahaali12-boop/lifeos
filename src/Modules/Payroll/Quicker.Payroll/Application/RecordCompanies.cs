using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Payroll.Application;

/// <summary>The company of pay components, employees, leave and runs, so what hangs off them follows the member's company scopes.</summary>
internal sealed class PayrollRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        [ComponentService.AuditEntity] = "app.pay_components",
        [EmployeeService.AuditEntity] = "app.pay_employees",
        [LeaveService.AuditEntity] = "app.pay_leaves",
        [RunService.AuditEntity] = "app.pay_runs",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

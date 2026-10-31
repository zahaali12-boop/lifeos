using Dapper;
using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Reporting.Application.Reports;

/// <summary>The company a saved report was saved for, so what hangs off it (comments, files, timeline) follows the member's company scopes.</summary>
internal sealed class ReportingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    public IReadOnlyCollection<string> EntityTypes { get; } = [ReportService.AuditEntity];

    public async Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        // A report kept without a company belongs to no company in particular and is left out, as a missing record is.
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(Guid Id, Guid CompanyId)>(new CommandDefinition(
            "SELECT id, company_id FROM app.rpt_reports WHERE id = ANY(@ids) AND company_id IS NOT NULL", new { ids = ids.Distinct().ToArray() }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => r.Id, static r => r.CompanyId);
    }
}

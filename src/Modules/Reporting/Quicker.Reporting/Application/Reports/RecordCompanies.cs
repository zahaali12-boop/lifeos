using Dapper;
using Quicker.Collaboration.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Application.Statements;

namespace Quicker.Reporting.Application.Reports;

/// <summary>The company a saved report or a statement layout was kept for, so what hangs off it (comments, files, timeline) follows the member's company scopes.</summary>
internal sealed class ReportingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        [ReportService.AuditEntity] = "app.rpt_reports",
        [LayoutService.AuditEntity] = "app.rpt_financial_layouts",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public async Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        // A record kept for every company belongs to no company in particular and is left out, as a missing record is.
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(Guid Id, Guid CompanyId)>(new CommandDefinition(
            $"SELECT id, company_id FROM {Tables[entityType]} WHERE id = ANY(@ids) AND company_id IS NOT NULL", new { ids = ids.Distinct().ToArray() }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => r.Id, static r => r.CompanyId);
    }
}

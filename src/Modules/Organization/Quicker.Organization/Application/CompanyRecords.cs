using Dapper;
using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Organization.Application;

/// <summary>A company is its own company for record scopes: a role limited to other companies does not see its discussion.</summary>
internal sealed class CompanyRecords(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    public IReadOnlyCollection<string> EntityTypes { get; } = ["company"];

    public async Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var found = await uow.Connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM app.org_companies WHERE id = ANY(@ids)", new { ids = ids.Distinct().ToArray() }, uow.Transaction, cancellationToken: cancellationToken));
        return found.ToDictionary(static id => id, static id => id);
    }
}

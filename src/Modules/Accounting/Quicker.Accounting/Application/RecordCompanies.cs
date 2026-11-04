using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Accounting.Application;

/// <summary>The company of manual journals, so what hangs off them follows the member's company scopes.</summary>
internal sealed class AccountingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["manual_journal"] = "app.gl_manual_journals",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

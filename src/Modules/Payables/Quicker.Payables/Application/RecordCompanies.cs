using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Payables.Application;

/// <summary>The company of payment proposals, so what hangs off them follows the member's company scopes.</summary>
internal sealed class PayablesRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["payment_proposal"] = "app.ap_payment_proposals",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

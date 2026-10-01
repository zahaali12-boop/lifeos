using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Banking.Application;

/// <summary>The company of bank payments and bank accounts, so what hangs off them follows the member's company scopes.</summary>
internal sealed class BankingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["bank_payment"] = "app.bnk_payments",
        ["bank_account"] = "app.bnk_bank_accounts",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

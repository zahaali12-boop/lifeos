using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Expenses.Application;

/// <summary>The company of expenses, funds and replenishments, so what hangs off them follows the member's company scopes.</summary>
internal sealed class ExpensesRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["expense"] = "app.exp_expenses",
        ["petty_cash_fund"] = "app.exp_funds",
        ["fund_replenishment"] = "app.exp_replenishments",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

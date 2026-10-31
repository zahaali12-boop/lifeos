using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Banking.Application;

/// <summary>The company of bank payments, bank accounts, cheques, deposit slips, cheque books, statements and reconciliations, so what hangs off them follows the member's company scopes.</summary>
internal sealed class BankingRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["bank_payment"] = "app.bnk_payments",
        ["bank_receipt"] = "app.bnk_payments",
        ["bank_account"] = "app.bnk_bank_accounts",
        ["bank_cheque"] = "app.bnk_cheques",
        ["bank_cheque_deposit"] = "app.bnk_cheque_deposits",
        ["bank_cheque_book"] = "app.bnk_cheque_books",
        ["bank_statement"] = "app.bnk_statements",
        ["bank_reconciliation"] = "app.bnk_reconciliations",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

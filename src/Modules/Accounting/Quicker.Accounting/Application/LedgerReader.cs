using Dapper;
using Quicker.Accounting.Contracts;
using Quicker.Persistence;

namespace Quicker.Accounting.Application;

/// <summary>
/// Read-only movement on the tax accounts of the general ledger, for the tax module to reconcile a return to the
/// books (ADR-0018): every posted line that carries a tax code, net debit − credit by account role and code.
/// </summary>
public sealed class LedgerReader(IUnitOfWorkAccessor unitOfWork) : ILedgerReader
{
    public async Task<IReadOnlyList<TaxAccountMovement>> TaxMovementAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<TaxAccountMovement>(new CommandDefinition("""
            SELECT tax_code_id AS "TaxCodeId", account_role AS "AccountRole", coalesce(sum(debit_fc - credit_fc), 0) AS "AmountFc"
            FROM app.gl_journal_lines
            WHERE tenant_id = @tenant AND company_id = @company AND tax_code_id IS NOT NULL AND posting_date >= @from::date AND posting_date <= @to::date
            GROUP BY tax_code_id, account_role
            """, new { tenant = uow.Context.TenantId.Value, company = companyId, from, to }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToList();
    }
}

using System.Text.Json;
using Dapper;
using Quicker.Accounting.Contracts;
using Quicker.Persistence;

namespace Quicker.Accounting.Application;

/// <summary>
/// Read-only movement of the general ledger for other modules (ADR-0018): the tax accounts for the tax module to
/// reconcile a return to the books (every posted line that carries a tax code, net debit − credit by account role and
/// code), and every account's monthly debits and credits for a statement in another module's words (the trader's
/// income statement, ADR-0035 §8), closing entries left out as the profit and loss leaves them out.
/// </summary>
public sealed class LedgerReader(IUnitOfWorkAccessor unitOfWork) : ILedgerReader
{
    private static readonly IReadOnlyDictionary<string, string> NoName = new Dictionary<string, string>(StringComparer.Ordinal);

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

    public async Task<IReadOnlyList<AccountMonthMovement>> MonthlyMovementAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<MonthRow>(new CommandDefinition("""
            SELECT a.id AS "AccountId", a.code AS "Code", a.name_i18n::text AS "Name", a.type AS "Type", a.subtype AS "Subtype",
                   date_trunc('month', l.posting_date)::date AS "Month", coalesce(sum(l.debit_fc), 0) AS "DebitFc", coalesce(sum(l.credit_fc), 0) AS "CreditFc"
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            JOIN app.gl_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.account_id
            WHERE l.tenant_id = @tenant AND l.company_id = @company AND l.posting_date >= @from::date AND l.posting_date <= @to::date AND NOT e.is_closing_entry
            GROUP BY a.id, a.code, a.name_i18n, a.type, a.subtype, date_trunc('month', l.posting_date)
            ORDER BY a.code, date_trunc('month', l.posting_date)
            """, new { tenant = uow.Context.TenantId.Value, company = companyId, from, to }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(static r => new AccountMonthMovement(r.AccountId, r.Code, ParseName(r.Name), r.Type, r.Subtype, r.Month, r.DebitFc, r.CreditFc)).ToList();
    }

    private static IReadOnlyDictionary<string, string> ParseName(string? json) =>
        string.IsNullOrWhiteSpace(json) ? NoName : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? NoName;

    private sealed record MonthRow(Guid AccountId, string Code, string? Name, string Type, string Subtype, DateOnly Month, decimal DebitFc, decimal CreditFc);
}

using Quicker.Banking.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Trader.Application;

/// <summary>
/// The cash and bank screen (ADR-0035 §18, A-201): every cash box and bank account of the company over a period, from
/// the bank subledger alone. Per account: the balance now, what it held the day before the period began, what came in
/// and went out, what it held at the period's end, and the movements newest first with the balance after each one
/// (the period's closing balance less the movements above it).
/// </summary>
public sealed class CashService(ICompanyDirectory companies, IBankAccountDirectory banks, IBankTransactions transactions, IClock clock)
{
    public async Task<Result<TraderCashReport>> ReadAsync(Guid companyId, DateOnly? from, DateOnly? to, Guid? bankAccountId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var today = clock.TodayIn(company.TimeZone);
        var until = to ?? today;
        if (from is { } start && start > until)
        {
            return Error.Validation("cash.period_invalid", "The period ends before it begins.").WithWhy(("from", start), ("to", until));
        }

        var accounts = new List<TraderCashReportAccount>();
        foreach (var account in (await banks.ListAsync(companyId, cancellationToken)).Where(a => bankAccountId is null || a.Id == bankAccountId).OrderBy(static a => a.Code, StringComparer.Ordinal))
        {
            var balanceNow = await transactions.BalanceAsync(account.Id, null, cancellationToken);
            var closing = await transactions.BalanceAsync(account.Id, until, cancellationToken);
            var opening = from is { } f ? await transactions.BalanceAsync(account.Id, f.AddDays(-1), cancellationToken) : 0m;
            var moves = await transactions.MovementsAsync(account.Id, from, until, cancellationToken);
            var running = closing;
            var movements = new List<TraderCashMovement>(moves.Count);
            foreach (var move in moves)
            {
                movements.Add(new TraderCashMovement(move.Id, move.PostingDate, move.Kind, move.AmountTc > 0m ? move.AmountTc : 0m, move.AmountTc < 0m ? -move.AmountTc : 0m, running, move.Reference, move.SourceDocumentType, move.SourceDocumentId));
                running -= move.AmountTc;
            }

            accounts.Add(new TraderCashReportAccount(account.Id, account.Code, account.Name.Values, account.Kind, account.Currency, account.IsActive, balanceNow, opening, movements.Sum(static m => m.In), movements.Sum(static m => m.Out), closing, movements));
        }

        return new TraderCashReport(companyId, company.FunctionalCurrency.Code, today, from, until, accounts);
    }
}

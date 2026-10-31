using Quicker.Kernel.Results;
using Quicker.Onboarding.Contracts;
using Quicker.Web.Imports;

namespace Quicker.Onboarding.Application;

/// <summary>
/// The opening-balance sheet of the setup wizard (A-173): one row per account balance, a partner on the rows of a
/// control account so the open items are born with the balances, a bank account or cash box on a cash line. Columns:
/// account, debit, credit, partner, bank_account, due_date, description. A trial balance pasted from a spreadsheet is enough; what does not balance goes to opening balance
/// equity and is reported back.
/// </summary>
public static class OpeningBalanceCsv
{
    public static readonly IReadOnlyList<string> Columns = ["account", "debit", "credit", "partner", "bank_account", "due_date", "description"];

    public static Result<IReadOnlyList<OpeningBalanceRow>> Read(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var rows = new List<OpeningBalanceRow>(sheet.Rows.Count);
        foreach (var row in sheet.Rows)
        {
            var debit = row.Decimal("debit");
            if (debit.IsFailure)
            {
                return debit.Error!;
            }

            var credit = row.Decimal("credit");
            if (credit.IsFailure)
            {
                return credit.Error!;
            }

            var due = row.Date("due_date");
            if (due.IsFailure)
            {
                return due.Error!;
            }

            rows.Add(new OpeningBalanceRow(row.Get("account") ?? string.Empty, debit.Value ?? 0m, credit.Value ?? 0m, row.Get("partner"), row.Get("bank_account"), due.Value, row.Get("description")));
        }

        return rows;
    }
}

using Quicker.Accounting.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Kernel.Results;
using Quicker.Onboarding.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;

namespace Quicker.Onboarding.Application;

/// <summary>
/// Opening balances for a company from rows that name accounts, partners and bank accounts by code (setup wizard,
/// A-173): the codes are looked up, the lines handed to Accounting as one opening journal, created and posted. A dry
/// run does the same work inside a savepoint and rolls it back, so the file is checked as a whole (accounts,
/// partners, the period) and the difference opening balance equity would absorb is known before anything is kept.
/// </summary>
public sealed class OpeningBalanceService(IOpeningJournals journals, IPartnerDirectory partners, IBankAccountDirectory bankAccounts, IUnitOfWorkAccessor unitOfWork)
{
    public async Task<Result<OpeningBalanceResult>> ImportAsync(Guid companyId, DateOnly asOf, IReadOnlyList<OpeningBalanceRow> rows, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Error.Validation("import.empty", "Nothing to import.");
        }

        var lines = new List<OpeningJournalLine>(rows.Count);
        var partnerLines = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.IsNullOrWhiteSpace(row.Account))
            {
                return Error.Validation("import.account_required", $"Row {i + 1}: the account code is missing.").WithWhy(("row", i + 1));
            }

            if (row.Debit < 0m || row.Credit < 0m || (row.Debit > 0m && row.Credit > 0m) || (row.Debit == 0m && row.Credit == 0m))
            {
                return Error.Validation("import.amount_sides", $"Row {i + 1}: enter a debit or a credit, not both, never negative.").WithWhy(("row", i + 1), ("account", row.Account));
            }

            Guid? partnerId = null;
            if (!string.IsNullOrWhiteSpace(row.Partner))
            {
                var partner = await partners.FindByCodeAsync(row.Partner.Trim(), cancellationToken);
                if (partner is null)
                {
                    return Error.Validation("import.partner_unknown", $"Row {i + 1}: no partner has the code '{row.Partner}'.").WithWhy(("row", i + 1), ("partner", row.Partner));
                }

                partnerId = partner.Id;
                partnerLines++;
            }

            Guid? bankAccountId = null;
            if (!string.IsNullOrWhiteSpace(row.BankAccount))
            {
                var bankAccount = await bankAccounts.FindByCodeAsync(companyId, row.BankAccount.Trim(), cancellationToken);
                if (bankAccount is null)
                {
                    return Error.Validation("import.bank_account_unknown", $"Row {i + 1}: the company has no bank account or cash box with the code '{row.BankAccount}'.").WithWhy(("row", i + 1), ("bankAccount", row.BankAccount));
                }

                bankAccountId = bankAccount.Id;
            }

            lines.Add(new OpeningJournalLine(row.Account.Trim(), row.Debit, row.Credit, partnerId, bankAccountId, row.DueDate, row.Description));
        }

        const string savepoint = "opening_balances";
        var transaction = unitOfWork.Current.Transaction;
        if (dryRun)
        {
            await transaction.SaveAsync(savepoint, cancellationToken);
        }

        var posted = await journals.PostAsync(new OpeningJournalRequest(companyId, asOf, lines, "OPENING"), cancellationToken);
        if (dryRun)
        {
            await transaction.RollbackAsync(savepoint, cancellationToken);
        }

        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var result = posted.Value;
        return dryRun
            ? new OpeningBalanceResult(null, null, result.Debit, result.Credit, result.Difference, lines.Count, partnerLines, DryRun: true)
            : new OpeningBalanceResult(result.JournalId, result.Number, result.Debit, result.Credit, result.Difference, lines.Count, partnerLines);
    }
}

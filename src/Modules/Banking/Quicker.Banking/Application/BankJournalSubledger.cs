using Quicker.Accounting.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Banking.Application;

/// <summary>
/// The bank subledger behind journals (POSTING_RULES §8, A-140, A-194), as Business Central's bank account journal
/// line: a journal line on a bank or cash account names the bank account or cash box, and posting it records the
/// movement there (an opening balance for an opening journal, a receipt for a debit, a disbursement for a credit), so
/// the box's balance keeps equal to its ledger account. The line must post to the box's own account in the box's
/// currency; reversing the journal mirrors the movements.
/// </summary>
public sealed class BankJournalSubledger(IBankAccountDirectory bankAccounts, IBankTransactions transactions) : IJournalSubledger
{
    public const string DocumentType = "journal_entry";

    public string SubledgerType => SubledgerTypes.Bank;

    public async Task<Result> CheckAsync(Guid companyId, IReadOnlyList<JournalSubledgerLine> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            var bank = await bankAccounts.FindAsync(line.SubledgerRef, cancellationToken);
            if (bank is null || bank.CompanyId != companyId || !bank.IsActive)
            {
                return Error.Validation("journal.bank_account_required", $"Line {line.LineNo}: a line on a bank or cash account names the company's active bank account or cash box it moves.").WithWhy(("line", line.LineNo), ("subledgerRef", line.SubledgerRef));
            }

            if (line.AccountId != Guid.Empty && line.AccountId != bank.GlAccountId)
            {
                return Error.Validation("journal.bank_account_mismatch", $"Line {line.LineNo}: '{bank.Code}' keeps its balance on another ledger account; post the line there.").WithWhy(("line", line.LineNo), ("bankAccount", bank.Code));
            }

            if (line.Currency is { } currency && !string.Equals(currency, bank.Currency, StringComparison.Ordinal))
            {
                return Error.Validation("journal.bank_currency", $"Line {line.LineNo}: '{bank.Code}' is kept in {bank.Currency}; a journal in {currency} cannot move it.").WithWhy(("line", line.LineNo), ("bankAccount", bank.Code), ("bankCurrency", bank.Currency), ("currency", currency));
            }
        }

        return Result.Success();
    }

    public async Task PostedAsync(JournalSubledgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var line in entry.Lines)
        {
            var kind = entry.IsOpeningEntry ? "opening" : line.AmountTc >= 0m ? "receipt" : "disbursement";
            await transactions.RecordAsync(new NewBankTransaction(Guid.CreateVersion7(), line.SubledgerRef, entry.CompanyId, entry.PostingDate, kind, line.AmountTc, line.AmountFc, entry.Number, DocumentType, entry.EntryId, entry.EntryId), cancellationToken);
        }
    }

    public async Task<Result> ReverseAsync(Guid entryId, DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        await transactions.ReverseDocumentAsync(DocumentType, entryId, reversalDate, null, cancellationToken);
        return Result.Success();
    }
}

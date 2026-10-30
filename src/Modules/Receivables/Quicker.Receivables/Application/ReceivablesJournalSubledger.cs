using Quicker.Accounting.Contracts;
using Quicker.Kernel.Results;
using Quicker.Partners.Contracts;
using Quicker.Receivables.Contracts;

namespace Quicker.Receivables.Application;

/// <summary>
/// The receivables subledger behind journals (POSTING_RULES §8, A-140), the customer side of
/// <c>PayablesJournalSubledger</c>: a journal line on a receivables control account names the customer, and posting
/// it opens that customer's open item for the line (owed by the customer for a debit, owed to them for a credit) so
/// the control keeps equal to the open items. A line on the customer-deposits account opens a deposit, a line of an
/// opening journal an opening item. The items are applied like any other; reversing the journal reverses them,
/// refused while one is settled.
/// </summary>
public sealed class ReceivablesJournalSubledger(ReceivablesService receivables, IPartnerDirectory partners) : IJournalSubledger
{
    public const string DocumentType = "journal_entry";

    public string SubledgerType => SubledgerTypes.Receivables;

    public async Task<Result> CheckAsync(Guid companyId, IReadOnlyList<JournalSubledgerLine> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            var partner = await partners.FindAsync(line.SubledgerRef, cancellationToken);
            if (partner is null || !partner.IsCustomer)
            {
                return Error.Validation("journal.customer_required", $"Line {line.LineNo}: a line on the receivables control account names the customer it is owed by or to.").WithWhy(("line", line.LineNo), ("subledgerRef", line.SubledgerRef));
            }
        }

        return Result.Success();
    }

    public async Task PostedAsync(JournalSubledgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var line in entry.Lines)
        {
            var kind = string.Equals(line.AccountRole, AccountRoles.CustomerDeposits, StringComparison.Ordinal) ? ReceivableKinds.Deposit
                : entry.IsOpeningEntry ? ReceivableKinds.Opening
                : ReceivableKinds.Adjustment;
            await receivables.OpenAsync(new NewReceivable(entry.CompanyId, line.SubledgerRef, kind, DocumentType, entry.EntryId, entry.Number, line.LineNo, null,
                entry.PostingDate, entry.DocumentDate, line.DueDate ?? entry.PostingDate, null, 0m, entry.Currency, line.AmountTc, line.AmountFc, entry.RateTcFc, entry.EntryId, entry.BranchId), cancellationToken);
        }
    }

    public async Task<Result> ReverseAsync(Guid entryId, DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        var reversed = await receivables.ReverseDocumentAsync(DocumentType, entryId, reversalDate, cancellationToken);
        return reversed.IsSuccess ? Result.Success() : reversed.Error!;
    }
}

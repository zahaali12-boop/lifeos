using Quicker.Accounting.Contracts;
using Quicker.Kernel.Results;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;

namespace Quicker.Payables.Application;

/// <summary>
/// The payables subledger behind journals (POSTING_RULES §8, A-140), as Business Central's vendor journal line: a
/// journal line on a payables control account names the supplier, and posting it opens that supplier's open item for
/// the line (owed to the supplier for a credit, owed by them for a debit) so the control keeps equal to the open
/// items. A line on the supplier-advances account opens an advance, a line of an opening journal an opening item.
/// The items are applied like any other; reversing the journal reverses them, refused while one is settled.
/// </summary>
public sealed class PayablesJournalSubledger(PayablesService payables, IPartnerDirectory partners) : IJournalSubledger
{
    public const string DocumentType = "journal_entry";

    public string SubledgerType => SubledgerTypes.Payables;

    public async Task<Result> CheckAsync(Guid companyId, IReadOnlyList<JournalSubledgerLine> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            var partner = await partners.FindAsync(line.SubledgerRef, cancellationToken);
            if (partner is null || !partner.IsSupplier)
            {
                return Error.Validation("journal.supplier_required", $"Line {line.LineNo}: a line on the payables control account names the supplier it is owed to or by.").WithWhy(("line", line.LineNo), ("subledgerRef", line.SubledgerRef));
            }
        }

        return Result.Success();
    }

    public async Task PostedAsync(JournalSubledgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var line in entry.Lines)
        {
            var kind = string.Equals(line.AccountRole, AccountRoles.SupplierAdvances, StringComparison.Ordinal) ? PayableKinds.Advance
                : entry.IsOpeningEntry ? PayableKinds.Opening
                : PayableKinds.Adjustment;
            await payables.OpenAsync(new NewOpenItem(entry.CompanyId, line.SubledgerRef, kind, DocumentType, entry.EntryId, entry.Number, line.LineNo, null,
                entry.PostingDate, entry.DocumentDate, line.DueDate ?? entry.PostingDate, null, 0m, entry.Currency, -line.AmountTc, -line.AmountFc, entry.RateTcFc, entry.EntryId, entry.BranchId), cancellationToken);
        }
    }

    public async Task<Result> ReverseAsync(Guid entryId, DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        var reversed = await payables.ReverseDocumentAsync(DocumentType, entryId, reversalDate, cancellationToken);
        return reversed.IsSuccess ? Result.Success() : reversed.Error!;
    }
}

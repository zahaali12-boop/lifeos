using Microsoft.EntityFrameworkCore;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// The cheque behind a receipt or a payment by cheque (roadmap 6.3): drafted with the document, made live when the
/// document posts, returned or cancelled when it is reversed. Shared by the receipt and payment services, so neither
/// depends on the cheque life-cycle service.
/// </summary>
public sealed class ChequeRegistry(BankingDbContext db, IPartnerDirectory partners, ICompanyDirectory companies, ICurrentPrincipal principal, IClock clock)
{
    public const string Method = "cheque";

    public async Task<Cheque?> OfPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.Cheques.Include(static c => c.Events.OrderBy(static e => e.OccurredAt)).SingleOrDefaultAsync(c => c.PaymentId == paymentId, cancellationToken);

    /// <summary>Keeps the draft cheque in step with its draft document: created, updated, or removed when the method is no longer cheque.</summary>
    public async Task<Result> UpsertDraftAsync(Payment payment, ChequeDetailsRequest? details, string direction, BankAccount bank, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(bank);
        var existing = await db.Cheques.SingleOrDefaultAsync(c => c.PaymentId == payment.Id, cancellationToken);
        if (!string.Equals(payment.Method, Method, StringComparison.Ordinal))
        {
            if (existing is not null)
            {
                db.Cheques.Remove(existing);
            }

            return Result.Success();
        }

        if (!string.Equals(bank.Currency, payment.Currency, StringComparison.Ordinal))
        {
            return Error.Validation("cheque.bank_currency_mismatch", "A cheque is deposited to or drawn on an account in its own currency.").WithWhy(("chequeCurrency", payment.Currency), ("bankCurrency", bank.Currency));
        }

        if (payment.ChargesBank != 0m)
        {
            return Error.Validation("cheque.no_charges", "Bank charges on a cheque are taken when it clears or bounces, not on the document.").WithWhy(("charges", payment.ChargesBank));
        }

        details ??= new ChequeDetailsRequest();
        var cheque = existing ?? new Cheque { Id = Guid.CreateVersion7(), TenantId = payment.TenantId, PaymentId = payment.Id, CreatedAt = clock.UtcNow };
        var isIssued = string.Equals(direction, ChequeDirections.Issued, StringComparison.Ordinal);
        string number;
        if (isIssued && details.ChequeBookId is { } bookId)
        {
            var book = await db.ChequeBooks.SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken);
            if (book is null || book.CompanyId != payment.CompanyId || book.BankAccountId != bank.Id || !book.IsActive)
            {
                return Error.Validation("cheque.book_invalid", "The cheque book is not an active book of this bank account.").WithWhy(("chequeBookId", bookId));
            }

            if (existing?.ChequeBookId == bookId)
            {
                number = existing.ChequeNumber;
            }
            else
            {
                if (book.NextNumber > book.LastNumber)
                {
                    return Error.Conflict("cheque.book_exhausted", "The cheque book has no leaves left.").WithWhy(("lastNumber", book.LastNumber));
                }

                number = book.Prefix + book.NextNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                book.NextNumber++;
                book.UpdatedAt = clock.UtcNow;
            }

            cheque.ChequeBookId = bookId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(details.ChequeNumber))
            {
                return Error.Validation("cheque.number_required", isIssued ? "An issued cheque takes its number from a cheque book or is typed." : "The cheque's number is required.");
            }

            number = details.ChequeNumber.Trim();
            cheque.ChequeBookId = null;
        }

        if (details.ReplacesChequeId is { } replacesId)
        {
            var replaced = await db.Cheques.AsNoTracking().SingleOrDefaultAsync(c => c.Id == replacesId, cancellationToken);
            if (replaced is null || replaced.CompanyId != payment.CompanyId || replaced.PartnerId != payment.PartnerId || replaced.Direction != direction || replaced.Status is not (ChequeStatuses.Bounced or ChequeStatuses.Returned) || (replaced.ReplacedById is not null && replaced.ReplacedById != cheque.Id))
            {
                return Error.Validation("cheque.replaces_invalid", "A cheque replaces a bounced or returned cheque of the same partner that was not replaced already.").WithWhy(("replacesChequeId", replacesId));
            }
        }

        var dueDate = details.DueDate ?? payment.PaymentDate;
        if (dueDate < payment.PaymentDate.AddYears(-1))
        {
            return Error.Validation("cheque.due_date_invalid", "A cheque's date is not more than a year before the document.").WithWhy(("dueDate", dueDate));
        }

        cheque.CompanyId = payment.CompanyId;
        cheque.Direction = direction;
        cheque.ChequeNumber = number;
        cheque.DrawerBank = string.IsNullOrWhiteSpace(details.DrawerBank) ? null : details.DrawerBank.Trim();
        cheque.DrawerName = string.IsNullOrWhiteSpace(details.DrawerName) ? null : details.DrawerName.Trim();
        cheque.PartnerId = payment.PartnerId;
        cheque.Currency = payment.Currency;
        cheque.Amount = payment.AmountTc;
        cheque.IssueDate = payment.PaymentDate;
        cheque.DueDate = dueDate;
        cheque.IsPostDated = dueDate > payment.PaymentDate;
        cheque.Status = ChequeStatuses.Draft;
        cheque.BankAccountId = bank.Id;
        cheque.ReplacesId = details.ReplacesChequeId;
        cheque.Notes = string.IsNullOrWhiteSpace(details.Notes) ? null : details.Notes.Trim();
        cheque.UpdatedAt = clock.UtcNow;
        if (existing is null)
        {
            db.Cheques.Add(cheque);
        }

        return Result.Success();
    }

    /// <summary>The document posted: the cheque is in hand (received) or issued, carrying its functional value through the PDC accounts.</summary>
    public async Task OnPostedAsync(Payment payment, decimal amountFc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var cheque = await db.Cheques.SingleAsync(c => c.PaymentId == payment.Id, cancellationToken);
        var received = cheque.Direction == ChequeDirections.Received;
        cheque.Status = received ? ChequeStatuses.InHand : ChequeStatuses.Issued;
        cheque.AmountFc = amountFc;
        cheque.UpdatedAt = clock.UtcNow;
        cheque.Events.Add(Event(cheque, received ? "received" : "issued", payment.PaymentDate, 0m, 0m, null, payment.JournalEntryId));
        if (cheque.ReplacesId is { } replacesId)
        {
            var replaced = await db.Cheques.Include(static c => c.Events).SingleOrDefaultAsync(c => c.Id == replacesId, cancellationToken);
            if (replaced is not null)
            {
                replaced.ReplacedById = cheque.Id;
                replaced.UpdatedAt = clock.UtcNow;
                replaced.Events.Add(Event(replaced, "replaced", payment.PaymentDate, 0m, 0m, $"Replaced by {cheque.ChequeNumber}", payment.JournalEntryId));
            }
        }
    }

    /// <summary>The document is being reversed: only a cheque that went nowhere yet can be, and it is then returned (received) or cancelled (issued).</summary>
    public async Task<Result> OnReversingAsync(Payment payment, DateOnly reversalDate, string reason, Guid? reversalEntryId, bool commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var cheque = await db.Cheques.Include(static c => c.Events).SingleOrDefaultAsync(c => c.PaymentId == payment.Id, cancellationToken);
        if (cheque is null)
        {
            return Result.Success();
        }

        var received = cheque.Direction == ChequeDirections.Received;
        var allowed = received ? cheque.Status == ChequeStatuses.InHand : cheque.Status is ChequeStatuses.Issued or ChequeStatuses.Bounced;
        if (!allowed)
        {
            return Error.Conflict("cheque.in_progress", received ? "The cheque was deposited; bounce it or wait for it to clear." : "The cheque was paid by the bank; it cannot be cancelled.").WithWhy(("cheque", cheque.ChequeNumber), ("status", cheque.Status));
        }

        if (commit)
        {
            cheque.Status = received ? ChequeStatuses.Returned : ChequeStatuses.Cancelled;
            cheque.UpdatedAt = clock.UtcNow;
            cheque.Events.Add(Event(cheque, received ? "returned" : "cancelled", reversalDate, 0m, 0m, reason, reversalEntryId));
        }

        return Result.Success();
    }

    public ChequeEvent Event(Cheque cheque, string kind, DateOnly effectiveDate, decimal bankCharge, decimal customerFee, string? reason, Guid? journalEntryId)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        return new ChequeEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = cheque.TenantId,
            ChequeId = cheque.Id,
            OccurredAt = clock.UtcNow,
            Event = kind,
            EffectiveDate = effectiveDate,
            BankCharge = bankCharge,
            CustomerFee = customerFee,
            Reason = reason,
            JournalEntryId = journalEntryId,
            Actor = principal.Principal?.MembershipId.Value,
        };
    }

    public async Task<ChequeSummary> MapAsync(Cheque c, bool withEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        var partner = await partners.FindAsync(c.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(c.CompanyId), cancellationToken);
        var payment = await db.Payments.AsNoTracking().Where(p => p.Id == c.PaymentId).Select(static p => p.Number).SingleOrDefaultAsync(cancellationToken);
        var deposit = c.DepositId is { } d ? await db.ChequeDeposits.AsNoTracking().Where(x => x.Id == d).Select(static x => x.Number).SingleOrDefaultAsync(cancellationToken) : null;
        var bank = await db.BankAccounts.AsNoTracking().Where(b => b.Id == c.BankAccountId).Select(static b => b.Code).SingleOrDefaultAsync(cancellationToken);
        var events = withEvents
            ? c.Events.OrderBy(static e => e.OccurredAt).Select(static e => new ChequeEventSummary(e.Id, e.Event, e.EffectiveDate, e.BankCharge, e.CustomerFee, e.Reason, e.JournalEntryId, e.OccurredAt)).ToList()
            : [];
        return new ChequeSummary(c.Id, c.CompanyId, c.Direction, c.ChequeNumber, c.ChequeBookId, c.DrawerBank, c.DrawerName, c.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            c.Currency, c.Amount, c.AmountFc, company?.FunctionalCurrency.Code ?? string.Empty, c.IssueDate, c.DueDate, c.IsPostDated, c.Status, c.PaymentId, payment ?? string.Empty, c.DepositId, deposit, c.BankAccountId, bank ?? string.Empty,
            c.ReplacedById, c.ReplacesId, c.FeeOpenItemId, c.BankTransactionId, c.Notes, c.UpdatedAt, events);
    }
}

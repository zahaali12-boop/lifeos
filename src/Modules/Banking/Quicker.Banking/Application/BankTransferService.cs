using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;

namespace Quicker.Banking.Application;

/// <summary>Money moved between two of the company's accounts: from where, to where, what left, what arrived (asked for only when the currencies differ), on which day, the bank's reference and a note.</summary>
public sealed record RecordBankTransferRequest(Guid CompanyId, Guid FromBankAccountId, Guid ToBankAccountId, decimal Amount, decimal? AmountReceived = null, DateOnly? TransferDate = null, string? Reference = null, string? Notes = null);

/// <summary>A transfer as read back: both accounts with their codes and currencies, both amounts and their values in the company's currency, the exchange difference, the journal, and whether it still stands.</summary>
public sealed record BankTransferSummary(Guid Id, Guid CompanyId, string Number, string Status, DateOnly TransferDate,
    Guid FromBankAccountId, string FromBankAccountCode, IReadOnlyDictionary<string, string> FromBankAccountName,
    Guid ToBankAccountId, string ToBankAccountCode, IReadOnlyDictionary<string, string> ToBankAccountName,
    decimal AmountSent, string CurrencySent, decimal AmountReceived, string CurrencyReceived, decimal ExchangeRate, string FunctionalCurrency,
    decimal AmountSentFc, decimal AmountReceivedFc, decimal ExchangeDifferenceFc, string? Reference, string? Notes,
    Guid? JournalEntryId, Guid? ReversalEntryId, string? ReversalReason, DateTimeOffset? PostedAt, DateTimeOffset? ReversedAt);

/// <summary>
/// Transfers between the company's own bank accounts and cash boxes (ADR-0035 §24, A-210): cash paid into the bank,
/// cash drawn from it, money moved between boxes, or dinars changed for dollars. A transfer posts at once through the
/// posting engine: the account it arrives in is debited and the one it left credited, each line naming its own bank
/// movement, so both boxes and their ledger accounts move together. When the currencies differ the foreign side is
/// valued at the company's spot rate for the day (the exchange's own rate when the company has none), the side in the
/// company's currency at what actually moved, and the gap is a realised exchange gain or loss, as payments and receipts
/// book theirs. A transfer is undone by its reversal, never edited.
/// </summary>
public sealed class BankTransferService(
    BankingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    INumberAllocator numbering,
    IPostingService posting,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = BankDocumentTypes.Transfer;

    public async Task<IReadOnlyList<BankTransferSummary>> ListAsync(Guid? companyId, Guid? bankAccountId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Transfers.AsNoTracking().AsQueryable();
        if (companyId is { } company)
        {
            query = query.Where(t => t.CompanyId == company);
        }

        if (bankAccountId is { } account)
        {
            query = query.Where(t => t.FromBankAccountId == account || t.ToBankAccountId == account);
        }

        if (from is { } start)
        {
            query = query.Where(t => t.TransferDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(t => t.TransferDate <= end);
        }

        var transfers = await query.OrderByDescending(static t => t.TransferDate).ThenByDescending(static t => t.CreatedAt).Take(1000).ToListAsync(cancellationToken);
        return await MapAsync(transfers, cancellationToken);
    }

    public async Task<Result<BankTransferSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var transfer = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        return transfer is null ? Error.NotFound(DocumentType, id) : (await MapAsync([transfer], cancellationToken))[0];
    }

    public async Task<Result<BankTransferSummary>> RecordAsync(RecordBankTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.FromBankAccountId == request.ToBankAccountId)
        {
            return Error.Validation("transfer.same_account", "Money moves from one account to another; choose two different ones.").WithWhy(("bankAccountId", request.FromBankAccountId));
        }

        var boxes = await db.BankAccounts.AsNoTracking().Where(b => b.Id == request.FromBankAccountId || b.Id == request.ToBankAccountId).ToListAsync(cancellationToken);
        var source = boxes.SingleOrDefault(b => b.Id == request.FromBankAccountId);
        var target = boxes.SingleOrDefault(b => b.Id == request.ToBankAccountId);
        if (source is null || source.CompanyId != request.CompanyId)
        {
            return Error.NotFound("bank_account", request.FromBankAccountId);
        }

        if (target is null || target.CompanyId != request.CompanyId)
        {
            return Error.NotFound("bank_account", request.ToBankAccountId);
        }

        foreach (var box in new[] { source, target })
        {
            if (!box.IsActive)
            {
                return Error.Validation("transfer.bank_account_inactive", $"'{box.Code}' is closed; reopen it before moving money through it.").WithWhy(("bankAccount", box.Code));
            }
        }

        if (request.Amount <= 0m)
        {
            return Error.Validation("transfer.amount_invalid", "The amount sent is above zero.").WithWhy(("amount", request.Amount));
        }

        var fc = company.FunctionalCurrency;
        var policy = new RoundingPolicy(company.RoundingMode);
        var sentCurrency = await companies.FindCurrencyAsync(source.Currency, cancellationToken);
        var receivedCurrency = await companies.FindCurrencyAsync(target.Currency, cancellationToken);
        if (sentCurrency is null || receivedCurrency is null)
        {
            return Error.Validation("transfer.currency_unknown", "An account is kept in a currency the system does not know.").WithWhy(("from", source.Currency), ("to", target.Currency));
        }

        var sent = policy.Round(request.Amount, sentCurrency.Value.MinorUnits);
        var sameCurrency = string.Equals(source.Currency, target.Currency, StringComparison.Ordinal);
        decimal received;
        if (sameCurrency)
        {
            if (request.AmountReceived is { } given && policy.Round(given, receivedCurrency.Value.MinorUnits) != sent)
            {
                return Error.Validation("transfer.amounts_differ", $"Between two accounts in {source.Currency} what arrives is what left; a bank's fee on the transfer is an expense of its own.").WithWhy(("amount", sent), ("amountReceived", given), ("currency", source.Currency));
            }

            received = sent;
        }
        else
        {
            if (request.AmountReceived is not { } given || given <= 0m)
            {
                return Error.Validation("transfer.amount_received_required", $"The money changes from {source.Currency} to {target.Currency}; say how much arrived in {target.Code}.").WithWhy(("from", source.Currency), ("to", target.Currency));
            }

            received = policy.Round(given, receivedCurrency.Value.MinorUnits);
        }

        var sourceIsFc = string.Equals(source.Currency, fc.Code, StringComparison.Ordinal);
        var targetIsFc = string.Equals(target.Currency, fc.Code, StringComparison.Ordinal);
        if (!sameCurrency && !sourceIsFc && !targetIsFc)
        {
            return Error.Validation("transfer.currencies_unsupported", $"Money changes between {source.Currency} and {target.Currency} through the company's currency ({fc.Code}): move it to an account in {fc.Code} first.").WithWhy(("from", source.Currency), ("to", target.Currency), ("functionalCurrency", fc.Code));
        }

        var date = request.TransferDate ?? clock.TodayIn(company.TimeZone);

        // The currency the entry is written in: the foreign one when there is one, both lines carrying its amount.
        var foreign = sourceIsFc && targetIsFc ? null : sourceIsFc ? target.Currency : source.Currency;
        var foreignAmount = foreign is null ? 0m : sourceIsFc ? received : sent;
        var rate = 1m;
        if (foreign is not null)
        {
            var resolved = await rates.ResolveAsync(company.Id, foreign, fc.Code, date, RateTypes.Spot, cancellationToken);
            if (resolved.IsSuccess)
            {
                rate = resolved.Value.Rate.Rate;
            }
            else if (!sameCurrency)
            {
                // No rate of the day: the exchange itself says what the foreign money was worth.
                rate = (sourceIsFc ? sent : received) / foreignAmount;
            }
            else
            {
                return Error.Validation("transfer.rate_missing", $"No rate from {foreign} to {fc.Code} on {date:yyyy-MM-dd}; enter the day's rate first.").WithWhy(("from", foreign), ("to", fc.Code), ("date", date));
            }
        }

        var sentFc = sourceIsFc ? sent : policy.Round(sent * rate, fc.MinorUnits);
        var receivedFc = targetIsFc ? received : policy.Round(received * rate, fc.MinorUnits);
        var difference = receivedFc - sentFc;
        var entryAmount = foreign is null ? sent : foreignAmount;

        var transfer = new BankTransfer
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            TransferDate = date,
            FromBankAccountId = source.Id,
            ToBankAccountId = target.Id,
            AmountSent = sent,
            CurrencySent = source.Currency,
            AmountReceived = received,
            CurrencyReceived = target.Currency,
            ExchangeRate = rate,
            AmountSentFc = sentFc,
            AmountReceivedFc = receivedFc,
            ExchangeDifferenceFc = difference,
            Reference = Clean(request.Reference),
            Notes = Clean(request.Notes),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(transfer.CompanyId), "TRF", "TRF-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(transfer.CompanyId), null, transfer.TransferDate, transfer.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        transfer.Number = number.Value.Text;
        var outId = Guid.CreateVersion7();
        var inId = Guid.CreateVersion7();
        var text = transfer.Notes ?? $"{source.Code} → {target.Code}";
        var lines = new List<PostingLine>
        {
            new(BankAccountService.RoleOf(target.Kind), entryAmount, new PostingKeys(DocumentType, BankAccountId: target.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: inId, AmountFc: receivedFc, Description: LocalizedText.Bilingual(text, text)),
            new(BankAccountService.RoleOf(source.Kind), -entryAmount, new PostingKeys(DocumentType, BankAccountId: source.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: outId, AmountFc: -sentFc, Description: LocalizedText.Bilingual(text, text)),
        };
        if (difference != 0m)
        {
            // More value arrived than left: a gain (credit); less: a loss (debit).
            lines.Add(new PostingLine(difference > 0m ? AccountRoles.FxGainRealized : AccountRoles.FxLossRealized, 0m, new PostingKeys(DocumentType), AmountFc: -difference, Description: LocalizedText.Bilingual("Realised exchange difference", "فرق صرف محقق")));
        }

        var posted = await posting.PostAsync(new PostingRequest(company.Id, "banking", DocumentType, transfer.Id, date, foreign ?? source.Currency, lines, transfer.Number, date,
            LocalizedText.Bilingual($"Transfer {transfer.Number}: {source.Code} to {target.Code}", $"تحويل {transfer.Number}: من {source.Code} إلى {target.Code}"),
            null, RateTypes.Spot, foreign is null ? null : rate, foreign is null ? null : "Transfer rate", $"bank_transfer:{transfer.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var reference = transfer.Reference ?? transfer.Number;
        db.Transactions.Add(new BankTransaction
        {
            Id = outId,
            BankAccountId = source.Id,
            CompanyId = transfer.CompanyId,
            PostingDate = date,
            ValueDate = date,
            Kind = "transfer_out",
            AmountTc = -sent,
            AmountFc = -sentFc,
            Reference = reference,
            SourceDocumentType = DocumentType,
            SourceDocumentId = transfer.Id,
            JournalEntryId = posted.Value.EntryId,
            CreatedAt = clock.UtcNow,
        });
        db.Transactions.Add(new BankTransaction
        {
            Id = inId,
            BankAccountId = target.Id,
            CompanyId = transfer.CompanyId,
            PostingDate = date,
            ValueDate = date,
            Kind = "transfer_in",
            AmountTc = received,
            AmountFc = receivedFc,
            Reference = reference,
            SourceDocumentType = DocumentType,
            SourceDocumentId = transfer.Id,
            JournalEntryId = posted.Value.EntryId,
            CreatedAt = clock.UtcNow,
        });
        transfer.JournalEntryId = posted.Value.EntryId;
        transfer.FromTransactionId = outId;
        transfer.ToTransactionId = inId;
        transfer.PostedAt = clock.UtcNow;
        transfer.PostedBy = principal.Principal?.MembershipId.Value;
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, transfer.Id, transfer.Number, AuditActions.Posted, After: new { from = source.Code, to = target.Code, sent, transfer.CurrencySent, received, transfer.CurrencyReceived, rate, fx = difference, journal = posted.Value.Number }, CompanyId: transfer.CompanyId), cancellationToken);
        return (await MapAsync([transfer], cancellationToken))[0];
    }

    public async Task<Result<BankTransferSummary>> ReverseAsync(Guid id, ReversePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Clean(request.Reason);
        if (reason is null)
        {
            return Error.Validation("transfer.reason_required", "A reversal names its reason.");
        }

        var transfer = await db.Transfers.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (transfer is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (transfer.Status != "posted")
        {
            return Error.Conflict("transfer.not_posted", "The transfer is reversed already.").WithWhy(("status", transfer.Status));
        }

        var company = await companies.FindAsync(new CompanyId(transfer.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < transfer.TransferDate)
        {
            return Error.Validation("transfer.reversal_date_invalid", "A transfer is reversed on or after its date.").WithWhy(("transferDate", transfer.TransferDate), ("reversalDate", reversalDate));
        }

        var reversed = await posting.ReverseAsync(transfer.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var moved = await db.Transactions.Where(t => t.Id == transfer.FromTransactionId || t.Id == transfer.ToTransactionId).ToListAsync(cancellationToken);
        foreach (var original in moved)
        {
            db.Transactions.Add(new BankTransaction
            {
                Id = Guid.CreateVersion7(),
                BankAccountId = original.BankAccountId,
                CompanyId = original.CompanyId,
                PostingDate = reversed.Value.PostingDate,
                ValueDate = reversed.Value.PostingDate,
                Kind = "reversal",
                AmountTc = -original.AmountTc,
                AmountFc = -original.AmountFc,
                Reference = original.Reference,
                SourceDocumentType = DocumentType,
                SourceDocumentId = transfer.Id,
                JournalEntryId = reversed.Value.EntryId,
                ReversesTransactionId = original.Id,
                CreatedAt = clock.UtcNow,
            });
        }

        transfer.Status = "reversed";
        transfer.ReversalEntryId = reversed.Value.EntryId;
        transfer.ReversalReason = reason;
        transfer.ReversedAt = clock.UtcNow;
        transfer.ReversedBy = principal.Principal?.MembershipId.Value;
        transfer.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, transfer.Id, transfer.Number, AuditActions.Reversed, After: new { status = transfer.Status, reversalDate = reversed.Value.PostingDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: transfer.CompanyId), cancellationToken);
        return (await MapAsync([transfer], cancellationToken))[0];
    }

    private async Task<List<BankTransferSummary>> MapAsync(IReadOnlyList<BankTransfer> transfers, CancellationToken cancellationToken)
    {
        var ids = transfers.SelectMany(static t => new[] { t.FromBankAccountId, t.ToBankAccountId }).Distinct().ToList();
        var boxes = await db.BankAccounts.AsNoTracking().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(static b => b.Id, cancellationToken);
        var result = new List<BankTransferSummary>(transfers.Count);
        foreach (var t in transfers)
        {
            var company = await companies.FindAsync(new CompanyId(t.CompanyId), cancellationToken);
            var source = boxes.GetValueOrDefault(t.FromBankAccountId);
            var target = boxes.GetValueOrDefault(t.ToBankAccountId);
            result.Add(new BankTransferSummary(t.Id, t.CompanyId, t.Number, t.Status, t.TransferDate,
                t.FromBankAccountId, source?.Code ?? string.Empty, source?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                t.ToBankAccountId, target?.Code ?? string.Empty, target?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                t.AmountSent, t.CurrencySent, t.AmountReceived, t.CurrencyReceived, t.ExchangeRate, company?.FunctionalCurrency.Code ?? string.Empty,
                t.AmountSentFc, t.AmountReceivedFc, t.ExchangeDifferenceFc, t.Reference, t.Notes,
                t.JournalEntryId, t.ReversalEntryId, t.ReversalReason, t.PostedAt, t.ReversedAt));
        }

        return result;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Receivables.Domain;
using Quicker.Receivables.Persistence;

namespace Quicker.Receivables.Application;

/// <summary>
/// Netting (roadmap 5.7b, A-113, A-162): a partner who is both a customer and a supplier of the company has what they
/// owe set against what they are owed, in one currency. One journal Dr <c>AP</c> / Cr <c>AR</c> relieves each item at
/// its booked functional value, the difference being realised FX (ADR-0031 on both sides); each subledger gets a
/// counter item of the netting's own, used up at once by a settlement of kind <c>netting</c>, so both control accounts
/// keep equalling their items. Reversal mirrors the journal and reopens both items.
/// </summary>
public sealed class NettingService(ReceivablesDbContext db, ReceivablesService receivables, IPayables payables, ICompanyDirectory companies, IPartnerDirectory partners, ICustomerDirectory customers, INumberAllocator numbering, IPostingService posting, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public async Task<IReadOnlyList<NettingSummary>> ListAsync(Guid companyId, Guid? partnerId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Nettings.AsNoTracking().Where(n => n.CompanyId == companyId);
        if (partnerId is { } p)
        {
            query = query.Where(n => n.PartnerId == p);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(n => n.Status == status);
        }

        var rows = await query.OrderByDescending(static n => n.NettingDate).ThenByDescending(static n => n.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<NettingSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<Result<NettingSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Nettings.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        return row is null ? Error.NotFound(ReceivablesDocumentTypes.Netting, id) : await MapAsync(row, cancellationToken);
    }

    public async Task<Result<NettingSummary>> NetAsync(NettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receivable = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == request.ReceivableItemId, cancellationToken);
        if (receivable is null)
        {
            return Error.NotFound("open_item", request.ReceivableItemId);
        }

        var payable = await payables.FindAsync(request.PayableItemId, cancellationToken);
        if (payable is null)
        {
            return Error.NotFound("open_item", request.PayableItemId);
        }

        if (receivable.CompanyId != payable.CompanyId || receivable.PartnerId != payable.PartnerId)
        {
            return Error.Validation("netting.partner_mismatch", "Both items belong to the same partner and company.").WithWhy(("receivablePartner", receivable.PartnerId), ("payablePartner", payable.PartnerId));
        }

        if (!string.Equals(receivable.Currency, payable.Currency, StringComparison.Ordinal))
        {
            return Error.Validation("netting.currency_mismatch", "Items are netted in one currency.").WithWhy(("receivableCurrency", receivable.Currency), ("payableCurrency", payable.Currency));
        }

        if (receivable.OriginalTc <= 0m || receivable.Status is not ("open" or "partially_settled") || payable.OriginalTc <= 0m || payable.Status is not ("open" or "partially_settled"))
        {
            return Error.Conflict("netting.items_not_open", "A netting sets an open receivable invoice against an open payable invoice.").WithWhy(("receivableStatus", receivable.Status), ("receivableKind", receivable.Kind), ("payableStatus", payable.Status), ("payableKind", payable.Kind));
        }

        if (receivable.OnHold || payable.PaymentBlocked)
        {
            return Error.Conflict("netting.item_held", "A held item is not netted.").WithWhy(("receivableHeld", receivable.OnHold), ("payableHeld", payable.PaymentBlocked));
        }

        var amount = request.Amount ?? Math.Min(receivable.RemainingTc, payable.RemainingTc);
        if (amount <= 0m || amount > receivable.RemainingTc || amount > payable.RemainingTc)
        {
            return Error.Validation("netting.amount_invalid", "The amount is positive and within what is open on both items.").WithWhy(("amount", amount), ("receivableRemaining", receivable.RemainingTc), ("payableRemaining", payable.RemainingTc));
        }

        var company = await companies.FindAsync(new CompanyId(receivable.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", receivable.CompanyId);
        }

        var date = request.NettingDate ?? clock.TodayIn(company.TimeZone);
        if (date < receivable.PostingDate || date < payable.PostingDate)
        {
            return Error.Validation("netting.date_before_items", "A netting is not dated before the items it settles.").WithWhy(("nettingDate", date), ("receivableDate", receivable.PostingDate), ("payableDate", payable.PostingDate));
        }

        var minor = company.FunctionalCurrency.MinorUnits;
        var arFc = amount == receivable.RemainingTc ? receivable.RemainingFc : RoundingPolicy.Default.Round(amount * receivable.BookedRate, minor);
        var apFc = amount == payable.RemainingTc ? payable.RemainingFc : RoundingPolicy.Default.Round(amount * payable.BookedRate, minor);
        var sameCurrency = string.Equals(receivable.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        var row = new ArNetting
        {
            Id = Guid.CreateVersion7(),
            CompanyId = receivable.CompanyId,
            PartnerId = receivable.PartnerId,
            ReceivableItemId = receivable.Id,
            PayableItemId = payable.Id,
            NettingDate = date,
            Currency = receivable.Currency,
            AmountTc = amount,
            AmountFcReceivable = arFc,
            AmountFcPayable = apFc,
            FxGainLossFc = apFc - arFc,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(ReceivablesDocumentTypes.Netting, new CompanyId(row.CompanyId), "NET", "NET-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(ReceivablesDocumentTypes.Netting, new CompanyId(row.CompanyId), null, date, row.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        row.Number = number.Value.Text;
        var customer = await customers.FindCustomerAsync(row.CompanyId, row.PartnerId, cancellationToken);
        var supplier = await partners.FindSupplierAsync(row.CompanyId, row.PartnerId, cancellationToken);
        var lines = new List<PostingLine>
        {
            new(AccountRoles.AP, amount, new PostingKeys(ReceivablesDocumentTypes.Netting, PartnerPostingGroupId: supplier?.PostingGroupId), SubledgerType: SubledgerTypes.Payables, SubledgerRef: payable.DocumentId, PartnerId: row.PartnerId, AmountFc: apFc),
            new(AccountRoles.AR, -amount, new PostingKeys(ReceivablesDocumentTypes.Netting, PartnerPostingGroupId: customer?.PostingGroupId), SubledgerType: SubledgerTypes.Receivables, SubledgerRef: receivable.DocumentId, PartnerId: row.PartnerId, AmountFc: -arFc),
        };
        if (row.FxGainLossFc != 0m)
        {
            // The payable relieved at more than the receivable is a gain (credit); at less, a loss (debit).
            lines.Add(new PostingLine(row.FxGainLossFc > 0m ? AccountRoles.FxGainRealized : AccountRoles.FxLossRealized, 0m, new PostingKeys(ReceivablesDocumentTypes.Netting), PartnerId: row.PartnerId, AmountFc: -row.FxGainLossFc, Description: LocalizedText.Bilingual("Realised exchange difference", "فرق صرف محقق")));
        }

        var partner = await partners.FindAsync(row.PartnerId, cancellationToken);
        var description = LocalizedText.Bilingual($"Netting {partner?.Code}: {receivable.DocumentNumber} against {payable.DocumentNumber} ({row.Number})", $"مقاصة {partner?.Code}: {receivable.DocumentNumber} مقابل {payable.DocumentNumber} ({row.Number})");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "receivables", ReceivablesDocumentTypes.Netting, row.Id, date, row.Currency, lines, row.Number, date, description,
            receivable.BranchId is { } b ? new BranchId(b) : null, RateTypes.Spot, sameCurrency ? null : receivable.BookedRate, sameCurrency ? null : "Booked rate of the receivable netted", $"ar_netting:{row.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // The receivable side: a counter credit item of the netting's own, used up at once by the netting settlement.
        var arCounter = new ArOpenItem
        {
            Id = Guid.CreateVersion7(),
            CompanyId = row.CompanyId,
            PartnerId = row.PartnerId,
            Kind = ReceivableKinds.Adjustment,
            DocumentType = ReceivablesDocumentTypes.Netting,
            DocumentId = row.Id,
            DocumentNumber = row.Number,
            Instalment = 1,
            CustomerReference = receivable.CustomerReference,
            PostingDate = date,
            DocumentDate = date,
            DueDate = date,
            Currency = row.Currency,
            OriginalTc = -amount,
            OriginalFc = -arFc,
            BookedRate = receivable.BookedRate,
            RemainingTc = -amount,
            RemainingFc = -arFc,
            JournalEntryId = posted.Value.EntryId,
            SalesRepId = receivable.SalesRepId,
            BranchId = receivable.BranchId,
            Status = "open",
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        var arSettlement = new ArSettlement
        {
            Id = Guid.CreateVersion7(),
            CompanyId = row.CompanyId,
            SettlingItemId = arCounter.Id,
            SettledItemId = receivable.Id,
            SettlementDate = date,
            Kind = ReceivableSettlementKinds.Netting,
            Currency = row.Currency,
            AmountTc = amount,
            AmountFcSettledItem = arFc,
            AmountFcSettlingItem = arFc,
            SettlementRate = receivable.BookedRate,
            FxGainLossFc = 0m,
            JournalEntryId = posted.Value.EntryId,
            Reason = row.Reason,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
        };
        receivables.Move(receivable, -amount, -arFc);
        receivables.Move(arCounter, amount, arFc);
        db.OpenItems.Add(arCounter);
        db.Settlements.Add(arSettlement);

        // The payable side through its own subledger: a counter item (owed by the supplier) and the netting settlement.
        var apCounter = await payables.OpenAsync(new NewOpenItem(row.CompanyId, row.PartnerId, PayableKinds.Adjustment, ReceivablesDocumentTypes.Netting, row.Id, row.Number, 1, payable.SupplierReference, date, date, date, null, 0m, row.Currency, -amount, -apFc, payable.BookedRate, posted.Value.EntryId, payable.BranchId), cancellationToken);
        var apSettlement = await payables.RecordAsync(new RecordSettlementRequest(apCounter.Id, payable.Id, date, SettlementKinds.Netting, amount, apFc, apFc, payable.BookedRate, 0m, 0m, posted.Value.EntryId, row.Reason), cancellationToken);
        if (apSettlement.IsFailure)
        {
            return apSettlement.Error!;
        }

        row.ArCounterItemId = arCounter.Id;
        row.ApCounterItemId = apCounter.Id;
        row.ArSettlementId = arSettlement.Id;
        row.JournalEntryId = posted.Value.EntryId;
        db.Nettings.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReceivablesDocumentTypes.Netting, row.Id, row.Number, AuditActions.Posted, After: new { receivable = receivable.DocumentNumber, payable = payable.DocumentNumber, row.AmountTc, row.Currency, row.FxGainLossFc, journal = posted.Value.Number }, Reason: row.Reason, CompanyId: row.CompanyId), cancellationToken);
        return await MapAsync(row, cancellationToken);
    }

    public async Task<Result<NettingSummary>> ReverseAsync(Guid id, ReverseNettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("netting.reason_required", "A reversal names its reason.");
        }

        var row = await db.Nettings.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ReceivablesDocumentTypes.Netting, id);
        }

        if (row.Status != "posted")
        {
            return Error.Conflict("netting.not_posted", "Only a posted netting is reversed.").WithWhy(("status", row.Status));
        }

        var company = await companies.FindAsync(new CompanyId(row.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < row.NettingDate)
        {
            return Error.Validation("netting.reversal_date_invalid", "A netting is reversed on or after its date.").WithWhy(("nettingDate", row.NettingDate), ("reversalDate", reversalDate));
        }

        var reason = request.Reason.Trim();
        var reversed = await posting.ReverseAsync(row.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        // Payables first (its guard refuses when the counter item was used further), then the receivable side.
        var apSettlements = await payables.ReverseSettlementsOfAsync(row.JournalEntryId.Value, reversed.Value.PostingDate, reversed.Value.EntryId, reason, cancellationToken);
        if (apSettlements.IsFailure)
        {
            return apSettlements.Error!;
        }

        var apCounter = await payables.ReverseDocumentAsync(ReceivablesDocumentTypes.Netting, row.Id, reversed.Value.PostingDate, cancellationToken);
        if (apCounter.IsFailure)
        {
            return apCounter.Error!;
        }

        var settlement = await db.Settlements.SingleAsync(s => s.Id == row.ArSettlementId, cancellationToken);
        var receivable = await db.OpenItems.SingleAsync(i => i.Id == row.ReceivableItemId, cancellationToken);
        var arCounter = await db.OpenItems.SingleAsync(i => i.Id == row.ArCounterItemId, cancellationToken);
        receivables.Move(receivable, settlement.AmountTc, settlement.AmountFcSettledItem);
        receivables.Move(arCounter, -settlement.AmountTc, -settlement.AmountFcSettlingItem);
        settlement.Status = "reversed";
        db.Settlements.Add(receivables.Mirror(settlement, reversed.Value.PostingDate, reversed.Value.EntryId, reason));
        arCounter.Status = "reversed";
        arCounter.ReversedOn = reversed.Value.PostingDate;
        arCounter.UpdatedAt = clock.UtcNow;
        row.Status = "reversed";
        row.ReversalEntryId = reversed.Value.EntryId;
        row.ReversalReason = reason;
        row.ReversedAt = clock.UtcNow;
        row.ReversedBy = principal.Principal?.MembershipId.Value;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReceivablesDocumentTypes.Netting, row.Id, row.Number, AuditActions.Reversed, After: new { reversalDate = reversed.Value.PostingDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: row.CompanyId), cancellationToken);
        return await MapAsync(row, cancellationToken);
    }

    private async Task<NettingSummary> MapAsync(ArNetting n, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(n.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(n.CompanyId), cancellationToken);
        var receivable = await db.OpenItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == n.ReceivableItemId, cancellationToken);
        var payable = await payables.FindAsync(n.PayableItemId, cancellationToken);
        return new NettingSummary(n.Id, n.CompanyId, n.Number, n.Status, n.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? ReceivablesService.Empty(), n.ReceivableItemId, receivable?.DocumentNumber ?? string.Empty, n.PayableItemId, payable?.DocumentNumber ?? string.Empty,
            n.NettingDate, n.Currency, n.AmountTc, n.AmountFcReceivable, n.AmountFcPayable, n.FxGainLossFc, company?.FunctionalCurrency.Code ?? n.Currency, n.Reason, n.JournalEntryId, n.ReversalEntryId, n.ReversalReason, n.CreatedAt);
    }
}

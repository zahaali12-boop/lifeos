using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Payables.Domain;
using Quicker.Payables.Persistence;

namespace Quicker.Payables.Application;

/// <summary>
/// Applications between open items (POSTING_RULES §4 "Credit application", "Apply advance to invoice"): a debit note, a
/// payment on account or an advance settles an invoice item of the same supplier and currency. Both items move in the
/// transaction currency; where they were booked at different rates the difference in the company's currency is realised
/// FX, booked in the company's currency against each item's control account (ADR-0031), so every control account keeps
/// equalling its open items. A reversal mirrors the settlement and its journal.
/// </summary>
public sealed class SettlementService(PayablesDbContext db, PayablesService payables, ICompanyDirectory companies, IPartnerDirectory partners, IPostingService posting, IAuditSink audit, IClock clock)
{
    public async Task<IReadOnlyList<SettlementInfo>> ListAsync(Guid companyId, Guid? openItemId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Settlements.AsNoTracking().Where(s => s.CompanyId == companyId);
        if (openItemId is { } o)
        {
            query = query.Where(s => s.SettlingItemId == o || s.SettledItemId == o);
        }

        if (partnerId is { } p)
        {
            var partnerItems = db.OpenItems.AsNoTracking().Where(i => i.PartnerId == p).Select(static i => i.Id);
            query = query.Where(s => partnerItems.Contains(s.SettledItemId));
        }

        var rows = await query.OrderByDescending(static s => s.SettlementDate).ThenByDescending(static s => s.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var itemIds = rows.Select(static s => s.SettlingItemId).Concat(rows.Select(static s => s.SettledItemId)).Distinct().ToList();
        var items = await db.OpenItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(static i => i.Id, cancellationToken);
        return rows.Select(s => PayablesService.Map(s, items[s.SettlingItemId], items[s.SettledItemId])).ToList();
    }

    public async Task<Result<SettlementInfo>> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settling = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == request.SettlingItemId, cancellationToken);
        var settled = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == request.SettledItemId, cancellationToken);
        if (settling is null || settled is null)
        {
            return Error.NotFound("open_item", settling is null ? request.SettlingItemId : request.SettledItemId);
        }

        var check = PayablesService.Check(settling, settled, request.Amount);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var company = await companies.FindAsync(new CompanyId(settled.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", settled.CompanyId);
        }

        var settlementDate = request.SettlementDate ?? clock.TodayIn(company.TimeZone);
        if (settlementDate < settled.PostingDate || settlementDate < settling.PostingDate)
        {
            return Error.Validation("settlement.date_before_items", "A settlement is not dated before the items it settles.").WithWhy(("settlementDate", settlementDate), ("settledDate", settled.PostingDate), ("settlingDate", settling.PostingDate));
        }

        var fullSettled = request.Amount == settled.RemainingTc;
        var fullSettling = request.Amount == -settling.RemainingTc;
        var settledFc = fullSettled ? settled.RemainingFc : RoundingPolicy.Default.Round(request.Amount * settled.BookedRate, company.FunctionalCurrency.MinorUnits);
        var settlingFc = fullSettling ? -settling.RemainingFc : RoundingPolicy.Default.Round(request.Amount * settling.BookedRate, company.FunctionalCurrency.MinorUnits);
        var kind = settling.Kind == PayableKinds.Advance ? SettlementKinds.AdvanceApplication : SettlementKinds.CreditApplication;
        var row = new ApSettlement
        {
            Id = Guid.CreateVersion7(),
            CompanyId = settled.CompanyId,
            SettlingItemId = settling.Id,
            SettledItemId = settled.Id,
            SettlementDate = settlementDate,
            Kind = kind,
            Currency = settled.Currency,
            AmountTc = request.Amount,
            AmountFcSettledItem = settledFc,
            AmountFcSettlingItem = settlingFc,
            SettlementRate = settling.BookedRate,
            FxGainLossFc = settlingFc - settledFc,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedAt = clock.UtcNow,
        };

        // The advance leaves its own control account for the payable's; a credit stays on payables, so only the difference is booked.
        var settlingRole = settling.Kind == PayableKinds.Advance ? AccountRoles.SupplierAdvances : AccountRoles.AP;
        var needsJournal = settlingRole != AccountRoles.AP || row.FxGainLossFc != 0m;
        if (needsJournal)
        {
            var supplier = await partners.FindSupplierAsync(settled.CompanyId, settled.PartnerId, cancellationToken);
            var lines = new List<PostingLine>
            {
                new(AccountRoles.AP, settledFc, new PostingKeys(PayablesDocumentTypes.Settlement, PartnerPostingGroupId: supplier?.PostingGroupId), SubledgerType: SubledgerTypes.Payables, SubledgerRef: settled.DocumentId, PartnerId: settled.PartnerId),
                new(settlingRole, -settlingFc, new PostingKeys(PayablesDocumentTypes.Settlement, PartnerPostingGroupId: supplier?.PostingGroupId), SubledgerType: SubledgerTypes.Payables, SubledgerRef: settling.DocumentId, PartnerId: settled.PartnerId),
            };
            if (row.FxGainLossFc != 0m)
            {
                // Relieving the payable at its booked value against a credit worth less (or more) today: the payable cost more (loss) or less (gain).
                lines.Add(new PostingLine(row.FxGainLossFc > 0m ? AccountRoles.FxLossRealized : AccountRoles.FxGainRealized, row.FxGainLossFc, new PostingKeys(PayablesDocumentTypes.Settlement), PartnerId: settled.PartnerId));
            }

            var description = LocalizedText.Bilingual($"{settling.DocumentNumber} applied to {settled.DocumentNumber}", $"تطبيق {settling.DocumentNumber} على {settled.DocumentNumber}");
            var posted = await posting.PostAsync(new PostingRequest(company.Id, "payables", PayablesDocumentTypes.Settlement, row.Id, settlementDate, company.FunctionalCurrency.Code, lines, $"{settling.DocumentNumber}→{settled.DocumentNumber}", settlementDate, description,
                settled.BranchId is { } b ? new BranchId(b) : null, RateTypes.Spot, null, null, $"ap_settlement:{row.Id}"), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            row.JournalEntryId = posted.Value.EntryId;
        }

        payables.Move(settled, -request.Amount, -settledFc);
        payables.Move(settling, request.Amount, settlingFc);
        db.Settlements.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(PayablesDocumentTypes.Settlement, row.Id, $"{settling.DocumentNumber}→{settled.DocumentNumber}", AuditActions.Posted, After: new { row.Kind, row.AmountTc, row.Currency, row.FxGainLossFc, settlingRemaining = settling.RemainingTc, settledRemaining = settled.RemainingTc }, CompanyId: settled.CompanyId), cancellationToken);
        return PayablesService.Map(row, settling, settled);
    }

    public async Task<Result<SettlementInfo>> ReverseAsync(Guid id, ReverseSettlementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("settlement.reason_required", "A reversal names its reason.");
        }

        var row = await db.Settlements.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(PayablesDocumentTypes.Settlement, id);
        }

        if (row.Status != "posted" || row.ReversesSettlementId is not null)
        {
            return Error.Conflict("settlement.not_reversible", "Only a standing application is reversed.").WithWhy(("status", row.Status));
        }

        if (row.Kind is not (SettlementKinds.CreditApplication or SettlementKinds.AdvanceApplication))
        {
            return Error.Conflict("settlement.reverse_document", "A payment's settlements are reversed by reversing the payment.").WithWhy(("kind", row.Kind));
        }

        var settling = await db.OpenItems.SingleAsync(i => i.Id == row.SettlingItemId, cancellationToken);
        var settled = await db.OpenItems.SingleAsync(i => i.Id == row.SettledItemId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(row.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < row.SettlementDate)
        {
            return Error.Validation("settlement.reversal_date_invalid", "An application is reversed on or after its date.").WithWhy(("settlementDate", row.SettlementDate), ("reversalDate", reversalDate));
        }

        Guid? reversalEntry = null;
        if (row.JournalEntryId is { } journal)
        {
            var reversed = await posting.ReverseAsync(journal, reversalDate, request.Reason.Trim(), false, cancellationToken);
            if (reversed.IsFailure)
            {
                return reversed.Error!;
            }

            reversalEntry = reversed.Value.EntryId;
        }

        payables.Move(settled, row.AmountTc, row.AmountFcSettledItem);
        payables.Move(settling, -row.AmountTc, -row.AmountFcSettlingItem);
        row.Status = "reversed";
        var mirror = payables.Mirror(row, reversalDate, reversalEntry, request.Reason.Trim());
        db.Settlements.Add(mirror);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(PayablesDocumentTypes.Settlement, row.Id, $"{settling.DocumentNumber}→{settled.DocumentNumber}", AuditActions.Reversed, After: new { reversalDate, mirror = mirror.Id }, Reason: request.Reason.Trim(), CompanyId: row.CompanyId), cancellationToken);
        return PayablesService.Map(mirror, settling, settled);
    }
}

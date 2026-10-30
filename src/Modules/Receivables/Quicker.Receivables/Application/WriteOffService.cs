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
using Quicker.Receivables.Contracts;
using Quicker.Receivables.Domain;
using Quicker.Receivables.Persistence;

namespace Quicker.Receivables.Application;

/// <summary>
/// Write-offs (roadmap 5.7, POSTING_RULES §2 "Write-off (bad debt)"): what a customer still owes on an item, or part of
/// it, leaves the books as bad debt or as a small balance not worth chasing. One journal in the item's currency, Dr
/// <c>BadDebt</c> (or <c>WriteOff</c>) / Cr <c>AR</c> at the item's booked value (nothing is realised: no cash moved),
/// opens a credit item on the customer for the amount and settles the written-off item with it (settlement kind
/// <c>write_off</c>), so the control account still equals its open items. Reversal mirrors the journal and reopens
/// the item for exactly what was written off.
/// </summary>
public sealed class WriteOffService(ReceivablesDbContext db, ReceivablesService receivables, ICompanyDirectory companies, IPartnerDirectory partners, ICustomerDirectory customers, INumberAllocator numbering, IPostingService posting, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public static readonly IReadOnlyList<string> Kinds = ["bad_debt", "small_balance"];

    public async Task<IReadOnlyList<WriteOffSummary>> ListAsync(Guid companyId, Guid? partnerId, string? status, CancellationToken cancellationToken)
    {
        var query = db.WriteOffs.AsNoTracking().Where(w => w.CompanyId == companyId);
        if (partnerId is { } p)
        {
            query = query.Where(w => w.PartnerId == p);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(w => w.Status == status);
        }

        var rows = await query.OrderByDescending(static w => w.WriteOffDate).ThenByDescending(static w => w.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<WriteOffSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<Result<WriteOffSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.WriteOffs.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        return row is null ? Error.NotFound(ReceivablesDocumentTypes.WriteOff, id) : await MapAsync(row, cancellationToken);
    }

    public async Task<Result<WriteOffSummary>> WriteOffAsync(Guid itemId, WriteOffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("write_off.reason_required", "A write-off says why.");
        }

        if (!Kinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("write_off.kind_invalid", "A write-off is bad_debt or small_balance.").WithWhy(("kind", request.Kind));
        }

        var item = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("open_item", itemId);
        }

        if (item.OriginalTc <= 0m || item.Status is not ("open" or "partially_settled"))
        {
            return Error.Conflict("write_off.item_not_open", "Only what is open on an invoice item is written off.").WithWhy(("kind", item.Kind), ("status", item.Status));
        }

        var amount = request.Amount ?? item.RemainingTc;
        if (amount <= 0m || amount > item.RemainingTc)
        {
            return Error.Validation("write_off.amount_invalid", "A write-off is a positive amount up to what is open on the item.").WithWhy(("amount", amount), ("remaining", item.RemainingTc));
        }

        var company = await companies.FindAsync(new CompanyId(item.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", item.CompanyId);
        }

        var date = request.WriteOffDate ?? clock.TodayIn(company.TimeZone);
        if (date < item.PostingDate)
        {
            return Error.Validation("write_off.date_before_item", "A write-off is not dated before the item it closes.").WithWhy(("writeOffDate", date), ("itemDate", item.PostingDate));
        }

        // The item leaves at its booked value for the part written off, so no exchange difference arises.
        var full = amount == item.RemainingTc;
        var amountFc = full ? item.RemainingFc : RoundingPolicy.Default.Round(amount * item.BookedRate, company.FunctionalCurrency.MinorUnits);
        var sameCurrency = string.Equals(item.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        var row = new ArWriteOff
        {
            Id = Guid.CreateVersion7(),
            CompanyId = item.CompanyId,
            PartnerId = item.PartnerId,
            OpenItemId = item.Id,
            WriteOffDate = date,
            Kind = request.Kind,
            Currency = item.Currency,
            AmountTc = amount,
            AmountFc = amountFc,
            Reason = request.Reason.Trim(),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(ReceivablesDocumentTypes.WriteOff, new CompanyId(row.CompanyId), "WO", "WO-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(ReceivablesDocumentTypes.WriteOff, new CompanyId(row.CompanyId), null, date, row.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        row.Number = number.Value.Text;
        var customer = await customers.FindCustomerAsync(item.CompanyId, item.PartnerId, cancellationToken);
        var keys = new PostingKeys(ReceivablesDocumentTypes.WriteOff, PartnerPostingGroupId: customer?.PostingGroupId);
        var expenseRole = request.Kind == "small_balance" ? AccountRoles.WriteOff : AccountRoles.BadDebt;
        var lines = new List<PostingLine>
        {
            new(expenseRole, amount, keys, PartnerId: item.PartnerId, AmountFc: amountFc),
            new(AccountRoles.AR, -amount, keys, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: row.Id, PartnerId: item.PartnerId, AmountFc: -amountFc),
        };
        var description = LocalizedText.Bilingual($"Write-off of {item.DocumentNumber} ({row.Number})", $"شطب {item.DocumentNumber} ({row.Number})");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "receivables", ReceivablesDocumentTypes.WriteOff, row.Id, date, item.Currency, lines, row.Number, date, description,
            item.BranchId is { } b ? new BranchId(b) : null, RateTypes.Spot, sameCurrency ? null : item.BookedRate, sameCurrency ? null : "Booked rate of the item written off", $"ar_write_off:{row.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // The write-off's own credit item, used up at once by the settlement that closes the invoice item.
        var credit = new ArOpenItem
        {
            Id = Guid.CreateVersion7(),
            CompanyId = item.CompanyId,
            PartnerId = item.PartnerId,
            Kind = ReceivableKinds.Adjustment,
            DocumentType = ReceivablesDocumentTypes.WriteOff,
            DocumentId = row.Id,
            DocumentNumber = row.Number,
            Instalment = 1,
            CustomerReference = item.CustomerReference,
            PostingDate = date,
            DocumentDate = date,
            DueDate = date,
            Currency = item.Currency,
            OriginalTc = -amount,
            OriginalFc = -amountFc,
            BookedRate = item.BookedRate,
            RemainingTc = -amount,
            RemainingFc = -amountFc,
            JournalEntryId = posted.Value.EntryId,
            SalesRepId = item.SalesRepId,
            BranchId = item.BranchId,
            Status = "open",
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        var settlement = new ArSettlement
        {
            Id = Guid.CreateVersion7(),
            CompanyId = item.CompanyId,
            SettlingItemId = credit.Id,
            SettledItemId = item.Id,
            SettlementDate = date,
            Kind = ReceivableSettlementKinds.WriteOff,
            Currency = item.Currency,
            AmountTc = amount,
            AmountFcSettledItem = amountFc,
            AmountFcSettlingItem = amountFc,
            SettlementRate = item.BookedRate,
            FxGainLossFc = 0m,
            WriteOffTc = amount,
            JournalEntryId = posted.Value.EntryId,
            Reason = row.Reason,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
        };
        receivables.Move(item, -amount, -amountFc);
        receivables.Move(credit, amount, amountFc);
        row.AdjustmentItemId = credit.Id;
        row.SettlementId = settlement.Id;
        row.JournalEntryId = posted.Value.EntryId;
        db.OpenItems.Add(credit);
        db.Settlements.Add(settlement);
        db.WriteOffs.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReceivablesDocumentTypes.WriteOff, row.Id, row.Number, AuditActions.Posted, After: new { item = item.DocumentNumber, row.Kind, row.AmountTc, row.Currency, journal = posted.Value.Number, itemRemaining = item.RemainingTc }, Reason: row.Reason, CompanyId: row.CompanyId), cancellationToken);
        return await MapAsync(row, cancellationToken);
    }

    public async Task<Result<WriteOffSummary>> ReverseAsync(Guid id, ReverseWriteOffRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("write_off.reason_required", "A reversal names its reason.");
        }

        var row = await db.WriteOffs.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ReceivablesDocumentTypes.WriteOff, id);
        }

        if (row.Status != "posted")
        {
            return Error.Conflict("write_off.not_posted", "Only a posted write-off is reversed.").WithWhy(("status", row.Status));
        }

        var company = await companies.FindAsync(new CompanyId(row.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < row.WriteOffDate)
        {
            return Error.Validation("write_off.reversal_date_invalid", "A write-off is reversed on or after its date.").WithWhy(("writeOffDate", row.WriteOffDate), ("reversalDate", reversalDate));
        }

        var settlement = await db.Settlements.SingleAsync(s => s.Id == row.SettlementId, cancellationToken);
        var item = await db.OpenItems.SingleAsync(i => i.Id == row.OpenItemId, cancellationToken);
        var credit = await db.OpenItems.SingleAsync(i => i.Id == row.AdjustmentItemId, cancellationToken);
        var reversed = await posting.ReverseAsync(row.JournalEntryId!.Value, reversalDate, request.Reason.Trim(), false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        receivables.Move(item, settlement.AmountTc, settlement.AmountFcSettledItem);
        receivables.Move(credit, -settlement.AmountTc, -settlement.AmountFcSettlingItem);
        settlement.Status = "reversed";
        db.Settlements.Add(receivables.Mirror(settlement, reversed.Value.PostingDate, reversed.Value.EntryId, request.Reason.Trim()));
        credit.Status = "reversed";
        credit.ReversedOn = reversed.Value.PostingDate;
        credit.UpdatedAt = clock.UtcNow;
        row.Status = "reversed";
        row.ReversalEntryId = reversed.Value.EntryId;
        row.ReversalReason = request.Reason.Trim();
        row.ReversedAt = clock.UtcNow;
        row.ReversedBy = principal.Principal?.MembershipId.Value;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReceivablesDocumentTypes.WriteOff, row.Id, row.Number, AuditActions.Reversed, After: new { reversalDate = reversed.Value.PostingDate, journal = reversed.Value.Number, itemRemaining = item.RemainingTc }, Reason: request.Reason.Trim(), CompanyId: row.CompanyId), cancellationToken);
        return await MapAsync(row, cancellationToken);
    }

    private async Task<WriteOffSummary> MapAsync(ArWriteOff w, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(w.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(w.CompanyId), cancellationToken);
        var item = await db.OpenItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == w.OpenItemId, cancellationToken);
        return new WriteOffSummary(w.Id, w.CompanyId, w.Number, w.Status, w.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? ReceivablesService.Empty(), w.OpenItemId, item?.DocumentNumber ?? string.Empty, w.AdjustmentItemId, w.SettlementId,
            w.WriteOffDate, w.Kind, w.Currency, w.AmountTc, w.AmountFc, company?.FunctionalCurrency.Code ?? w.Currency, w.Reason, w.JournalEntryId, w.ReversalEntryId, w.ReversalReason, w.CreatedAt);
    }
}

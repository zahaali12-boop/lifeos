using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Receivables.Domain;
using Quicker.Receivables.Persistence;

namespace Quicker.Receivables.Application;

/// <summary>
/// The receivables subledger (roadmap 5.6a, DOMAIN_MODEL §13): open items opened by invoices and settled by credit
/// notes, receipts and deposits; aging at any date computed from the items and the settlements dated up to it, never
/// from a snapshot; holds. The mirror of <c>PayablesService</c> with the signs the other way round: a positive item is
/// owed by the customer.
/// </summary>
public sealed class ReceivablesService(ReceivablesDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    private static readonly string[] Live = ["open", "partially_settled"];

    // ------------------------------------------------------------------ contract

    public async Task<ReceivableInfo> OpenAsync(NewReceivable item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var entity = new ArOpenItem
        {
            Id = Guid.CreateVersion7(),
            CompanyId = item.CompanyId,
            PartnerId = item.PartnerId,
            Kind = item.Kind,
            DocumentType = item.DocumentType,
            DocumentId = item.DocumentId,
            DocumentNumber = item.DocumentNumber,
            Instalment = item.Instalment,
            CustomerReference = item.CustomerReference,
            PostingDate = item.PostingDate,
            DocumentDate = item.DocumentDate,
            DueDate = item.DueDate,
            DiscountDate = item.DiscountDate,
            DiscountPct = item.DiscountPct,
            Currency = item.Currency,
            OriginalTc = item.AmountTc,
            OriginalFc = item.AmountFc,
            BookedRate = item.BookedRate,
            RemainingTc = item.AmountTc,
            RemainingFc = item.AmountFc,
            JournalEntryId = item.JournalEntryId,
            SalesRepId = item.SalesRepId,
            BranchId = item.BranchId,
            DimensionSetId = item.DimensionSetId,
            Status = "open",
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        db.OpenItems.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<ReceivableInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await db.OpenItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        return item is null ? null : Map(item);
    }

    public async Task<IReadOnlyList<ReceivableInfo>> ItemsOfAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) =>
        (await db.OpenItems.AsNoTracking().Where(i => i.DocumentType == documentType && i.DocumentId == documentId).OrderBy(static i => i.Instalment).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<IReadOnlyList<ReceivableInfo>>> ReverseDocumentAsync(string documentType, Guid documentId, DateOnly reversalDate, CancellationToken cancellationToken = default)
    {
        var items = await db.OpenItems.Where(i => i.DocumentType == documentType && i.DocumentId == documentId && i.Status != "reversed").ToListAsync(cancellationToken);
        var settled = items.Where(static i => i.SettledTc != 0m).Select(static i => i.DocumentNumber + (i.Instalment > 1 ? $"/{i.Instalment}" : string.Empty)).ToList();
        if (settled.Count > 0)
        {
            return Error.Conflict("receivables.item_settled", "The document's open items have been settled in part; reverse those settlements first.").WithWhy(("items", settled));
        }

        foreach (var item in items)
        {
            item.Status = "reversed";
            item.RemainingTc = 0m;
            item.RemainingFc = 0m;
            item.ReversedOn = reversalDate;
            item.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return items.Select(Map).ToList();
    }

    public async Task<Result<ReceivableSettlementInfo>> RecordAsync(RecordReceivableSettlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settling = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == request.SettlingItemId, cancellationToken);
        var settled = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == request.SettledItemId, cancellationToken);
        if (settling is null || settled is null)
        {
            return Error.NotFound("open_item", settling is null ? request.SettlingItemId : request.SettledItemId);
        }

        // The settled invoice goes down by the gross amount; the settling receipt or credit is used up by the cash part only.
        var check = Check(settling, settled, request.AmountTc, request.AmountTc - request.DiscountTakenTc - request.WhtDeductedTc);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var row = new ArSettlement
        {
            Id = Guid.CreateVersion7(),
            CompanyId = settled.CompanyId,
            SettlingItemId = settling.Id,
            SettledItemId = settled.Id,
            SettlementDate = request.SettlementDate,
            Kind = request.Kind,
            Currency = settled.Currency,
            AmountTc = request.AmountTc,
            AmountFcSettledItem = request.AmountFcSettledItem,
            AmountFcSettlingItem = request.AmountFcSettlingItem,
            SettlementRate = request.SettlementRate,
            FxGainLossFc = request.AmountFcSettlingItem - request.AmountFcSettledItem,
            DiscountTakenTc = request.DiscountTakenTc,
            WhtDeductedTc = request.WhtDeductedTc,
            JournalEntryId = request.JournalEntryId,
            Reason = request.Reason,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
        };
        Move(settled, -request.AmountTc, -request.AmountFcSettledItem);
        Move(settling, request.AmountTc - request.DiscountTakenTc - request.WhtDeductedTc, request.AmountFcSettlingItem);
        db.Settlements.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row, settling, settled);
    }

    public async Task<Result> ReverseSettlementsOfAsync(Guid journalEntryId, DateOnly reversalDate, Guid reversalEntryId, string reason, CancellationToken cancellationToken = default)
    {
        var rows = await db.Settlements.Where(s => s.JournalEntryId == journalEntryId && s.Status == "posted" && s.ReversesSettlementId == null).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return Result.Success();
        }

        var settlingIds = rows.Select(static r => r.SettlingItemId).Distinct().ToList();
        var applied = await db.Settlements.AsNoTracking().Where(s => settlingIds.Contains(s.SettlingItemId) && s.JournalEntryId != journalEntryId && s.Status == "posted" && s.ReversesSettlementId == null).ToListAsync(cancellationToken);
        if (applied.Count > 0)
        {
            return Error.Conflict("receivables.remainder_applied", "The receipt's remainder was applied to other items; reverse those applications first.").WithWhy(("settlements", applied.Select(static a => a.Id)));
        }

        var itemIds = rows.SelectMany(static r => new[] { r.SettlingItemId, r.SettledItemId }).Distinct().ToList();
        var items = await db.OpenItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(static i => i.Id, cancellationToken);
        foreach (var row in rows)
        {
            var settled = items[row.SettledItemId];
            var settling = items[row.SettlingItemId];
            Move(settled, row.AmountTc, row.AmountFcSettledItem);
            Move(settling, -(row.AmountTc - row.DiscountTakenTc - row.WhtDeductedTc), -row.AmountFcSettlingItem);
            row.Status = "reversed";
            db.Settlements.Add(Mirror(row, reversalDate, reversalEntryId, reason));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<decimal> OpenBalanceAsync(Guid companyId, Guid partnerId, string currency, CancellationToken cancellationToken = default) =>
        await db.OpenItems.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.PartnerId == partnerId && i.Currency == currency && Live.Contains(i.Status))
            .SumAsync(static i => (decimal?)i.RemainingTc, cancellationToken) ?? 0m;

    // ------------------------------------------------------------------ API

    public async Task<IReadOnlyList<ReceivableSummary>> ListAsync(Guid companyId, Guid? partnerId, string? status, string? kind, CancellationToken cancellationToken)
    {
        var query = db.OpenItems.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (partnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        query = string.IsNullOrWhiteSpace(status) ? query.Where(static i => i.Status != "reversed") : status == "live" ? query.Where(static i => Live.Contains(i.Status)) : query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(i => i.Kind == kind);
        }

        var items = await query.OrderBy(static i => i.DueDate).ThenBy(static i => i.DocumentNumber).ThenBy(static i => i.Instalment).Take(1000).ToListAsync(cancellationToken);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var names = await PartnersAsync(items.Select(static i => i.PartnerId), cancellationToken);
        return items.Select(i => new ReceivableSummary(Map(i), names.GetValueOrDefault(i.PartnerId)?.Code ?? string.Empty, names.GetValueOrDefault(i.PartnerId)?.LegalName.Values ?? Empty(), company?.FunctionalCurrency.Code ?? i.Currency, i.UpdatedAt)).ToList();
    }

    public async Task<Result<ReceivableSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.OpenItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("open_item", id);
        }

        var company = await companies.FindAsync(new CompanyId(item.CompanyId), cancellationToken);
        var partner = await partners.FindAsync(item.PartnerId, cancellationToken);
        return new ReceivableSummary(Map(item), partner?.Code ?? string.Empty, partner?.LegalName.Values ?? Empty(), company?.FunctionalCurrency.Code ?? item.Currency, item.UpdatedAt);
    }

    /// <summary>Aging at a date (hard scenario 15): items posted by then and not yet reversed, each at its balance after the settlements dated up to then, bucketed by days past due.</summary>
    public async Task<Result<ArAgingReport>> AgingAsync(Guid companyId, DateOnly? asOfDate, Guid? partnerId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var asOf = asOfDate ?? clock.TodayIn(company.TimeZone);
        var query = db.OpenItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.PostingDate <= asOf && (i.ReversedOn == null || i.ReversedOn > asOf));
        if (partnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        var items = await query.ToListAsync(cancellationToken);
        var ids = items.Select(static i => i.Id).ToList();
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.CompanyId == companyId && s.SettlementDate <= asOf && (ids.Contains(s.SettledItemId) || ids.Contains(s.SettlingItemId))).ToListAsync(cancellationToken);
        var remaining = items.ToDictionary(static i => i.Id, static i => i.OriginalFc);
        foreach (var s in settlements)
        {
            if (remaining.ContainsKey(s.SettledItemId))
            {
                remaining[s.SettledItemId] -= s.AmountFcSettledItem;
            }

            if (remaining.ContainsKey(s.SettlingItemId))
            {
                remaining[s.SettlingItemId] += s.AmountFcSettlingItem;
            }
        }

        var names = await PartnersAsync(items.Select(static i => i.PartnerId), cancellationToken);
        var rows = new List<ArAgingRow>();
        foreach (var group in items.GroupBy(static i => i.PartnerId).OrderBy(g => names.GetValueOrDefault(g.Key)?.Code, StringComparer.Ordinal))
        {
            decimal notDue = 0m, d30 = 0m, d60 = 0m, d90 = 0m, over = 0m, deposits = 0m;
            var count = 0;
            foreach (var item in group)
            {
                var value = remaining[item.Id];
                if (value == 0m)
                {
                    continue;
                }

                count++;
                if (item.Kind == ReceivableKinds.Deposit)
                {
                    deposits += value;
                    continue;
                }

                var days = asOf.DayNumber - item.DueDate.DayNumber;
                if (days <= 0)
                {
                    notDue += value;
                }
                else if (days <= 30)
                {
                    d30 += value;
                }
                else if (days <= 60)
                {
                    d60 += value;
                }
                else if (days <= 90)
                {
                    d90 += value;
                }
                else
                {
                    over += value;
                }
            }

            if (count == 0)
            {
                continue;
            }

            var partner = names.GetValueOrDefault(group.Key);
            rows.Add(new ArAgingRow(group.Key, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? Empty(), count, notDue, d30, d60, d90, over, notDue + d30 + d60 + d90 + over, deposits));
        }

        var totals = new ArAgingTotals(rows.Sum(static r => r.NotDue), rows.Sum(static r => r.Days1To30), rows.Sum(static r => r.Days31To60), rows.Sum(static r => r.Days61To90), rows.Sum(static r => r.Over90), rows.Sum(static r => r.TotalFc), rows.Sum(static r => r.DepositsFc));
        return new ArAgingReport(companyId, asOf, company.FunctionalCurrency.Code, rows, totals);
    }

    /// <summary>
    /// A customer's statement over a period, per document currency: the balance owed before it, every document booked in
    /// it (invoices raise it; credit notes, receipts and deposits lower it) and every reversal in it (the document's
    /// amount taken back on the day it was reversed), with the running balance. Settlements only match documents
    /// against each other, so they move nothing here; the closing balance is what the open items still hold.
    /// </summary>
    public async Task<Result<CustomerStatement>> StatementAsync(Guid companyId, Guid partnerId, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken)
    {
        if (principal.Required.ScopesFor(ReceivablesPermissions.OpenItemRead) is not { } scopes || !scopes.AllowsCompany(companyId))
        {
            return Error.Forbidden("statement.company_forbidden", "You may not read this company's receivables.").WithWhy(("companyId", companyId));
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var partner = (await PartnersAsync([partnerId], cancellationToken)).GetValueOrDefault(partnerId);
        if (partner is null)
        {
            return Error.NotFound("partner", partnerId);
        }

        var to = toDate ?? clock.TodayIn(company.TimeZone);
        var from = fromDate ?? new DateOnly(to.Year, to.Month, 1);
        if (to < from)
        {
            return Error.Validation("statement.period_invalid", "The period ends on or after it starts.").WithWhy(("from", from), ("to", to));
        }

        var items = await db.OpenItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.PartnerId == partnerId && i.PostingDate <= to).ToListAsync(cancellationToken);
        var movements = items.Select(static i => (Date: i.PostingDate, Item: i, Reversal: false))
            .Concat(items.Where(i => i.ReversedOn is { } r && r <= to).Select(static i => (Date: i.ReversedOn!.Value, Item: i, Reversal: true)))
            .ToList();
        var currencies = new List<ArStatementCurrency>();
        foreach (var currency in movements.GroupBy(static m => m.Item.Currency, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            decimal Signed((DateOnly Date, ArOpenItem Item, bool Reversal) m) => m.Reversal ? -m.Item.OriginalTc : m.Item.OriginalTc;
            var opening = currency.Where(m => m.Date < from).Sum(Signed);
            var balance = opening;
            var lines = new List<ArStatementLine>();
            foreach (var m in currency.Where(m => m.Date >= from).OrderBy(static m => m.Date).ThenBy(static m => m.Reversal).ThenBy(static m => m.Item.CreatedAt).ThenBy(static m => m.Item.Id))
            {
                var amount = Signed(m);
                balance += amount;
                lines.Add(new ArStatementLine(m.Date, m.Item.Kind, m.Item.DocumentType, m.Item.DocumentId, m.Item.DocumentNumber, m.Item.CustomerReference, m.Reversal ? null : m.Item.DueDate, m.Reversal, amount, balance));
            }

            currencies.Add(new ArStatementCurrency(currency.Key, opening, lines.Where(static l => l.Amount > 0m).Sum(static l => l.Amount), -lines.Where(static l => l.Amount < 0m).Sum(static l => l.Amount), balance, lines));
        }

        return new CustomerStatement(companyId, partnerId, partner.Code, partner.LegalName.Values, from, to, currencies);
    }

    public async Task<Result<ReceivableSummary>> HoldAsync(Guid id, HoldReceivableRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("receivables.hold_reason_required", "A hold names its reason.");
        }

        var item = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("open_item", id);
        }

        if (!Live.Contains(item.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("receivables.item_not_open", "Only an open item is held.").WithWhy(("status", item.Status));
        }

        item.OnHold = true;
        item.HoldReason = request.Reason.Trim();
        item.HeldBy = principal.Principal?.MembershipId.Value;
        item.HeldAt = clock.UtcNow;
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("open_item", item.Id, item.DocumentNumber, AuditActions.StateChanged, After: new { onHold = true, reason = item.HoldReason }, Reason: item.HoldReason, CompanyId: item.CompanyId), cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<Result<ReceivableSummary>> ReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.OpenItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("open_item", id);
        }

        if (!item.OnHold)
        {
            return Error.Conflict("receivables.item_not_held", "The item is not held.");
        }

        item.OnHold = false;
        item.HoldReason = null;
        item.HeldBy = null;
        item.HeldAt = null;
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("open_item", item.Id, item.DocumentNumber, AuditActions.StateChanged, After: new { onHold = false }, CompanyId: item.CompanyId), cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    internal static Result Check(ArOpenItem settling, ArOpenItem settled, decimal amountTc, decimal? settlingUse = null)
    {
        var use = settlingUse ?? amountTc;
        if (amountTc <= 0m || use < 0m)
        {
            return Error.Validation("settlement.amount_invalid", "The amount settled is positive.").WithWhy(("amount", amountTc));
        }

        if (settling.CompanyId != settled.CompanyId || settling.PartnerId != settled.PartnerId)
        {
            return Error.Validation("settlement.partner_mismatch", "Both items belong to the same customer and company.");
        }

        if (!string.Equals(settling.Currency, settled.Currency, StringComparison.Ordinal))
        {
            return Error.Validation("settlement.currency_mismatch", "An item is settled in its own currency.").WithWhy(("settlingCurrency", settling.Currency), ("settledCurrency", settled.Currency));
        }

        if (settled.OriginalTc <= 0m || settling.OriginalTc >= 0m)
        {
            return Error.Validation("settlement.kinds_invalid", "A credit note, deposit or receipt is applied to an invoice open item.").WithWhy(("settlingKind", settling.Kind), ("settledKind", settled.Kind));
        }

        if (!Live.Contains(settling.Status, StringComparer.Ordinal) || !Live.Contains(settled.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("settlement.item_closed", "Only open items are settled.").WithWhy(("settlingStatus", settling.Status), ("settledStatus", settled.Status));
        }

        if (settled.OnHold)
        {
            return Error.Conflict("settlement.item_blocked", "The invoice is held.").WithWhy(("reason", settled.HoldReason));
        }

        if (amountTc > settled.RemainingTc || use > -settling.RemainingTc)
        {
            return Error.Validation("settlement.amount_exceeds", "The amount exceeds what is open on one of the items.").WithWhy(("amount", amountTc), ("settlingUse", use), ("settlingRemaining", -settling.RemainingTc), ("settledRemaining", settled.RemainingTc));
        }

        return Result.Success();
    }

    /// <summary>Moves an item by a signed settled amount: negative for the settled invoice going down, positive for a credit or receipt being used up.</summary>
    internal void Move(ArOpenItem item, decimal amountTc, decimal amountFc)
    {
        item.SettledTc -= amountTc;
        item.SettledFc -= amountFc;
        item.RemainingTc += amountTc;
        item.RemainingFc += amountFc;
        item.Status = item.RemainingTc == 0m ? "settled" : item.SettledTc == 0m ? "open" : "partially_settled";
        item.UpdatedAt = clock.UtcNow;
    }

    internal ArSettlement Mirror(ArSettlement row, DateOnly reversalDate, Guid? reversalEntryId, string reason) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = row.CompanyId,
        SettlingItemId = row.SettlingItemId,
        SettledItemId = row.SettledItemId,
        SettlementDate = reversalDate,
        Kind = row.Kind,
        Currency = row.Currency,
        AmountTc = -row.AmountTc,
        AmountFcSettledItem = -row.AmountFcSettledItem,
        AmountFcSettlingItem = -row.AmountFcSettlingItem,
        SettlementRate = row.SettlementRate,
        FxGainLossFc = -row.FxGainLossFc,
        DiscountTakenTc = -row.DiscountTakenTc,
        WhtDeductedTc = -row.WhtDeductedTc,
        WriteOffTc = -row.WriteOffTc,
        BankChargeTc = -row.BankChargeTc,
        JournalEntryId = reversalEntryId,
        ReversesSettlementId = row.Id,
        Reason = reason,
        CreatedBy = principal.Principal?.MembershipId.Value,
        CreatedAt = clock.UtcNow,
    };

    internal async Task<Dictionary<Guid, PartnerInfo>> PartnersAsync(IEnumerable<Guid> partnerIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, PartnerInfo>();
        foreach (var id in partnerIds.Distinct())
        {
            var partner = await partners.FindAsync(id, cancellationToken);
            if (partner is not null)
            {
                result[id] = partner;
            }
        }

        return result;
    }

    internal static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    internal static ReceivableInfo Map(ArOpenItem o) => new(o.Id, o.CompanyId, o.PartnerId, o.Kind, o.DocumentType, o.DocumentId, o.DocumentNumber, o.Instalment, o.CustomerReference, o.PostingDate, o.DocumentDate, o.DueDate, o.DiscountDate, o.DiscountPct, o.Currency,
        o.OriginalTc, o.OriginalFc, o.BookedRate, o.SettledTc, o.SettledFc, o.RemainingTc, o.RemainingFc, o.OnHold, o.HoldReason, o.DunningLevel, o.PromisedPaymentDate, o.JournalEntryId, o.SalesRepId, o.BranchId, o.Status);

    internal static ReceivableSettlementInfo Map(ArSettlement s, ArOpenItem settling, ArOpenItem settled) => new(s.Id, s.CompanyId, s.SettlingItemId, settling.DocumentNumber, settling.Kind, s.SettledItemId, settled.DocumentNumber, s.SettlementDate, s.Kind, s.Currency, s.AmountTc, s.AmountFcSettledItem, s.AmountFcSettlingItem, s.SettlementRate, s.FxGainLossFc,
        s.DiscountTakenTc, s.WhtDeductedTc, s.WriteOffTc, s.BankChargeTc, s.JournalEntryId, s.ReversesSettlementId, s.Reason, s.Status, s.CreatedAt);
}

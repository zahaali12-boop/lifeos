using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

public sealed record PricePoint(DateOnly Date, string Source, string DocumentNumber, Guid PartnerId, string PartnerCode, Guid ItemId, string ItemCode, decimal UnitPrice, string Currency, decimal? UnitPriceFc);

public sealed record PriceSummaryRow(Guid ItemId, string ItemCode, Guid PartnerId, string PartnerCode, int Points, DateOnly LastDate, decimal LastPriceFc, decimal MinPriceFc, decimal MaxPriceFc, decimal AveragePriceFc);

public sealed record PriceHistory(string FunctionalCurrency, IReadOnlyList<PricePoint> Points, IReadOnlyList<PriceSummaryRow> Summary);

public sealed record LeadTimeRow(Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, int StatedLeadTimeDays, int Receipts, decimal AverageDays, decimal MedianDays, int MinDays, int MaxDays, decimal? OnTimePct);

public sealed record ScorecardRow(Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, int ReceiptLines, int Invoices, decimal? OnTimePct, decimal? QuantityPct, decimal? PricePct, decimal? InvoicePct, decimal? Score, string? Grade);

public sealed record Scorecard(Guid CompanyId, DateOnly AsOf, DateOnly From, ScoringSettingsSummary Settings, IReadOnlyList<ScorecardRow> Rows);

public sealed record ScoringSettingsSummary(Guid CompanyId, decimal OnTimeWeight, decimal QuantityWeight, decimal PriceWeight, decimal InvoiceWeight, int OnTimeToleranceDays, int LookbackMonths, bool IsDefault);

public sealed record SaveScoringSettingsRequest(Guid CompanyId, decimal OnTimeWeight, decimal QuantityWeight, decimal PriceWeight, decimal InvoiceWeight, int OnTimeToleranceDays = 0, int LookbackMonths = 12);

/// <summary>
/// Supplier intelligence (roadmap 4.8): read-only views over the purchasing documents as they stand. Price history from
/// order lines, posted invoice lines and quotes; lead times from order date to receipt posting date; a scorecard over
/// a look-back window weighing on-time receipts, quantity kept (not returned), invoice prices within the supplier's
/// tolerance and invoices matched first time. Nothing is snapshotted, so every figure reflects reversals as they happen.
/// </summary>
public sealed class SupplierIntelligenceService(PurchasingDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, IItemDirectory items, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    private static readonly string[] LiveOrderStatuses = ["approved", "sent", "partially_received", "received", "closed"];

    public async Task<PriceHistory> PriceHistoryAsync(Guid companyId, Guid? itemId, Guid? partnerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var points = new List<(DateOnly Date, string Source, string Number, Guid Partner, Guid Item, decimal Price, string Currency, decimal? Fc)>();

        var orderRows = await (from l in db.OrderLines.AsNoTracking()
                               join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                               where o.CompanyId == companyId && LiveOrderStatuses.Contains(o.Status)
                               select new { o.OrderDate, o.Number, o.PartnerId, l.ItemId, l.UnitPrice, l.DiscountPct, o.Currency, o.ExchangeRate }).ToListAsync(cancellationToken);
        foreach (var r in orderRows)
        {
            var price = Round(r.UnitPrice * (1m - r.DiscountPct / 100m));
            points.Add((r.OrderDate, "order", r.Number, r.PartnerId, r.ItemId, price, r.Currency, Round(price * r.ExchangeRate)));
        }

        var invoiceRows = await (from l in db.InvoiceLines.AsNoTracking()
                                 join i in db.Invoices.AsNoTracking() on new { l.TenantId, Id = l.InvoiceId } equals new { i.TenantId, i.Id }
                                 where i.CompanyId == companyId && i.Status == "posted" && i.Kind == "invoice" && l.ItemId != null && (l.Kind == "receipt" || l.Kind == "order")
                                 select new { i.PostingDate, i.Number, i.PartnerId, ItemId = l.ItemId!.Value, l.UnitPrice, l.DiscountPct, i.Currency, i.ExchangeRate }).ToListAsync(cancellationToken);
        foreach (var r in invoiceRows)
        {
            var price = Round(r.UnitPrice * (1m - r.DiscountPct / 100m));
            points.Add((r.PostingDate, "invoice", r.Number, r.PartnerId, r.ItemId, price, r.Currency, Round(price * r.ExchangeRate)));
        }

        var quoteRows = await (from ql in db.QuoteLines.AsNoTracking()
                               join q in db.Quotes.AsNoTracking() on new { ql.TenantId, Id = ql.QuoteId } equals new { q.TenantId, q.Id }
                               join rs in db.RfqSuppliers.AsNoTracking() on new { q.TenantId, Id = q.RfqSupplierId } equals new { rs.TenantId, rs.Id }
                               join rfq in db.Rfqs.AsNoTracking() on new { rs.TenantId, Id = rs.RfqId } equals new { rfq.TenantId, rfq.Id }
                               join rl in db.RfqLines.AsNoTracking() on new { ql.TenantId, Id = ql.RfqLineId } equals new { rl.TenantId, rl.Id }
                               where rfq.CompanyId == companyId && rl.ItemId != null
                               select new { q.ReceivedAt, rfq.Number, rs.PartnerId, ItemId = rl.ItemId!.Value, ql.UnitPrice, q.Currency }).ToListAsync(cancellationToken);
        foreach (var r in quoteRows)
        {
            // A quote has no rate of its own; its company-currency value is left out rather than guessed.
            var fc = company is not null && string.Equals(r.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal) ? r.UnitPrice : (decimal?)null;
            points.Add((DateOnly.FromDateTime(r.ReceivedAt.UtcDateTime), "quote", r.Number, r.PartnerId, r.ItemId, r.UnitPrice, r.Currency, fc));
        }

        var filtered = points.Where(p => (itemId is null || p.Item == itemId) && (partnerId is null || p.Partner == partnerId) && (from is null || p.Date >= from) && (to is null || p.Date <= to))
            .OrderByDescending(static p => p.Date).ThenBy(static p => p.Source, StringComparer.Ordinal).ThenBy(static p => p.Number, StringComparer.Ordinal).Take(2000).ToList();
        var partnerCodes = await PartnerCodesAsync(filtered.Select(static p => p.Partner), cancellationToken);
        var itemCodes = await ItemCodesAsync(filtered.Select(static p => p.Item), cancellationToken);
        var result = filtered.Select(p => new PricePoint(p.Date, p.Source, p.Number, p.Partner, partnerCodes.GetValueOrDefault(p.Partner, string.Empty), p.Item, itemCodes.GetValueOrDefault(p.Item, string.Empty), p.Price, p.Currency, p.Fc)).ToList();

        // The summary counts what was actually bought (orders and invoices), in the company's currency.
        var summary = result.Where(static p => p.Source != "quote" && p.UnitPriceFc is not null)
            .GroupBy(static p => (p.ItemId, p.PartnerId))
            .Select(g =>
            {
                var ordered = g.OrderByDescending(static p => p.Date).ToList();
                var values = ordered.Select(static p => p.UnitPriceFc!.Value).ToList();
                return new PriceSummaryRow(g.Key.ItemId, ordered[0].ItemCode, g.Key.PartnerId, ordered[0].PartnerCode, values.Count, ordered[0].Date, values[0], values.Min(), values.Max(), Round(values.Average()));
            })
            .OrderBy(static r => r.ItemCode, StringComparer.Ordinal).ThenBy(static r => r.AveragePriceFc).ToList();
        return new PriceHistory(company?.FunctionalCurrency.Code ?? string.Empty, result, summary);
    }

    public async Task<IReadOnlyList<LeadTimeRow>> LeadTimesAsync(Guid companyId, Guid? partnerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var rows = await ReceiptRowsAsync(companyId, partnerId, from, to, cancellationToken);
        var result = new List<LeadTimeRow>();
        foreach (var group in rows.GroupBy(static r => r.PartnerId))
        {
            var partner = await partners.FindAsync(group.Key, cancellationToken);
            var supplier = await partners.FindSupplierAsync(companyId, group.Key, cancellationToken);
            var days = group.Select(static r => r.ReceivedOn.DayNumber - r.OrderDate.DayNumber).OrderBy(static d => d).ToList();
            var withExpected = group.Where(static r => r.Expected is not null).ToList();
            decimal? onTime = withExpected.Count == 0 ? null : Pct(withExpected.Count(static r => r.ReceivedOn <= r.Expected!.Value), withExpected.Count);
            result.Add(new LeadTimeRow(group.Key, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), supplier?.LeadTimeDays ?? 0, days.Count,
                Round((decimal)days.Average()), Median(days), days[0], days[^1], onTime));
        }

        return result.OrderBy(static r => r.PartnerCode, StringComparer.Ordinal).ToList();
    }

    public async Task<Result<Scorecard>> ScorecardAsync(Guid companyId, DateOnly? asOfDate, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var settings = await SettingsCoreAsync(companyId, cancellationToken);
        var asOf = asOfDate ?? clock.TodayIn(company.TimeZone);
        var from = asOf.AddMonths(-settings.LookbackMonths).AddDays(1);
        var receipts = await ReceiptRowsAsync(companyId, null, from, asOf, cancellationToken);

        var invoices = await db.Invoices.AsNoTracking().Where(i => i.CompanyId == companyId && i.Status == "posted" && i.Kind == "invoice" && i.PostingDate >= from && i.PostingDate <= asOf)
            .Select(static i => new { i.Id, i.PartnerId }).ToListAsync(cancellationToken);
        var invoiceIds = invoices.Select(static i => i.Id).ToList();
        var matches = await db.MatchResults.AsNoTracking().Where(m => invoiceIds.Contains(m.InvoiceId)).OrderBy(static m => m.MatchedAt).ToListAsync(cancellationToken);
        var firstMatch = matches.GroupBy(static m => m.InvoiceId).ToDictionary(static g => g.Key, static g => g.First());
        var lastTolerance = matches.GroupBy(static m => m.InvoiceId).ToDictionary(static g => g.Key, static g => g.Last().PriceTolerancePct);
        var pricedLines = await db.InvoiceLines.AsNoTracking().Where(l => invoiceIds.Contains(l.InvoiceId) && (l.Kind == "receipt" || l.Kind == "order") && l.PriceVariancePct != null)
            .Select(static l => new { l.InvoiceId, l.PriceVariancePct }).ToListAsync(cancellationToken);
        var partnerOfInvoice = invoices.ToDictionary(static i => i.Id, static i => i.PartnerId);

        var partnerIds = receipts.Select(static r => r.PartnerId).Concat(invoices.Select(static i => i.PartnerId)).Distinct().ToList();
        var rows = new List<ScorecardRow>();
        foreach (var partnerId in partnerIds)
        {
            var own = receipts.Where(r => r.PartnerId == partnerId).ToList();
            var withExpected = own.Where(static r => r.Expected is not null).ToList();
            decimal? onTime = withExpected.Count == 0 ? null : Pct(withExpected.Count(r => r.ReceivedOn <= r.Expected!.Value.AddDays(settings.OnTimeToleranceDays)), withExpected.Count);
            var received = own.Sum(static r => r.Quantity);
            decimal? quantity = received == 0m ? null : Round(Math.Max(0m, 100m - own.Sum(static r => r.Returned) / received * 100m));
            var ownInvoices = invoices.Where(i => i.PartnerId == partnerId).Select(static i => i.Id).ToList();
            var ownLines = pricedLines.Where(l => partnerOfInvoice[l.InvoiceId] == partnerId).ToList();
            decimal? price = ownLines.Count == 0 ? null : Pct(ownLines.Count(l => Math.Abs(l.PriceVariancePct!.Value) <= lastTolerance.GetValueOrDefault(l.InvoiceId)), ownLines.Count);
            var matched = ownInvoices.Where(firstMatch.ContainsKey).ToList();
            decimal? invoice = matched.Count == 0 ? null : Pct(matched.Count(id => firstMatch[id].Status == "matched"), matched.Count);

            var parts = new (decimal? Value, decimal Weight)[] { (onTime, settings.OnTimeWeight), (quantity, settings.QuantityWeight), (price, settings.PriceWeight), (invoice, settings.InvoiceWeight) }.Where(static p => p.Value is not null && p.Weight > 0m).ToList();
            var weight = parts.Sum(static p => p.Weight);
            decimal? score = weight == 0m ? null : Round(parts.Sum(static p => p.Value!.Value * p.Weight) / weight);
            var partner = await partners.FindAsync(partnerId, cancellationToken);
            rows.Add(new ScorecardRow(partnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), own.Count, ownInvoices.Count, onTime, quantity, price, invoice, score, Grade(score)));
        }

        return new Scorecard(companyId, asOf, from, settings, rows.OrderByDescending(static r => r.Score ?? -1m).ThenBy(static r => r.PartnerCode, StringComparer.Ordinal).ToList());
    }

    public async Task<Result<ScoringSettingsSummary>> SettingsAsync(Guid companyId, CancellationToken cancellationToken) => await SettingsCoreAsync(companyId, cancellationToken);

    public async Task<Result<ScoringSettingsSummary>> SaveSettingsAsync(SaveScoringSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("scoring.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var weights = new[] { request.OnTimeWeight, request.QuantityWeight, request.PriceWeight, request.InvoiceWeight };
        if (weights.Any(static w => w < 0m || w > 100m) || weights.Sum() <= 0m)
        {
            return Error.Validation("scoring.weights_invalid", "Weights are between 0 and 100 and at least one is above zero.").WithWhy(("weights", weights));
        }

        if (request.OnTimeToleranceDays is < 0 or > 90 || request.LookbackMonths is < 1 or > 60)
        {
            return Error.Validation("scoring.window_invalid", "The on-time tolerance is 0 to 90 days and the look-back 1 to 60 months.").WithWhy(("toleranceDays", request.OnTimeToleranceDays), ("lookbackMonths", request.LookbackMonths));
        }

        var row = await db.ScoringSettings.SingleOrDefaultAsync(s => s.CompanyId == request.CompanyId, cancellationToken);
        if (row is null)
        {
            row = new ScoringSettings { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
            db.ScoringSettings.Add(row);
        }

        row.OnTimeWeight = request.OnTimeWeight;
        row.QuantityWeight = request.QuantityWeight;
        row.PriceWeight = request.PriceWeight;
        row.InvoiceWeight = request.InvoiceWeight;
        row.OnTimeToleranceDays = request.OnTimeToleranceDays;
        row.LookbackMonths = request.LookbackMonths;
        row.UpdatedBy = principal.Principal?.MembershipId.Value;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("scoring_settings", row.Id, company.Code, AuditActions.Updated, After: new { weights, request.OnTimeToleranceDays, request.LookbackMonths }, CompanyId: request.CompanyId), cancellationToken);
        return Map(row, false);
    }

    // ------------------------------------------------------------------ internals

    private sealed record ReceiptRow(Guid PartnerId, DateOnly OrderDate, DateOnly ReceivedOn, DateOnly? Expected, decimal Quantity, decimal Returned);

    private async Task<List<ReceiptRow>> ReceiptRowsAsync(Guid companyId, Guid? partnerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = from l in db.ReceiptLines.AsNoTracking()
                    join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                    join ol in db.OrderLines.AsNoTracking() on new { l.TenantId, Id = l.OrderLineId } equals new { ol.TenantId, ol.Id }
                    join o in db.Orders.AsNoTracking() on new { ol.TenantId, Id = ol.OrderId } equals new { o.TenantId, o.Id }
                    where r.CompanyId == companyId && r.Status == "posted"
                    select new { r.PartnerId, o.OrderDate, r.PostingDate, LineExpected = ol.ExpectedDate, OrderExpected = o.ExpectedDate, l.Quantity, l.QtyReturned };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.PartnerId == p);
        }

        if (from is { } f)
        {
            query = query.Where(x => x.PostingDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(x => x.PostingDate <= t);
        }

        return (await query.ToListAsync(cancellationToken)).Select(static x => new ReceiptRow(x.PartnerId, x.OrderDate, x.PostingDate, x.LineExpected ?? x.OrderExpected, x.Quantity, x.QtyReturned)).ToList();
    }

    private async Task<ScoringSettingsSummary> SettingsCoreAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var row = await db.ScoringSettings.AsNoTracking().SingleOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);
        return row is null ? Map(new ScoringSettings { CompanyId = companyId }, true) : Map(row, false);
    }

    private static ScoringSettingsSummary Map(ScoringSettings s, bool isDefault) => new(s.CompanyId, s.OnTimeWeight, s.QuantityWeight, s.PriceWeight, s.InvoiceWeight, s.OnTimeToleranceDays, s.LookbackMonths, isDefault);

    private async Task<Dictionary<Guid, string>> PartnerCodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
        {
            result[id] = (await partners.FindAsync(id, cancellationToken))?.Code ?? string.Empty;
        }

        return result;
    }

    private async Task<Dictionary<Guid, string>> ItemCodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
        {
            result[id] = (await items.FindAsync(id, cancellationToken))?.Code ?? string.Empty;
        }

        return result;
    }

    private static decimal Round(decimal value) => RoundingPolicy.Default.Round(value, 2);

    private static decimal Pct(int part, int whole) => Round(part * 100m / whole);

    private static decimal Median(List<int> sorted) => sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : Round((sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2m);

    private static string? Grade(decimal? score) => score switch
    {
        null => null,
        >= 90m => "A",
        >= 75m => "B",
        >= 60m => "C",
        _ => "D",
    };
}

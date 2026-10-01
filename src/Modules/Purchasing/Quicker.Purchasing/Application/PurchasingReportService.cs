using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>One order line with goods still to arrive: what was ordered, what came, what is open and whether it is late.</summary>
public sealed record OpenOrderLine(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    DateOnly OrderDate,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? WarehouseId,
    string? WarehouseCode,
    int LineNo,
    string UomCode,
    decimal Ordered,
    decimal Received,
    decimal Open,
    decimal NetUnitPrice,
    decimal OpenValue,
    string Currency,
    DateOnly? ExpectedDate,
    int? DaysLate);

public sealed record CurrencyTotal(string Currency, decimal Amount);

public sealed record OpenOrderLinesReport(DateOnly AsOf, IReadOnlyList<OpenOrderLine> Lines, IReadOnlyList<CurrencyTotal> Totals, int LateLines);

/// <summary>One group of a purchase analysis: a supplier, an item or a month, with its orders and values in the company's currency.</summary>
public sealed record PurchaseAnalysisRow(string Key, string Code, IReadOnlyDictionary<string, string> Name, int Orders, decimal? Quantity, string? UomCode, decimal Ordered, decimal Received, decimal Invoiced);

public sealed record PurchaseAnalysis(Guid CompanyId, string Currency, string GroupBy, DateOnly From, DateOnly To, IReadOnlyList<PurchaseAnalysisRow> Rows, decimal Ordered, decimal Received, decimal Invoiced, int Orders);

/// <summary>
/// Purchasing inquiries for buyers. Open order lines: the stock lines of approved, sent and partly received orders with
/// goods still to arrive (ordered less cancelled less received), valued at the net order price in the order's currency,
/// late when the expected date (the line's, else the order's) has passed. Service lines, which are invoiced rather than
/// received, are left out.
/// </summary>
public sealed class PurchasingReportService(PurchasingDbContext db, IItemDirectory items, IPartnerDirectory partners, IWarehouseDirectory warehouses, ICompanyDirectory companies, ICurrentPrincipal principal, IClock clock)
{
    private static readonly string[] OpenStatuses = ["approved", "sent", "partially_received"];

    private static readonly string[] OpenLineStatuses = ["open", "partially_received"];

    private static readonly string[] OrderedStatuses = ["approved", "sent", "partially_received", "received", "closed"];

    public static readonly IReadOnlyList<string> AnalysisGroupings = ["supplier", "item", "month"];

    public async Task<Result<OpenOrderLinesReport>> OpenOrderLinesAsync(Guid companyId, Guid? partnerId, Guid? warehouseId, Guid? itemId, bool lateOnly, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (principal.Required.ScopesFor(PurchasingPermissions.OrderRead) is not { } scopes || !scopes.AllowsCompany(companyId))
        {
            return Error.Forbidden("report.company_forbidden", "You may not read this company's purchase orders.").WithWhy(("companyId", companyId));
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var day = asOf ?? clock.TodayIn(company.TimeZone);
        var query = from l in db.OrderLines.AsNoTracking()
                    join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                    where o.CompanyId == companyId && OpenStatuses.Contains(o.Status) && OpenLineStatuses.Contains(l.Status) && l.Quantity - l.QtyCancelled - l.QtyReceived > 0m
                    select new { Line = l, Order = o };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.Order.PartnerId == p);
        }

        if (itemId is { } i)
        {
            query = query.Where(x => x.Line.ItemId == i);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(x => (x.Line.WarehouseId ?? x.Order.WarehouseId) == w);
        }

        var rows = await query.OrderBy(static x => x.Line.ExpectedDate ?? x.Order.ExpectedDate).ThenBy(static x => x.Order.Number).ThenBy(static x => x.Line.LineNo).ToListAsync(cancellationToken);
        var itemCache = new Dictionary<Guid, ItemInfo?>();
        var uomCache = new Dictionary<Guid, IReadOnlyList<ItemUomInfo>>();
        var partnerCache = new Dictionary<Guid, PartnerInfo?>();
        var warehouseCache = new Dictionary<Guid, WarehouseInfo?>();
        var minorUnits = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<OpenOrderLine>();
        foreach (var row in rows)
        {
            if (!itemCache.TryGetValue(row.Line.ItemId, out var item))
            {
                item = itemCache[row.Line.ItemId] = await items.FindAsync(row.Line.ItemId, cancellationToken);
            }

            if (item is null || !item.IsStockItem)
            {
                continue;
            }

            var expected = row.Line.ExpectedDate ?? row.Order.ExpectedDate;
            var late = expected is { } e && e < day ? day.DayNumber - e.DayNumber : (int?)null;
            if (lateOnly && late is null)
            {
                continue;
            }

            if (!uomCache.TryGetValue(item.Id, out var units))
            {
                units = uomCache[item.Id] = await items.UomsAsync(item.Id, cancellationToken);
            }

            if (!partnerCache.TryGetValue(row.Order.PartnerId, out var partner))
            {
                partner = partnerCache[row.Order.PartnerId] = await partners.FindAsync(row.Order.PartnerId, cancellationToken);
            }

            WarehouseInfo? warehouse = null;
            if ((row.Line.WarehouseId ?? row.Order.WarehouseId) is { } lineWarehouse && !warehouseCache.TryGetValue(lineWarehouse, out warehouse))
            {
                warehouse = warehouseCache[lineWarehouse] = await warehouses.FindAsync(lineWarehouse, cancellationToken);
            }

            if (!minorUnits.TryGetValue(row.Order.Currency, out var decimals))
            {
                decimals = minorUnits[row.Order.Currency] = (await companies.FindCurrencyAsync(row.Order.Currency, cancellationToken))?.MinorUnits ?? 2;
            }

            var ordered = row.Line.Quantity - row.Line.QtyCancelled;
            var open = ordered - row.Line.QtyReceived;
            var netPrice = row.Line.UnitPrice * (1m - (row.Line.DiscountPct / 100m));
            lines.Add(new OpenOrderLine(row.Order.Id, row.Order.Number, row.Order.Status, row.Order.OrderDate, row.Order.PartnerId, partner?.Code ?? string.Empty,
                partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), item.Id, item.Code, item.Name.Values, warehouse?.Id, warehouse?.Code,
                row.Line.LineNo, units.FirstOrDefault(u => u.UomId == row.Line.UomId)?.UomCode ?? string.Empty, ordered, row.Line.QtyReceived, open,
                RoundingPolicy.Default.Round(netPrice, 4), RoundingPolicy.Default.Round(open * netPrice, decimals), row.Order.Currency, expected, late));
        }

        var totals = lines.GroupBy(static l => l.Currency, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => new CurrencyTotal(g.Key, g.Sum(static l => l.OpenValue))).ToList();
        return new OpenOrderLinesReport(day, lines, totals, lines.Count(static l => l.DaysLate is not null));
    }

    /// <summary>
    /// What was bought over a period, grouped by supplier, item or month of the order date: approved, sent, received and
    /// closed orders (cancelled quantities left out) valued at the net order price converted at each order's rate into
    /// the company's currency, with what of it has been received and invoiced. Quantities are summed per item only.
    /// </summary>
    public async Task<Result<PurchaseAnalysis>> AnalysisAsync(Guid companyId, DateOnly periodStart, DateOnly periodEnd, string? groupBy, Guid? partnerId, Guid? itemId, CancellationToken cancellationToken)
    {
        if (principal.Required.ScopesFor(PurchasingPermissions.OrderRead) is not { } scopes || !scopes.AllowsCompany(companyId))
        {
            return Error.Forbidden("report.company_forbidden", "You may not read this company's purchase orders.").WithWhy(("companyId", companyId));
        }

        var grouping = string.IsNullOrWhiteSpace(groupBy) ? "supplier" : groupBy.Trim().ToLowerInvariant();
        if (!AnalysisGroupings.Contains(grouping, StringComparer.Ordinal))
        {
            return Error.Validation("report.group_by_invalid", "Group by supplier, item or month.").WithWhy(("groupBy", groupBy), ("allowed", AnalysisGroupings));
        }

        if (periodEnd < periodStart || periodEnd.DayNumber - periodStart.DayNumber > 3660)
        {
            return Error.Validation("report.period_invalid", "The period ends on or after it starts and spans at most ten years.").WithWhy(("from", periodStart), ("to", periodEnd));
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var functional = company.FunctionalCurrency;
        var query = from l in db.OrderLines.AsNoTracking()
                    join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                    where o.CompanyId == companyId && OrderedStatuses.Contains(o.Status) && o.OrderDate >= periodStart && o.OrderDate <= periodEnd && l.Quantity - l.QtyCancelled > 0m
                    select new { l.OrderId, l.ItemId, l.Quantity, l.QtyCancelled, l.QtyReceived, l.QtyInvoiced, l.QuantityBase, l.UnitPrice, l.DiscountPct, o.PartnerId, o.OrderDate, o.ExchangeRate };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.PartnerId == p);
        }

        if (itemId is { } i)
        {
            query = query.Where(x => x.ItemId == i);
        }

        var rows = await query.ToListAsync(cancellationToken);
        decimal Value(decimal quantity, decimal unitPrice, decimal discountPct, decimal rate) => Shared.Round(quantity * unitPrice * (1m - (discountPct / 100m)) * rate, functional);
        var measured = rows.Select(r => new
        {
            r.OrderId,
            r.ItemId,
            r.PartnerId,
            Month = new DateOnly(r.OrderDate.Year, r.OrderDate.Month, 1),
            BaseQuantity = r.Quantity == 0m ? 0m : (r.Quantity - r.QtyCancelled) * r.QuantityBase / r.Quantity,
            Ordered = Value(r.Quantity - r.QtyCancelled, r.UnitPrice, r.DiscountPct, r.ExchangeRate),
            Received = Value(r.QtyReceived, r.UnitPrice, r.DiscountPct, r.ExchangeRate),
            Invoiced = Value(r.QtyInvoiced, r.UnitPrice, r.DiscountPct, r.ExchangeRate),
        }).ToList();

        var result = new List<PurchaseAnalysisRow>();
        switch (grouping)
        {
            case "supplier":
                foreach (var g in measured.GroupBy(static m => m.PartnerId))
                {
                    var partner = await partners.FindAsync(g.Key, cancellationToken);
                    result.Add(new PurchaseAnalysisRow(g.Key.ToString(), partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                        g.Select(static m => m.OrderId).Distinct().Count(), null, null, g.Sum(static m => m.Ordered), g.Sum(static m => m.Received), g.Sum(static m => m.Invoiced)));
                }

                result = [.. result.OrderByDescending(static r => r.Ordered).ThenBy(static r => r.Code, StringComparer.Ordinal)];
                break;
            case "item":
                foreach (var g in measured.GroupBy(static m => m.ItemId))
                {
                    var item = await items.FindAsync(g.Key, cancellationToken);
                    result.Add(new PurchaseAnalysisRow(g.Key.ToString(), item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                        g.Select(static m => m.OrderId).Distinct().Count(), g.Sum(static m => m.BaseQuantity), item?.BaseUomCode, g.Sum(static m => m.Ordered), g.Sum(static m => m.Received), g.Sum(static m => m.Invoiced)));
                }

                result = [.. result.OrderByDescending(static r => r.Ordered).ThenBy(static r => r.Code, StringComparer.Ordinal)];
                break;
            default:
                // Every month of the period, with nothing bought shown as zero, so the trend reads without gaps.
                var byMonth = measured.GroupBy(static m => m.Month).ToDictionary(static g => g.Key);
                for (var month = new DateOnly(periodStart.Year, periodStart.Month, 1); month <= periodEnd; month = month.AddMonths(1))
                {
                    var key = month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                    var g = byMonth.GetValueOrDefault(month);
                    result.Add(new PurchaseAnalysisRow(key, key, new Dictionary<string, string>(StringComparer.Ordinal), g?.Select(static m => m.OrderId).Distinct().Count() ?? 0, null, null,
                        g?.Sum(static m => m.Ordered) ?? 0m, g?.Sum(static m => m.Received) ?? 0m, g?.Sum(static m => m.Invoiced) ?? 0m));
                }

                break;
        }

        return new PurchaseAnalysis(companyId, functional.Code, grouping, periodStart, periodEnd, result, measured.Sum(static m => m.Ordered), measured.Sum(static m => m.Received), measured.Sum(static m => m.Invoiced), measured.Select(static m => m.OrderId).Distinct().Count());
    }
}

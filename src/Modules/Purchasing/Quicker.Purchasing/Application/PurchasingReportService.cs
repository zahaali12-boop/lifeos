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
}

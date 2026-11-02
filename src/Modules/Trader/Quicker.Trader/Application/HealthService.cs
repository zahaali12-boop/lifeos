using System.Globalization;
using System.Text.Json;
using Quicker.Accounting.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Reporting.Contracts;
using Quicker.Trader.Contracts;

namespace Quicker.Trader.Application;

/// <summary>
/// The health of the project (ADR-0035 §8, A-190): the income statement of a period in a trader's words, read from the
/// ledger by account type and subtype exactly as the shipped profit and loss reads it (so the two agree to the dinar),
/// the period before it and six months of results; whether the project carries itself (fixed costs, the margin, the
/// sales a month needs to break even, where this month leads at its pace, the cash and what it covers, the money
/// position); warnings in codes; the shipments on the way with their money; sales by item and by customer; and what
/// to reorder from the pace of the last cover days' sales against the stock and the goods on the way. One read over
/// the modules' contracts; nothing is kept.
/// </summary>
public sealed class HealthService(
    ICompanyDirectory companies,
    ICompanySettings settings,
    ILedgerReader ledger,
    IBankAccountDirectory banks,
    IReceivables receivables,
    IPayables payables,
    IPurchaseShipments shipments,
    ISalesLines salesLines,
    IInventoryCosting costing,
    IClock clock)
{
    private const int DefaultCoverDays = 90;
    private const int TrendMonths = 6;
    private const int AverageMonths = 3;
    private const int RunwayWarningMonths = 2;
    private const int QuietDays = 7;
    private const int TopRows = 10;
    private const decimal DefaultTargetMarginPct = 30m;

    private static readonly string[] CostOfGoodsSubtypes = ["cogs", "variance"];
    private static readonly string[] FinanceSubtypes = ["finance", "fx"];
    private static readonly string[] AttentionHints = ["arrival_overdue", "no_costs", "payment_missing", "stale"];

    public async Task<Result<TraderHealth>> ReadAsync(Guid companyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var fc = company.FunctionalCurrency;
        var today = clock.TodayIn(company.TimeZone);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end)
        {
            return Error.Validation("trader.period_invalid", "The period starts after it ends.").WithWhy(("from", start), ("to", end));
        }

        var config = await ConfigAsync(companyId, cancellationToken);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);

        // The ledger: the period, the one before it, the months of the trend and of the fixed-cost average, and this month so far.
        var income = Income(await ledger.MonthlyMovementAsync(companyId, start, end, cancellationToken), start, end, fc);
        var (previousStart, previousEnd) = Previous(start, end, today);
        var previous = Income(await ledger.MonthlyMovementAsync(companyId, previousStart, previousEnd, cancellationToken), previousStart, previousEnd, fc);
        var trendStart = new DateOnly(end.Year, end.Month, 1).AddMonths(-(TrendMonths - 1));
        var trendEnd = EndOfMonth(end);
        var averageStart = thisMonth.AddMonths(-AverageMonths);
        var windowStart = trendStart < averageStart ? trendStart : averageStart;
        var windowEnd = trendEnd > EndOfMonth(today) ? trendEnd : EndOfMonth(today);
        var monthly = await ledger.MonthlyMovementAsync(companyId, windowStart, windowEnd, cancellationToken);
        var months = new List<TraderMonthResult>();
        for (var month = trendStart; month <= trendEnd; month = month.AddMonths(1))
        {
            var result = Income(monthly.Where(r => r.Month == month).ToList(), month, EndOfMonth(month), fc);
            months.Add(new TraderMonthResult(month, result.NetSales, result.GrossProfit, result.Expenses, result.NetProfit));
        }

        var monthToDate = start == thisMonth && end == today ? income : Income(await ledger.MonthlyMovementAsync(companyId, thisMonth, today, cancellationToken), thisMonth, today, fc);

        // Fixed costs: the trader's own figure, else the months with expenses among the last three complete ones.
        var fixedCosts = config.FixedCostsMonthly;
        var fixedSource = "setting";
        if (fixedCosts is null)
        {
            var past = new List<decimal>();
            for (var month = averageStart; month < thisMonth; month = month.AddMonths(1))
            {
                var expenses = Income(monthly.Where(r => r.Month == month).ToList(), month, EndOfMonth(month), fc).Expenses;
                if (expenses > 0m)
                {
                    past.Add(expenses);
                }
            }

            fixedSource = past.Count > 0 ? "average" : "none";
            fixedCosts = past.Count > 0 ? Round(past.Average(), fc) : null;
        }

        // The margin that pays the fixed costs: the period's own when it sold anything, else the pricing target on the price.
        decimal? margin;
        string marginSource;
        if (income.NetSales > 0m)
        {
            margin = income.GrossProfit * 100m / income.NetSales;
            marginSource = "period";
        }
        else
        {
            margin = config.TargetMarginPct * 100m / (100m + config.TargetMarginPct);
            marginSource = "target";
        }

        var breakEven = fixedCosts is { } fixedAmount && margin > 0m ? Round(fixedAmount * 100m / margin.Value, fc) : (decimal?)null;
        var projected = Round(monthToDate.NetSales * DateTime.DaysInMonth(today.Year, today.Month) / today.Day, fc);
        var coverage = breakEven is > 0m ? RoundingPolicy.Default.Round(projected * 100m / breakEven.Value, 1) : (decimal?)null;

        // The money: the banks and boxes today, what customers owe, what suppliers are owed.
        var balances = await banks.BalancesAsync(companyId, today, cancellationToken);
        var cashAccounts = balances.Where(static b => b.IsActive).Select(static b => new TraderCashAccount(b.Id, b.Code, b.Name.Values, b.Kind, b.Currency, b.BalanceTc, b.BalanceFc)).ToList();
        var cash = Round(cashAccounts.Sum(static a => a.BalanceFc), fc);
        var owed = await receivables.OpenTotalsAsync(companyId, today, cancellationToken);
        var owing = await payables.OpenTotalsAsync(companyId, today, cancellationToken);
        var netPosition = Round(cash + owed.TotalFc - owing.TotalFc, fc);
        var runway = fixedCosts is > 0m ? RoundingPolicy.Default.Round(cash / fixedCosts.Value, 1) : (decimal?)null;
        var viability = new TraderViability(fixedCosts, fixedSource, margin is { } m ? RoundingPolicy.Default.Round(m, 1) : null, marginSource, breakEven, monthToDate.NetSales, projected, coverage,
            cash, cashAccounts, Round(owed.TotalFc, fc), Round(owed.OverdueFc, fc), Round(owing.TotalFc, fc), Round(owing.OverdueFc, fc), netPosition, runway);

        // The shipments on the way and their money: what was paid out (the goods and the costs) and what the goods still need.
        var underway = await shipments.UnderwayAsync(companyId, cancellationToken);
        var shipmentRows = underway.Select(static s => new TraderShipmentUnderway(s.ShipmentId, s.OrderNumber, s.PartnerId, s.PartnerCode, s.PartnerName, s.Status, s.OrderedOn, s.ExpectedArrival,
            s.Currency, s.GoodsTotal, s.GoodsTotalRc, s.PaidAmountRc, s.CostsTotalRc, s.LandedTotalRc, s.Hints)).ToList();
        var goodsOnTheWay = Round(underway.Sum(static s => s.GoodsTotalRc), fc);
        var paidOnTheWay = Round(underway.Sum(static s => (s.PaidAmountRc ?? 0m) + s.CostsTotalRc), fc);
        var unpaidOnTheWay = Round(underway.Sum(static s => Math.Max(0m, s.GoodsTotalRc - (s.PaidAmountRc ?? 0m))), fc);
        var attention = underway.Count(static s => s.Hints.Any(static h => AttentionHints.Contains(h, StringComparer.Ordinal)));

        // Sales by item and by customer in the period; the pace of the last cover days for what to reorder.
        var periodLines = await salesLines.PostedAsync(companyId, start, end, cancellationToken);
        if (periodLines.IsFailure)
        {
            return periodLines.Error!;
        }

        var recentStart = today.AddDays(-(config.CoverDays - 1));
        var recentLines = start == recentStart && end == today ? periodLines : await salesLines.PostedAsync(companyId, recentStart, today, cancellationToken);
        if (recentLines.IsFailure)
        {
            return recentLines.Error!;
        }

        var rows = periodLines.Value.Rows;
        var periodNet = rows.Sum(static r => r.NetAmountFc);
        var byItem = rows.Where(static r => r.ItemId is not null).GroupBy(static r => r.ItemId!.Value)
            .Select(g => new TraderSalesByItem(g.Key, g.First().ItemCode ?? string.Empty, g.First().ItemName ?? Empty, g.Sum(static r => r.Quantity), Round(g.Sum(static r => r.NetAmountFc), fc), Share(g.Sum(static r => r.NetAmountFc), periodNet), g.Select(static r => r.InvoiceId).Distinct().Count()))
            .OrderByDescending(static i => i.NetSales).ThenBy(static i => i.ItemCode, StringComparer.Ordinal).Take(TopRows).ToList();
        var byCustomer = rows.GroupBy(static r => r.CustomerId)
            .Select(g => new TraderSalesByCustomer(g.Key, g.First().CustomerCode, g.First().CustomerName, Round(g.Sum(static r => r.NetAmountFc), fc), Share(g.Sum(static r => r.NetAmountFc), periodNet), g.Select(static r => r.InvoiceId).Distinct().Count()))
            .OrderByDescending(static c => c.NetSales).ThenBy(static c => c.PartnerCode, StringComparer.Ordinal).Take(TopRows).ToList();

        var onTheWay = underway.SelectMany(static s => s.Lines).GroupBy(static l => l.ItemId).ToDictionary(static g => g.Key, static g => g.Sum(static l => l.Quantity));
        var reorder = new List<TraderReorderRow>();
        foreach (var group in recentLines.Value.Rows.Where(static r => r.ItemId is not null).GroupBy(static r => r.ItemId!.Value))
        {
            var sold = group.Sum(static r => r.Quantity);
            if (sold <= 0m)
            {
                continue;
            }

            var cost = await costing.CostAsync(companyId, group.Key, null, today, cancellationToken);
            var onHand = cost?.Quantity ?? 0m;
            var incoming = onTheWay.GetValueOrDefault(group.Key);
            var suggested = sold - onHand - incoming;
            if (suggested <= 0m)
            {
                continue;
            }

            var daysOfCover = RoundingPolicy.Default.Round(onHand * config.CoverDays / sold, 1);
            reorder.Add(new TraderReorderRow(group.Key, group.First().ItemCode ?? string.Empty, group.First().ItemName ?? Empty, onHand, sold, daysOfCover, incoming, RoundingPolicy.Default.Round(suggested, 3)));
        }

        reorder.Sort(static (a, b) => Nullable.Compare(a.DaysOfCover, b.DaysOfCover) != 0 ? Nullable.Compare(a.DaysOfCover, b.DaysOfCover) : string.CompareOrdinal(a.ItemCode, b.ItemCode));
        var lastSale = recentLines.Value.Rows.Count > 0 ? recentLines.Value.Rows.Max(static r => r.PostingDate) : (DateOnly?)null;

        // What to act on, the gravest first.
        var warnings = new List<TraderWarning>();
        if (income.NetProfit < 0m)
        {
            warnings.Add(new TraderWarning("loss", -income.NetProfit));
        }

        if (fixedSource == "none")
        {
            warnings.Add(new TraderWarning("fixed_costs_unknown", null));
        }

        if (runway is { } runwayMonths && runwayMonths < RunwayWarningMonths)
        {
            warnings.Add(new TraderWarning("cash_runway_short", runwayMonths));
        }

        if (coverage is { } pct && pct < 100m)
        {
            warnings.Add(new TraderWarning("break_even_not_covered", pct));
        }

        var targetOnPrice = config.TargetMarginPct * 100m / (100m + config.TargetMarginPct);
        if (income.NetSales > 0m && income.GrossMarginPct < RoundingPolicy.Default.Round(targetOnPrice, 1))
        {
            warnings.Add(new TraderWarning("margin_below_target", income.GrossMarginPct));
        }

        if (owed.OverdueFc > 0m)
        {
            warnings.Add(new TraderWarning("overdue_receivables", Round(owed.OverdueFc, fc)));
        }

        if (owing.OverdueFc > 0m)
        {
            warnings.Add(new TraderWarning("overdue_payables", Round(owing.OverdueFc, fc)));
        }

        if (attention > 0)
        {
            warnings.Add(new TraderWarning("shipments_attention", attention));
        }

        if (lastSale is null)
        {
            warnings.Add(new TraderWarning("no_recent_sales", null));
        }
        else if (today.DayNumber - lastSale.Value.DayNumber >= QuietDays)
        {
            warnings.Add(new TraderWarning("no_recent_sales", today.DayNumber - lastSale.Value.DayNumber));
        }

        if (reorder.Count > 0)
        {
            warnings.Add(new TraderWarning("reorder_needed", reorder.Count));
        }

        return new TraderHealth(companyId, fc.Code, today, income, previous, months, viability, warnings, shipmentRows, goodsOnTheWay, paidOnTheWay, unpaidOnTheWay, byItem, byCustomer, reorder, config.CoverDays, lastSale);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The statement from the ledger's movement, in the vocabulary of the shipped profit and loss: revenue by subtype, cost of sales and variances, finance and exchange, income tax, the rest by account.</summary>
    private static TraderIncome Income(IReadOnlyList<AccountMonthMovement> rows, DateOnly from, DateOnly to, Currency fc)
    {
        decimal sales = 0m, contra = 0m, cogs = 0m, other = 0m, finance = 0m, tax = 0m;
        var lines = new Dictionary<Guid, (string Code, IReadOnlyDictionary<string, string> Name, decimal Amount)>();
        foreach (var row in rows)
        {
            var debit = row.DebitFc - row.CreditFc;
            if (row.Type == AccountTypes.Revenue)
            {
                if (row.Subtype == "sales")
                {
                    sales -= debit;
                }
                else if (row.Subtype == "contra_revenue")
                {
                    contra += debit;
                }
                else
                {
                    other -= debit;
                }
            }
            else if (row.Type == AccountTypes.Expense)
            {
                if (CostOfGoodsSubtypes.Contains(row.Subtype, StringComparer.Ordinal))
                {
                    cogs += debit;
                }
                else if (FinanceSubtypes.Contains(row.Subtype, StringComparer.Ordinal))
                {
                    finance += debit;
                }
                else if (row.Subtype == "income_tax")
                {
                    tax += debit;
                }
                else
                {
                    var existing = lines.GetValueOrDefault(row.AccountId, (Code: row.Code, Name: row.Name, Amount: 0m));
                    lines[row.AccountId] = (existing.Code, existing.Name, existing.Amount + debit);
                }
            }
        }

        var expenses = lines.Values.Sum(static l => l.Amount);
        var netSales = sales - contra;
        var gross = netSales - cogs;
        var net = gross + other - expenses - finance - tax;
        var expenseLines = lines.Where(static l => l.Value.Amount != 0m).Select(l => new TraderIncomeLine(l.Key, l.Value.Code, l.Value.Name, Round(l.Value.Amount, fc)))
            .OrderByDescending(static l => l.Amount).ThenBy(static l => l.Code, StringComparer.Ordinal).ToList();
        return new TraderIncome(from, to, Round(sales, fc), Round(contra, fc), Round(netSales, fc), Round(cogs, fc), Round(gross, fc), Pct(gross, netSales), Round(other, fc),
            Round(expenses, fc), expenseLines, Round(finance, fc), Round(tax, fc), Round(net, fc), Pct(net, netSales));
    }

    /// <summary>The period before: the previous calendar month for a month (whole or so far), else the same number of days before the start.</summary>
    private static (DateOnly Start, DateOnly End) Previous(DateOnly start, DateOnly end, DateOnly today)
    {
        var sameMonth = end.Year == start.Year && end.Month == start.Month;
        if (start.Day == 1 && sameMonth && (end == EndOfMonth(start) || end == today))
        {
            var month = start.AddMonths(-1);
            return (month, EndOfMonth(month));
        }

        var days = end.DayNumber - start.DayNumber + 1;
        return (start.AddDays(-days), start.AddDays(-1));
    }

    private static DateOnly EndOfMonth(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    private static decimal Round(decimal value, Currency fc) => RoundingPolicy.Default.Round(value, fc.MinorUnits);

    private static decimal? Pct(decimal value, decimal basis) => basis > 0m ? RoundingPolicy.Default.Round(value * 100m / basis, 1) : null;

    private static decimal Share(decimal value, decimal total) => total != 0m ? RoundingPolicy.Default.Round(value * 100m / total, 1) : 0m;

    private async Task<(decimal? FixedCostsMonthly, int CoverDays, decimal TargetMarginPct)> ConfigAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var fixedCosts = Number(await settings.GetAsync(companyId, TraderSettingKeys.FixedCostsMonthly, cancellationToken));
        var cover = Number(await settings.GetAsync(companyId, TraderSettingKeys.CoverDays, cancellationToken));
        var margin = Number(await settings.GetAsync(companyId, TraderSettingKeys.TargetMarginPct, cancellationToken)) ?? DefaultTargetMarginPct;
        return (fixedCosts is > 0m ? fixedCosts : null, cover is > 0m ? (int)RoundingPolicy.Default.RoundToMultiple(cover.Value, 1m, RoundingDirection.Up) : DefaultCoverDays, margin < 0m ? 0m : margin);
    }

    private static decimal? Number(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDecimal(),
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}

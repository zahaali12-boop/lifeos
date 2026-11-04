namespace Quicker.Trader.Application;

/// <summary>One named expense of the period (rent, salaries, utilities): the account's name is the trader's word for it.</summary>
public sealed record TraderIncomeLine(Guid AccountId, string Code, IReadOnlyDictionary<string, string> Name, decimal Amount);

/// <summary>
/// The income statement of a period in a trader's words, tied to the ledger like the shipped profit and loss: what was
/// sold, what came back or was discounted, what the goods sold had cost, the profit on them, the other income, the
/// expenses by name, the bank and exchange costs, the income tax, and what is left. Costs and expenses are positive;
/// a profit is positive and a loss negative.
/// </summary>
public sealed record TraderIncome(
    DateOnly From,
    DateOnly To,
    decimal Sales,
    decimal ReturnsAndDiscounts,
    decimal NetSales,
    decimal CostOfGoods,
    decimal GrossProfit,
    decimal? GrossMarginPct,
    decimal OtherIncome,
    decimal Expenses,
    IReadOnlyList<TraderIncomeLine> ExpenseLines,
    decimal FinanceCosts,
    decimal IncomeTax,
    decimal NetProfit,
    decimal? NetMarginPct);

/// <summary>One month of results for the trend.</summary>
public sealed record TraderMonthResult(DateOnly Month, decimal NetSales, decimal GrossProfit, decimal Expenses, decimal NetProfit);

/// <summary>A bank account or cash box with its balance today, in its currency and the company's.</summary>
public sealed record TraderCashAccount(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string Currency, decimal BalanceTc, decimal BalanceFc);

/// <summary>
/// Whether the project carries itself: the fixed costs of a month (the trader's setting, else the average of the last
/// three months' expenses, else unknown), the margin that pays them (this period's gross margin, else the pricing
/// target), the sales a month needs to break even, this month's sales so far and where they lead at this pace, the
/// money in the banks and boxes, what is owed to and by the trader, and how many months the cash covers the fixed costs.
/// </summary>
public sealed record TraderViability(
    decimal? FixedCostsMonthly,
    string FixedCostsSource,
    decimal? MarginPct,
    string MarginSource,
    decimal? BreakEvenSalesMonthly,
    decimal MonthToDateSales,
    decimal ProjectedMonthSales,
    decimal? BreakEvenCoveragePct,
    decimal Cash,
    IReadOnlyList<TraderCashAccount> CashAccounts,
    decimal OwedToYou,
    decimal OwedToYouOverdue,
    decimal YouOwe,
    decimal YouOweOverdue,
    decimal NetPosition,
    decimal? CashRunwayMonths);

/// <summary>Something to act on, as a code the client words with the value where one helps: loss, fixed_costs_unknown, cash_negative (amount), cash_runway_short (months), break_even_not_covered (percent), margin_below_target (percent), overdue_receivables (amount), overdue_payables (amount), shipments_attention (count), no_recent_sales (days), reorder_needed (count).</summary>
public sealed record TraderWarning(string Code, decimal? Value);

/// <summary>A shipment on the way: its stage, dates, the goods in the order's currency and the company's, what was paid for them, the journey's costs so far and the hints of the shipment screen.</summary>
public sealed record TraderShipmentUnderway(
    Guid ShipmentId,
    string OrderNumber,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    string Status,
    DateOnly OrderedOn,
    DateOnly? ExpectedArrival,
    string Currency,
    decimal GoodsTotal,
    decimal GoodsTotalRc,
    decimal? PaidAmountRc,
    decimal CostsTotalRc,
    decimal LandedTotalRc,
    IReadOnlyList<string> Hints);

/// <summary>What one item brought in the period: the quantity sold, the net sales in the company's currency and their share of the period's sales.</summary>
public sealed record TraderSalesByItem(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal Quantity, decimal NetSales, decimal SharePct, int Invoices);

/// <summary>What one customer bought in the period, net, and the share of the period's sales.</summary>
public sealed record TraderSalesByCustomer(Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, decimal NetSales, decimal SharePct, int Invoices);

/// <summary>An item the stock and the shipments on the way will not carry through the cover days at the pace of the last cover days' sales, and how many to order.</summary>
public sealed record TraderReorderRow(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal OnHand, decimal SoldInCoverDays, decimal? DaysOfCover, decimal OnTheWay, decimal SuggestedQuantity);

/// <summary>The health of the project (ADR-0035 §8): one read over the modules' contracts, nothing kept.</summary>
public sealed record TraderHealth(
    Guid CompanyId,
    string Currency,
    DateOnly AsOf,
    TraderIncome Income,
    TraderIncome Previous,
    IReadOnlyList<TraderMonthResult> Months,
    TraderViability Viability,
    IReadOnlyList<TraderWarning> Warnings,
    IReadOnlyList<TraderShipmentUnderway> Shipments,
    decimal GoodsOnTheWayRc,
    decimal PaidOnTheWayRc,
    decimal UnpaidOnTheWayRc,
    IReadOnlyList<TraderSalesByItem> ByItem,
    IReadOnlyList<TraderSalesByCustomer> ByCustomer,
    IReadOnlyList<TraderReorderRow> Reorder,
    int CoverDays,
    DateOnly? LastSaleOn);

namespace Quicker.Trader.Application;

/// <summary>Today's spot rate from a purchase currency to the company's, or null when none is entered under Rates.</summary>
public sealed record TraderMarketRate(string Currency, decimal? Rate, DateOnly? AsOf);

/// <summary>
/// One item on the pricing board: what it cost landed (from its latest shipment), what it would cost to buy again at
/// today's rate, the cost the trader entered on the item when the program knows no other, the price it sells at now
/// and the margin that leaves, the suggested price at the target margin, the floor below which a unit sells at a loss
/// (the cost basis rounded up to the increment), and the alerts (no_price, no_cost, no_market_rate, loss_at_cost,
/// loss_at_replacement, margin_below_target, rate_up, rate_down).
/// </summary>
public sealed record TraderPricingRow(
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    decimal OnHand,
    string? PurchaseCurrency,
    decimal? UnitPriceFc,
    decimal? RatePaid,
    decimal? MarketRate,
    decimal? RateMovePct,
    decimal? LandedUnitCostRc,
    decimal? StockUnitCostRc,
    decimal? ItemCostRc,
    decimal? CostBasisRc,
    decimal? ReplacementUnitCostRc,
    decimal? CurrentPrice,
    decimal? CurrentMarginPct,
    decimal? SuggestedPrice,
    decimal? SuggestedMarginPct,
    decimal? FloorPrice,
    IReadOnlyList<string> Alerts,
    string? OrderNumber);

/// <summary>The board: the list priced, the settings in force, today's market rates and every item with a cost or a price. Hints: no_price_list.</summary>
public sealed record TraderPricingBoard(
    Guid CompanyId,
    string Currency,
    DateOnly AsOf,
    Guid? PriceListId,
    IReadOnlyDictionary<string, string>? PriceListName,
    decimal TargetMarginPct,
    decimal RoundTo,
    IReadOnlyList<TraderMarketRate> MarketRates,
    IReadOnlyList<TraderPricingRow> Rows,
    int LosingItems,
    int UnpricedItems,
    IReadOnlyList<string> Hints);

/// <summary>One currency the trader buys in: the spot rate to the company's currency in force today, the date it was entered from, and whether it was entered for today.</summary>
public sealed record TraderDailyRate(string Currency, decimal? Rate, DateOnly? AsOf, bool EnteredToday);

/// <summary>The day's rates (ADR-0035 §14): whether the prompt is due now (the hour reached and a currency without today's rate), the hour it is asked from (negative: never), and each currency's rate.</summary>
public sealed record TraderDailyRates(Guid CompanyId, string Currency, DateOnly Date, int PromptHour, bool Due, IReadOnlyList<TraderDailyRate> Rates);

public sealed record TraderRateEntry(string Currency, decimal Rate);

/// <summary>Today's spot rates entered in one go, 1 unit of each currency = the rate in the company's currency; a reason when a rate of today is corrected.</summary>
public sealed record TraderDailyRatesRequest(Guid CompanyId, IReadOnlyList<TraderRateEntry> Rates, string? Reason = null);

/// <summary>One movement of a cash box or bank account in the trader's words: what came in or went out, and the balance after it.</summary>
public sealed record TraderCashMovement(Guid Id, DateOnly Date, string Kind, decimal In, decimal Out, decimal BalanceAfter, string? Reference, string? SourceDocumentType, Guid? SourceDocumentId);

/// <summary>A cash box or bank account over the period: its balance now, what it held when the period began, what came in and went out, what it held at the period's end, and its movements newest first.</summary>
public sealed record TraderCashReportAccount(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Kind, string Currency, bool IsActive, decimal BalanceNow, decimal Opening, decimal In, decimal Out, decimal Closing, IReadOnlyList<TraderCashMovement> Movements);

/// <summary>The cash and bank screen (ADR-0035 §18): every box and account of the company over the period asked for (to today when no end is given).</summary>
public sealed record TraderCashReport(Guid CompanyId, string Currency, DateOnly AsOf, DateOnly? From, DateOnly To, IReadOnlyList<TraderCashReportAccount> Accounts);

public sealed record TraderPriceChange(Guid ItemId, decimal Price);

/// <summary>Writes prices into the sales price list (the board's list, or the one named) from today.</summary>
public sealed record TraderApplyPricesRequest(Guid CompanyId, IReadOnlyList<TraderPriceChange> Prices, Guid? PriceListId = null);

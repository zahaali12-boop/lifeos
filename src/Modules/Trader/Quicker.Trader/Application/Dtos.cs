namespace Quicker.Trader.Application;

/// <summary>Today's spot rate from a purchase currency to the company's, or null when none is entered under Rates.</summary>
public sealed record TraderMarketRate(string Currency, decimal? Rate, DateOnly? AsOf);

/// <summary>
/// One item on the pricing board: what it cost landed (from its latest shipment), what it would cost to buy again at
/// today's rate, the price it sells at now and the margin that leaves, the suggested price at the target margin, and
/// the alerts (no_price, no_cost, no_market_rate, loss_at_cost, loss_at_replacement, margin_below_target, rate_up, rate_down).
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
    decimal? CostBasisRc,
    decimal? ReplacementUnitCostRc,
    decimal? CurrentPrice,
    decimal? CurrentMarginPct,
    decimal? SuggestedPrice,
    decimal? SuggestedMarginPct,
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

public sealed record TraderPriceChange(Guid ItemId, decimal Price);

/// <summary>Writes prices into the sales price list (the board's list, or the one named) from today.</summary>
public sealed record TraderApplyPricesRequest(Guid CompanyId, IReadOnlyList<TraderPriceChange> Prices, Guid? PriceListId = null);

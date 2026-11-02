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

public sealed record TraderPriceChange(Guid ItemId, decimal Price);

/// <summary>Writes prices into the sales price list (the board's list, or the one named) from today.</summary>
public sealed record TraderApplyPricesRequest(Guid CompanyId, IReadOnlyList<TraderPriceChange> Prices, Guid? PriceListId = null);

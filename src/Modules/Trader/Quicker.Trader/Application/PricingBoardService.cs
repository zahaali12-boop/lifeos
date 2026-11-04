using System.Globalization;
using System.Text.Json;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Pricing.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Trader.Contracts;

namespace Quicker.Trader.Application;

/// <summary>
/// The trader's pricing board (ADR-0035 §5, A-188): every item with a landed cost from its shipments or a price in the
/// sales list, what one unit cost landed at the rate paid, what it would cost to buy again at today's spot rate (the
/// goods part re-valued, the shipping and duty part kept), the price it sells at now and the margin that leaves, the
/// price the target margin suggests rounded up to the increment, and the alerts. Applying writes the suggested prices
/// into the sales price list through the pricing module; the board itself keeps nothing.
/// </summary>
public sealed class PricingBoardService(
    ICompanyDirectory companies,
    ICompanySettings settings,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IInventoryCosting costing,
    IStockActivity stock,
    IPurchaseLandedCosts landedCosts,
    IPriceLists priceLists,
    IClock clock)
{
    private const decimal DefaultTargetMarginPct = 30m;

    public async Task<Result<TraderPricingBoard>> BoardAsync(Guid companyId, Guid? priceListId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var fc = company.FunctionalCurrency;
        var today = clock.TodayIn(company.TimeZone);
        var config = await ConfigAsync(companyId, cancellationToken);
        var list = await ListAsync(companyId, priceListId ?? config.PriceListId, fc.Code, cancellationToken);
        if (list.IsFailure)
        {
            return list.Error!;
        }

        var hints = new List<string>();
        if (list.Value is null)
        {
            hints.Add("no_price_list");
        }

        var prices = list.Value is null ? new Dictionary<Guid, decimal>() : (await priceLists.PricesAsync(list.Value.Id, today, cancellationToken)).ToDictionary(static p => p.ItemId, static p => p.Price);
        var landed = (await landedCosts.LatestByItemAsync(companyId, cancellationToken)).ToDictionary(static l => l.ItemId);

        // Today's rate for every currency the trader buys in (named in the settings or seen on shipments), once.
        var marketRates = new List<TraderMarketRate>();
        var rateByCurrency = new Dictionary<string, (decimal Rate, DateOnly AsOf)>(StringComparer.Ordinal);
        foreach (var currency in await TraderCurrencies.ForAsync(settings, companyId, fc.Code, landed.Values.Select(static l => l.Currency), cancellationToken))
        {
            var resolved = await rates.ResolveAsync(company.Id, currency, fc.Code, today, RateTypes.Spot, cancellationToken);
            if (resolved.IsSuccess)
            {
                rateByCurrency[currency] = (resolved.Value.Rate.Rate, resolved.Value.EffectiveFrom);
                marketRates.Add(new TraderMarketRate(currency, resolved.Value.Rate.Rate, resolved.Value.EffectiveFrom));
            }
            else
            {
                marketRates.Add(new TraderMarketRate(currency, null, null));
            }
        }

        // Every item a shipment landed, every item the list prices, and every item the store holds (goods entered
        // directly or there before the program, T13): a trader prices what is on the shelf, whatever brought it.
        var held = (await stock.OnHandByItemAsync(companyId, cancellationToken)).Select(static x => x.ItemId);
        var rows = new List<TraderPricingRow>();
        foreach (var itemId in landed.Keys.Union(prices.Keys).Union(held))
        {
            var item = await items.FindAsync(itemId, cancellationToken);
            if (item is null)
            {
                continue;
            }

            var cost = await costing.CostAsync(companyId, itemId, null, today, cancellationToken);
            var onHand = cost?.Quantity ?? 0m;
            var stockUnit = cost is { Quantity: > 0m } ? cost.AverageUnitCost : (decimal?)null;
            landed.TryGetValue(itemId, out var shipment);
            var foreign = shipment is not null && !string.Equals(shipment.Currency, fc.Code, StringComparison.Ordinal);
            decimal? marketRate = null;
            decimal? rateMove = null;
            decimal? replacement = null;
            if (shipment is not null)
            {
                if (!foreign)
                {
                    replacement = shipment.LandedUnitCostRc;
                }
                else if (rateByCurrency.TryGetValue(shipment.Currency, out var market))
                {
                    marketRate = market.Rate;
                    rateMove = shipment.RatePaid > 0m ? RoundingPolicy.Default.Round((market.Rate - shipment.RatePaid) * 100m / shipment.RatePaid, 1) : null;
                    replacement = RoundingPolicy.Default.Round(shipment.UnitPriceFc * market.Rate + shipment.CostsUnitRc, fc.MinorUnits);
                }
            }

            // The cost the trader typed on the item stands in until a shipment or the stock ledger knows better.
            var itemCost = (await items.CompanyPolicyAsync(itemId, companyId, cancellationToken))?.StandardCost;
            var costBasis = shipment?.LandedUnitCostRc ?? stockUnit ?? itemCost;
            var basisForPrice = replacement ?? costBasis;
            prices.TryGetValue(itemId, out var current);
            var hasPrice = prices.ContainsKey(itemId);
            var suggested = basisForPrice is { } basis ? Suggest(basis, config.TargetMarginPct, config.RoundTo, fc) : (decimal?)null;
            var floor = basisForPrice is { } fb ? Suggest(fb, 0m, config.RoundTo, fc) : (decimal?)null;
            var currentMargin = hasPrice && basisForPrice is { } b1 && b1 > 0m ? RoundingPolicy.Default.Round((current - b1) * 100m / b1, 1) : (decimal?)null;
            var suggestedMargin = suggested is { } sp && basisForPrice is { } b2 && b2 > 0m ? RoundingPolicy.Default.Round((sp - b2) * 100m / b2, 1) : (decimal?)null;

            var alerts = new List<string>();
            if (basisForPrice is null)
            {
                alerts.Add("no_cost");
            }

            if (!hasPrice && basisForPrice is not null)
            {
                alerts.Add("no_price");
            }

            if (foreign && marketRate is null)
            {
                alerts.Add("no_market_rate");
            }

            if (hasPrice && costBasis is { } cb && current < cb)
            {
                alerts.Add("loss_at_cost");
            }
            else if (hasPrice && replacement is { } rp && current < rp)
            {
                alerts.Add("loss_at_replacement");
            }
            else if (hasPrice && currentMargin is { } cm && cm < config.TargetMarginPct)
            {
                alerts.Add("margin_below_target");
            }

            if (rateMove is { } move && move != 0m)
            {
                alerts.Add(move > 0m ? "rate_up" : "rate_down");
            }

            rows.Add(new TraderPricingRow(itemId, item.Code, item.Name.Values, onHand, shipment?.Currency, shipment?.UnitPriceFc, shipment?.RatePaid, marketRate, rateMove,
                shipment?.LandedUnitCostRc, stockUnit, itemCost, costBasis, replacement, hasPrice ? current : null, currentMargin, suggested, suggestedMargin, floor, alerts, shipment?.OrderNumber));
        }

        var ordered = rows.OrderByDescending(Losing).ThenByDescending(static r => r.Alerts.Count).ThenBy(static r => r.ItemCode, StringComparer.Ordinal).ToList();
        return new TraderPricingBoard(companyId, fc.Code, today, list.Value?.Id, list.Value?.Name.Values, config.TargetMarginPct, config.RoundTo, marketRates, ordered,
            ordered.Count(Losing), ordered.Count(static r => r.Alerts.Contains("no_price", StringComparer.Ordinal)), hints);
    }

    /// <summary>Writes the prices into the sales price list from today, then returns the board as it now stands.</summary>
    public async Task<Result<TraderPricingBoard>> ApplyAsync(TraderApplyPricesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.Prices.Count == 0)
        {
            return Error.Validation("trader.prices_required", "Name at least one item and its price.");
        }

        if (request.Prices.Any(static p => p.Price < 0m))
        {
            return Error.Validation("trader.price_invalid", "A price is zero or more.");
        }

        var config = await ConfigAsync(request.CompanyId, cancellationToken);
        var list = await ListAsync(request.CompanyId, request.PriceListId ?? config.PriceListId, company.FunctionalCurrency.Code, cancellationToken);
        if (list.IsFailure)
        {
            return list.Error!;
        }

        if (list.Value is null)
        {
            return Error.Validation("trader.price_list_missing", "Create a sales price list in the company's currency first.").WithWhy(("currency", company.FunctionalCurrency.Code));
        }

        var today = clock.TodayIn(company.TimeZone);
        foreach (var change in request.Prices)
        {
            var set = await priceLists.SetPriceAsync(list.Value.Id, change.ItemId, RoundingPolicy.Default.Round(change.Price, company.FunctionalCurrency.MinorUnits), today, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error!.WithWhy(("itemId", change.ItemId));
            }
        }

        return await BoardAsync(request.CompanyId, list.Value.Id, cancellationToken);
    }

    /// <summary>Takes the item's price off the sales price list from today (the history stays), then returns the board as it now stands.</summary>
    public async Task<Result<TraderPricingBoard>> RemovePriceAsync(Guid companyId, Guid itemId, Guid? priceListId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var config = await ConfigAsync(companyId, cancellationToken);
        var list = await ListAsync(companyId, priceListId ?? config.PriceListId, company.FunctionalCurrency.Code, cancellationToken);
        if (list.IsFailure)
        {
            return list.Error!;
        }

        if (list.Value is null)
        {
            return Error.Validation("trader.price_list_missing", "There is no sales price list in the company's currency to take the price off.").WithWhy(("currency", company.FunctionalCurrency.Code));
        }

        var removed = await priceLists.RemovePriceAsync(list.Value.Id, itemId, clock.TodayIn(company.TimeZone), cancellationToken);
        if (removed.IsFailure)
        {
            return removed.Error!.WithWhy(("itemId", itemId));
        }

        return await BoardAsync(companyId, list.Value.Id, cancellationToken);
    }

    /// <summary>The list priced: the one named (it must be the company's and in its currency), else the setting's, else the default active list in the company's currency.</summary>
    private async Task<Result<PriceListInfo?>> ListAsync(Guid companyId, Guid? wanted, string currency, CancellationToken cancellationToken)
    {
        var lists = await priceLists.ListAsync(companyId, cancellationToken);
        if (wanted is { } id)
        {
            var named = lists.FirstOrDefault(l => l.Id == id);
            if (named is null)
            {
                return Error.Validation("trader.price_list_unknown", "The price list is not one of the company's.").WithWhy(("priceListId", id));
            }

            if (!string.Equals(named.Currency, currency, StringComparison.Ordinal))
            {
                return Error.Validation("trader.price_list_currency", "Pricing by landed cost works on a price list in the company's currency.").WithWhy(("priceList", named.Code), ("currency", named.Currency), ("expected", currency));
            }

            return Result<PriceListInfo?>.Success(named);
        }

        var candidates = lists.Where(l => l.IsActive && string.Equals(l.Currency, currency, StringComparison.Ordinal)).ToList();
        return Result<PriceListInfo?>.Success(candidates.FirstOrDefault(static l => l.IsDefault) ?? candidates.FirstOrDefault());
    }

    private static bool Losing(TraderPricingRow row) => row.Alerts.Contains("loss_at_cost", StringComparer.Ordinal) || row.Alerts.Contains("loss_at_replacement", StringComparer.Ordinal);

    private static decimal Suggest(decimal basis, decimal targetMarginPct, decimal roundTo, Currency currency)
    {
        var raw = basis * (1m + targetMarginPct / 100m);
        return roundTo > 0m ? RoundingPolicy.Default.RoundToMultiple(raw, roundTo, RoundingDirection.Up) : RoundingPolicy.Default.Round(raw, currency.MinorUnits);
    }

    private async Task<(decimal TargetMarginPct, decimal RoundTo, Guid? PriceListId)> ConfigAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var margin = Number(await settings.GetAsync(companyId, TraderSettingKeys.TargetMarginPct, cancellationToken)) ?? DefaultTargetMarginPct;
        var roundTo = Number(await settings.GetAsync(companyId, TraderSettingKeys.RoundTo, cancellationToken)) ?? 0m;
        var listSetting = await settings.GetAsync(companyId, TraderSettingKeys.PriceListId, cancellationToken);
        Guid? listId = listSetting is { ValueKind: JsonValueKind.String } s && Guid.TryParse(s.GetString(), out var parsed) ? parsed : null;
        return (margin < 0m ? 0m : margin, roundTo < 0m ? 0m : roundTo, listId);
    }

    private static decimal? Number(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDecimal(),
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}

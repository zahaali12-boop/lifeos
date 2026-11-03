using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Trader.Application;
using Quicker.Web;

namespace Quicker.Trader.Api;

/// <summary>The trader edition's own endpoints under /api/v1/trader (ADR-0035): the pricing board and applying its prices; the health of the project; the owner's money; the day's exchange rate.</summary>
public static class TraderEndpoints
{
    // The day's rate is an organization rate: entering one needs the rate permission; whether one is due, any signed-in person may ask.
    private const string RateManage = "organization.rate.manage";

    // The board reads prices and costs, and writes the sales price list: the pricing module's own permissions apply.
    private const string PriceRead = "pricing.price.read";
    private const string PriceListManage = "pricing.price_list.manage";

    // The health screen is the company's figures in plain words: what running a statement needs.
    private const string ReportRun = "reporting.report.run";

    // The owner's money moves as a manual journal: the journal permissions apply.
    private const string JournalRead = "accounting.journal.read";
    private const string JournalManage = "accounting.journal.manage";
    private const string JournalPost = "accounting.journal.post";

    public static RouteGroupBuilder MapTraderEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var trader = api.MapGroup("/trader").WithTags("Trader").RequireAuthorization();
        var pricing = trader.MapGroup("/pricing");
        pricing.MapGet("/board", async (Guid companyId, Guid? priceListId, PricingBoardService service, CancellationToken ct) => ApiProblems.Ok(await service.BoardAsync(companyId, priceListId, ct)))
            .RequirePermission(PriceRead)
            .WithSummary("Every item with a landed cost or a sales price: cost landed at the rate paid, cost to buy again at today's rate, current price and margin, suggested price at the target margin, alerts");
        pricing.MapPost("/apply", async (TraderApplyPricesRequest request, PricingBoardService service, CancellationToken ct) => ApiProblems.Ok(await service.ApplyAsync(request, ct)))
            .RequirePermission(PriceListManage)
            .WithSummary("Writes the given prices into the sales price list from today and returns the board");
        pricing.MapDelete("/prices/{itemId:guid}", async (Guid itemId, Guid companyId, Guid? priceListId, PricingBoardService service, CancellationToken ct) => ApiProblems.Ok(await service.RemovePriceAsync(companyId, itemId, priceListId, ct)))
            .RequirePermission(PriceListManage)
            .WithSummary("Takes the item's price off the sales price list from today (the board's list, or the one named; earlier prices stay in the history) and returns the board");
        trader.MapGet("/health", async (Guid companyId, DateOnly? from, DateOnly? to, HealthService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, from, to, ct)))
            .RequirePermission(ReportRun)
            .WithSummary("The health of the project for a period (this month so far by default): the income statement in plain words with the period before and six months of results, fixed costs, break-even and cash runway, the money position, warnings, the shipments on the way with their money, sales by item and by customer, what to reorder");
        var owner = trader.MapGroup("/owner-money");
        owner.MapGet("/", async (Guid companyId, OwnerMoneyService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, ct)))
            .RequirePermission(JournalRead)
            .WithSummary("The owner's money in the business: what was put in and taken out in all, the equity accounts used, and every movement newest first");
        owner.MapPost("/", async (TraderOwnerMovementRequest request, OwnerMoneyService service, CancellationToken ct) => ApiProblems.Ok(await service.RecordAsync(request, ct)))
            .RequirePermission(JournalManage).RequirePermission(JournalPost)
            .WithSummary("Records money the owner put in (kind in: the box against capital) or took out (kind out: drawings against the box) as a posted manual journal, and returns the owner's money");
        var rates = trader.MapGroup("/rates");
        rates.MapGet("/today", async (Guid companyId, DailyRatesService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, ct)))
            .WithSummary("Today's spot rate of every currency the trader buys in, with the date each was entered from, and whether the day's prompt is due (the hour chosen reached and a currency without today's rate)");
        rates.MapPost("/today", async (TraderDailyRatesRequest request, DailyRatesService service, CancellationToken ct) => ApiProblems.Ok(await service.EnterAsync(request, ct)))
            .RequirePermission(RateManage)
            .WithSummary("Enters today's spot rates in one go (1 unit of each currency = the rate in the company's currency) into the organization's rates, a reason being needed to correct one already entered today, and returns the day's rates");
        return trader;
    }
}

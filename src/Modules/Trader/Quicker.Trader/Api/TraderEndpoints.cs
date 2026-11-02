using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Trader.Application;
using Quicker.Web;

namespace Quicker.Trader.Api;

/// <summary>The trader edition's own endpoints under /api/v1/trader (ADR-0035): the pricing board and applying its prices.</summary>
public static class TraderEndpoints
{
    // The board reads prices and costs, and writes the sales price list: the pricing module's own permissions apply.
    private const string PriceRead = "pricing.price.read";
    private const string PriceListManage = "pricing.price_list.manage";

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
        return trader;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Pricing.Application;
using Quicker.Pricing.Contracts;
using Quicker.Web;

namespace Quicker.Pricing.Api;

/// <summary>Pricing under /api/v1/pricing: price lists and their prices, customer agreements, discount rules, promotions, floors, and pricing a basket with the explanation of every price.</summary>
public static class PricingEndpoints
{
    public static RouteGroupBuilder MapPricingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var pricing = api.MapGroup("/pricing").WithTags("Pricing").RequireAuthorization();

        pricing.MapPost("/calculate", async (PricingRequest request, IPricing engine, CancellationToken ct) => ApiProblems.Ok(await engine.PriceAsync(request, ct)))
            .RequirePermission(PricingPermissions.Read)
            .WithSummary("Prices a basket for a customer on a date: every line's price with its explanation (the source that won and the ones considered, currency, discounts, promotions, document discounts, floor), deterministic for the same inputs and rules");

        // ------------------------------------------------------------------ price lists
        pricing.MapGet("/price-lists", async (Guid? companyId, PriceListService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapGet("/price-lists/{listId:guid}", async (Guid listId, PriceListService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(listId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/price-lists", async (SavePriceListRequest request, PriceListService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static l => $"/api/v1/pricing/price-lists/{l.Id}"))
            .RequirePermission(PricingPermissions.PriceListManage)
            .WithSummary("A price list: currency, tax basis, validity, priority, the customers and groups it is for; its own prices or its parent's adjusted by a percentage and a rounding rule");
        pricing.MapPut("/price-lists/{listId:guid}", async (Guid listId, SavePriceListRequest request, PriceListService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(listId, request, ct)))
            .RequirePermission(PricingPermissions.PriceListManage);
        pricing.MapDelete("/price-lists/{listId:guid}", async (Guid listId, PriceListService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(listId, ct)))
            .RequirePermission(PricingPermissions.PriceListManage);
        pricing.MapPost("/price-lists/{listId:guid}/adjust", async (Guid listId, AdjustPriceListRequest request, PriceListService service, CancellationToken ct) => ApiProblems.Ok(await service.AdjustAsync(listId, request, ct)))
            .RequirePermission(PricingPermissions.PriceListManage)
            .WithSummary("Changes every price of the list by a percentage (a yearly increase), rounded to an increment or to the currency's price decimals");

        pricing.MapGet("/price-lists/{listId:guid}/items", async (Guid listId, string? q, PriceListService service, CancellationToken ct) => ApiProblems.Ok(await service.ListItemsAsync(listId, q, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/price-lists/{listId:guid}/items", async (Guid listId, SavePriceListItemRequest request, PriceListService service, CancellationToken ct) => ApiProblems.Created(await service.SaveItemAsync(listId, null, request, ct), e => $"/api/v1/pricing/price-lists/{listId}/items/{e.Id}"))
            .RequirePermission(PricingPermissions.PriceListManage)
            .WithSummary("A price of an item (or one variant) per unit in the list, from a quantity (the break) and a date");
        pricing.MapPut("/price-lists/{listId:guid}/items/{entryId:guid}", async (Guid listId, Guid entryId, SavePriceListItemRequest request, PriceListService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveItemAsync(listId, entryId, request, ct)))
            .RequirePermission(PricingPermissions.PriceListManage);
        pricing.MapDelete("/price-lists/{listId:guid}/items/{entryId:guid}", async (Guid listId, Guid entryId, PriceListService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteItemAsync(listId, entryId, ct)))
            .RequirePermission(PricingPermissions.PriceListManage);

        // ------------------------------------------------------------------ agreements
        pricing.MapGet("/agreements", async (Guid? companyId, Guid? partnerId, PricingRulesService service, CancellationToken ct) => TypedResults.Ok(await service.ListAgreementsAsync(companyId, partnerId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/agreements", async (SavePriceAgreementRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAgreementAsync(null, request, ct), static a => $"/api/v1/pricing/agreements/{a.Id}"))
            .RequirePermission(PricingPermissions.AgreementManage)
            .WithSummary("A price agreed with a customer for an item (per unit, from a quantity) or a discount on an item or a category, for a period");
        pricing.MapPut("/agreements/{agreementId:guid}", async (Guid agreementId, SavePriceAgreementRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAgreementAsync(agreementId, request, ct)))
            .RequirePermission(PricingPermissions.AgreementManage);
        pricing.MapDelete("/agreements/{agreementId:guid}", async (Guid agreementId, PricingRulesService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAgreementAsync(agreementId, ct)))
            .RequirePermission(PricingPermissions.AgreementManage);

        // ------------------------------------------------------------------ discount rules
        pricing.MapGet("/discount-rules", async (Guid? companyId, PricingRulesService service, CancellationToken ct) => TypedResults.Ok(await service.ListRulesAsync(companyId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/discount-rules", async (SaveDiscountRuleRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Created(await service.SaveRuleAsync(null, request, ct), static r => $"/api/v1/pricing/discount-rules/{r.Id}"))
            .RequirePermission(PricingPermissions.PromotionManage)
            .WithSummary("A line or document discount: its scope (item, category, brand, customer, group, channel, terms), conditions (minimum quantity or amount, weekdays), value and whether it competes (exclusive) or stacks");
        pricing.MapPut("/discount-rules/{ruleId:guid}", async (Guid ruleId, SaveDiscountRuleRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRuleAsync(ruleId, request, ct)))
            .RequirePermission(PricingPermissions.PromotionManage);
        pricing.MapDelete("/discount-rules/{ruleId:guid}", async (Guid ruleId, PricingRulesService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteRuleAsync(ruleId, ct)))
            .RequirePermission(PricingPermissions.PromotionManage);

        // ------------------------------------------------------------------ promotions
        pricing.MapGet("/promotions", async (Guid? companyId, PricingRulesService service, CancellationToken ct) => TypedResults.Ok(await service.ListPromotionsAsync(companyId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/promotions", async (SavePromotionRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Created(await service.SavePromotionAsync(null, request, ct), static p => $"/api/v1/pricing/promotions/{p.Id}"))
            .RequirePermission(PricingPermissions.PromotionManage)
            .WithSummary("A promotion: buy X get Y, a bundle price, volume tiers or a coupon; optionally for a customer, group or channel, behind a coupon code, with usage limits");
        pricing.MapPut("/promotions/{promotionId:guid}", async (Guid promotionId, SavePromotionRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Ok(await service.SavePromotionAsync(promotionId, request, ct)))
            .RequirePermission(PricingPermissions.PromotionManage);
        pricing.MapDelete("/promotions/{promotionId:guid}", async (Guid promotionId, PricingRulesService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeletePromotionAsync(promotionId, ct)))
            .RequirePermission(PricingPermissions.PromotionManage);

        // ------------------------------------------------------------------ floors
        pricing.MapGet("/floors", async (Guid? companyId, PricingRulesService service, CancellationToken ct) => TypedResults.Ok(await service.ListFloorsAsync(companyId, ct)))
            .RequirePermission(PricingPermissions.Read);
        pricing.MapPost("/floors", async (SavePriceFloorRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Created(await service.SaveFloorAsync(null, request, ct), static f => $"/api/v1/pricing/floors/{f.Id}"))
            .RequirePermission(PricingPermissions.FloorManage)
            .WithSummary("The lowest price, or the thinnest margin over expected cost, an item or a category may sell at; a breach blocks or warns");
        pricing.MapPut("/floors/{floorId:guid}", async (Guid floorId, SavePriceFloorRequest request, PricingRulesService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveFloorAsync(floorId, request, ct)))
            .RequirePermission(PricingPermissions.FloorManage);
        pricing.MapDelete("/floors/{floorId:guid}", async (Guid floorId, PricingRulesService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteFloorAsync(floorId, ct)))
            .RequirePermission(PricingPermissions.FloorManage);

        return api;
    }
}

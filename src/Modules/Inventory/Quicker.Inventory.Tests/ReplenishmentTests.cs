using System.Net;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>Replenishment (roadmap 3.7): reorder points, min/max, safety stock and lead times become purchase suggestions that explain their arithmetic.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReplenishmentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task The_planner_suggests_what_to_buy_explains_it_counts_transit_as_incoming_refreshes_and_closes_suggestions_and_runs_for_every_tenant()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        var branch = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra", "البصرة") })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "TRANSIT", name = Name("Transit", "الطريق"), kind = "in_transit" });
        var water = (await owner.PostAsync("/api/v1/items", new { code = "WATER", name = Name("Water", "ماء"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = Guid.CreateVersion7();
        await owner.PostAsync($"/api/v1/items/{water}/suppliers", new { partnerId = supplier, leadTimeDays = 7, isPreferred = true });
        await owner.PutAsync($"/api/v1/items/{water}/warehouse-settings/{branch}", new { reorderPoint = 50, minQty = 40, maxQty = 200, safetyStock = 10 });
        await owner.PutAsync($"/api/v1/items/{tea}/warehouse-settings/{branch}", new { reorderPoint = 20, safetyStock = 5, leadTimeDays = 3 });
        Task<StockPostingResult> Post(Guid item, string type, decimal quantity, Guid warehouse, decimal? cost = null) =>
            host.PostStockAsync(ws.TenantId, new StockPostingRequest(companyId, new DateOnly(2026, 9, 1), "test_document", Guid.CreateVersion7(), [new StockLine(item, type, quantity, warehouse, UnitCost: cost)]));
        await Post(water, StockEntryTypes.PurchaseReceipt, 300m, main, 1m);
        await Post(water, StockEntryTypes.PurchaseReceipt, 30m, branch, 1m);
        await Post(tea, StockEntryTypes.PurchaseReceipt, 100m, branch, 2m);
        await owner.PostAsync("/api/v1/inventory/reservations", new { companyId, itemId = water, quantity = 10, warehouseId = branch, sourceDocumentType = "sales_order", sourceDocumentId = Guid.CreateVersion7() });

        // Water in Basra: 30 on hand − 10 reserved = 20 ≤ reorder point 50 → buy up to the max: 180, needed in 7 days from the preferred supplier. Tea is fine.
        var run = await owner.PostAsync("/api/v1/inventory/replenishment/run", new { companyId, asOf = "2026-09-10" }, HttpStatusCode.OK);
        run.GetProperty("itemsChecked").GetInt32().ShouldBe(2);
        run.GetProperty("suggestionsCreated").GetInt32().ShouldBe(1);
        var suggestions = await owner.GetOkAsync($"/api/v1/inventory/replenishment/suggestions?companyId={companyId}");
        suggestions.GetArrayLength().ShouldBe(1);
        var suggestion = suggestions[0];
        suggestion.GetProperty("itemCode").GetString().ShouldBe("WATER");
        suggestion.GetProperty("warehouseCode").GetString().ShouldBe("BASRA");
        suggestion.GetProperty("suggestedQty").GetDecimal().ShouldBe(180m);
        suggestion.GetProperty("suggestedSupplierId").GetGuid().ShouldBe(supplier);
        suggestion.GetProperty("neededBy").GetString().ShouldBe("2026-09-17");
        var why = suggestion.GetProperty("explanation");
        why.GetProperty("onHand").GetDecimal().ShouldBe(30m);
        why.GetProperty("reserved").GetDecimal().ShouldBe(10m);
        why.GetProperty("available").GetDecimal().ShouldBe(20m);
        why.GetProperty("projected").GetDecimal().ShouldBe(20m);
        why.GetProperty("reorderPoint").GetDecimal().ShouldBe(50m);
        why.GetProperty("maxQty").GetDecimal().ShouldBe(200m);
        why.GetProperty("trigger").GetString().ShouldBe("reorder_point");
        why.GetProperty("formula").GetString().ShouldBe("max − (available + in transit + on order)");
        why.GetProperty("leadTimeDays").GetInt32().ShouldBe(7);

        // 20 water shipped from MAIN towards Basra count as incoming: the next run refreshes the suggestion to 160 instead of adding a second one.
        var transfer = await owner.PostAsync("/api/v1/inventory/transfers", new { companyId, fromWarehouseId = main, toWarehouseId = branch, lines = new object[] { new { itemId = water, quantity = 20 } } });
        await owner.PostAsync($"/api/v1/inventory/transfers/{transfer.GetProperty("id").GetGuid()}/ship", new { shipDate = "2026-09-10" }, HttpStatusCode.OK);
        var second = await owner.PostAsync("/api/v1/inventory/replenishment/run", new { companyId, warehouseId = branch, asOf = "2026-09-11" }, HttpStatusCode.OK);
        second.GetProperty("suggestionsCreated").GetInt32().ShouldBe(0);
        second.GetProperty("suggestionsRefreshed").GetInt32().ShouldBe(1);
        var refreshed = (await owner.GetOkAsync($"/api/v1/inventory/replenishment/suggestions?companyId={companyId}"))[0];
        refreshed.GetProperty("id").GetGuid().ShouldBe(suggestion.GetProperty("id").GetGuid());
        refreshed.GetProperty("suggestedQty").GetDecimal().ShouldBe(160m);
        refreshed.GetProperty("explanation").GetProperty("inTransit").GetDecimal().ShouldBe(20m);
        refreshed.GetProperty("explanation").GetProperty("projected").GetDecimal().ShouldBe(40m);

        // Tea drops below its reorder point without a max: buy back to reorder point + safety stock; a dismissal needs a reason; an acceptance records the decision.
        await Post(tea, StockEntryTypes.SaleShipment, 85m, branch);
        var third = await owner.PostAsync("/api/v1/inventory/replenishment/run", new { companyId, asOf = "2026-09-12" }, HttpStatusCode.OK);
        third.GetProperty("suggestionsCreated").GetInt32().ShouldBe(1);
        var teaSuggestion = (await owner.GetOkAsync($"/api/v1/inventory/replenishment/suggestions?companyId={companyId}&itemId={tea}"))[0];
        teaSuggestion.GetProperty("suggestedQty").GetDecimal().ShouldBe(10m, "15 on hand, reorder point 20 + safety 5 = 25");
        teaSuggestion.GetProperty("neededBy").GetString().ShouldBe("2026-09-15");
        teaSuggestion.GetProperty("explanation").GetProperty("formula").GetString().ShouldBe("reorder point + safety stock − (available + in transit + on order)");
        (await owner.PostErrorAsync($"/api/v1/inventory/replenishment/suggestions/{teaSuggestion.GetProperty("id").GetGuid()}/dismiss", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("replenishment.reason_required");
        var accepted = await owner.PostAsync($"/api/v1/inventory/replenishment/suggestions/{refreshed.GetProperty("id").GetGuid()}/accept", new { quantity = 100, note = "Round up to a pallet" }, HttpStatusCode.OK);
        accepted.GetProperty("status").GetString().ShouldBe("accepted");
        accepted.GetProperty("acceptedQty").GetDecimal().ShouldBe(100m);
        accepted.GetProperty("acceptedSupplierId").GetGuid().ShouldBe(supplier);
        (await owner.PostErrorAsync($"/api/v1/inventory/replenishment/suggestions/{refreshed.GetProperty("id").GetGuid()}/accept", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("replenishment.not_open");
        var dismissed = await owner.PostAsync($"/api/v1/inventory/replenishment/suggestions/{teaSuggestion.GetProperty("id").GetGuid()}/dismiss", new { reason = "Season over" }, HttpStatusCode.OK);
        dismissed.GetProperty("status").GetString().ShouldBe("dismissed");
        dismissed.GetProperty("decisionNote").GetString().ShouldBe("Season over");

        // The need for water disappears after the transfer arrives and a big receipt: the next run closes a fresh open suggestion as superseded.
        await owner.PostAsync($"/api/v1/inventory/transfers/{transfer.GetProperty("id").GetGuid()}/receive", new { receiveDate = "2026-09-12" }, HttpStatusCode.OK);
        var fourth = await owner.PostAsync("/api/v1/inventory/replenishment/run", new { companyId, asOf = "2026-09-13" }, HttpStatusCode.OK);
        fourth.GetProperty("suggestionsCreated").GetInt32().ShouldBe(2, "water (50 on hand, 10 reserved: still at or below the reorder point once the accepted suggestion is closed) and tea again");
        await Post(water, StockEntryTypes.PurchaseReceipt, 200m, branch, 1m);
        var fifth = await owner.PostAsync("/api/v1/inventory/replenishment/run", new { companyId, warehouseId = branch, asOf = "2026-09-14" }, HttpStatusCode.OK);
        fifth.GetProperty("suggestionsClosed").GetInt32().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/inventory/replenishment/suggestions?companyId={companyId}&status=superseded")).GetArrayLength().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/inventory/replenishment/suggestions?companyId={companyId}&status=all")).GetArrayLength().ShouldBe(4);
        (await owner.GetOkAsync($"/api/v1/inventory/replenishment/runs?companyId={companyId}")).GetArrayLength().ShouldBe(5);

        // The platform job plans every tenant.
        await host.EnqueueAsync(ReplenishmentJob.JobType, new { asOf = "2026-09-15" });
        await host.RunWorkerAsync();
        (await owner.GetOkAsync($"/api/v1/inventory/replenishment/runs?companyId={companyId}")).GetArrayLength().ShouldBe(6);
        await owner.AssertInvariantsAsync();
    }
}

using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>
/// A count finished in one step (trader edition, ADR-0035 §9): the shop's count is frozen, part of it counted, and
/// "finish" takes the rest as unchanged, gives every difference the shipped COUNT reason (made on first use), reviews,
/// approves and posts; the stock follows. A finish that must not guess refuses the uncounted lines; a reason that does
/// not apply to counts is refused; a posted count is not finished twice.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CountFinishTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task A_shop_count_is_finished_in_one_step_and_the_uncounted_items_stay_as_they_are()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "SHP", legalName = Name("Parts Shop", "محل القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var shop = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "SHOP", name = Name("The shop", "المحل") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var filter = (await owner.PostAsync("/api/v1/items", new { code = "FILTER", name = Name("Oil filter", "فلتر زيت"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        await host.PostStockAsync(ws.TenantId, new StockPostingRequest(companyId, new DateOnly(2026, 9, 1), "test_document", Guid.CreateVersion7(), [new StockLine(pads, StockEntryTypes.PurchaseReceipt, 100m, shop, UnitCost: 15000m)]));
        await host.PostStockAsync(ws.TenantId, new StockPostingRequest(companyId, new DateOnly(2026, 9, 1), "test_document", Guid.CreateVersion7(), [new StockLine(filter, StockEntryTypes.PurchaseReceipt, 40m, shop, UnitCost: 4000m)]));

        // The count of the shop is frozen: two lines. Only the pads are counted, two short.
        var countId = (await owner.PostAsync("/api/v1/inventory/counts", new { companyId, warehouseId = shop, scope = "full", postingDate = "2026-09-20" })).GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/inventory/counts/{countId}/freeze", new { }, HttpStatusCode.OK)).GetProperty("lineCount").GetInt32().ShouldBe(2);
        var sheet = await owner.GetOkAsync($"/api/v1/inventory/counts/{countId}/sheet");
        var padsLine = sheet.GetProperty("lines").EnumerateArray().Single(static l => l.GetProperty("itemCode").GetString() == "PADS").GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/counts/{countId}/entries", new { entries = new[] { new { lineId = padsLine, countedQty = 98 } } }, HttpStatusCode.OK);

        // Finishing without guessing refuses the uncounted filters; taking them as unchanged posts the two missing pads under the COUNT reason.
        (await owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/finish", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("count.lines_uncounted");
        var finished = await owner.PostAsync($"/api/v1/inventory/counts/{countId}/finish", new { uncountedAsUnchanged = true }, HttpStatusCode.OK);
        finished.GetProperty("status").GetString().ShouldBe("posted");
        finished.GetProperty("countedLines").GetInt32().ShouldBe(2);
        finished.GetProperty("varianceLines").GetInt32().ShouldBe(1);
        finished.GetProperty("varianceValue").GetDecimal().ShouldBe(-30000m);
        finished.GetProperty("stockPostingId").GetGuid().ShouldNotBe(Guid.Empty);
        var reasons = await owner.GetOkAsync("/api/v1/inventory/reason-codes?appliesTo=count");
        var countReason = reasons.EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "COUNT");
        countReason.GetProperty("name").GetProperty("ar").GetString().ShouldBe("فرق الجرد");
        var lines = (await owner.GetOkAsync($"/api/v1/inventory/counts/{countId}/sheet")).GetProperty("lines").EnumerateArray().ToDictionary(static l => l.GetProperty("itemCode").GetString()!, static l => l);
        lines["PADS"].GetProperty("varianceQty").GetDecimal().ShouldBe(-2m);
        lines["PADS"].GetProperty("reasonCode").GetString().ShouldBe("COUNT");
        lines["FILTER"].GetProperty("countedQty").GetDecimal().ShouldBe(40m, "taken as unchanged");
        lines["FILTER"].GetProperty("varianceQty").GetDecimal().ShouldBe(0m);
        var stock = (await owner.GetOkAsync($"/api/v1/inventory/stock?companyId={companyId}")).GetProperty("items").EnumerateArray().ToDictionary(static s => s.GetProperty("itemCode").GetString()!, static s => s.GetProperty("onHand").GetDecimal());
        stock["PADS"].ShouldBe(98m);
        stock["FILTER"].ShouldBe(40m);
        (await owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/finish", new { uncountedAsUnchanged = true }, HttpStatusCode.Conflict)).Code.ShouldBe("count.not_counting");

        // A second count, everything counted and nothing different: a reason that does not apply to counts is refused; the finish posts nothing.
        var again = (await owner.PostAsync("/api/v1/inventory/counts", new { companyId, warehouseId = shop, scope = "full", postingDate = "2026-09-21" })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/counts/{again}/freeze", new { }, HttpStatusCode.OK);
        var entries = (await owner.GetOkAsync($"/api/v1/inventory/counts/{again}/sheet")).GetProperty("lines").EnumerateArray().Select(static l => new { lineId = l.GetProperty("id").GetGuid(), countedQty = l.GetProperty("expectedQty").GetDecimal() }).ToArray();
        await owner.PostAsync($"/api/v1/inventory/counts/{again}/entries", new { entries }, HttpStatusCode.OK);
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "DAMAGE", name = Name("Damaged", "تالف"), appliesTo = "scrap" });
        (await owner.PostErrorAsync($"/api/v1/inventory/counts/{again}/finish", new { reasonCode = "DAMAGE" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("count.reason_not_applicable");
        var clean = await owner.PostAsync($"/api/v1/inventory/counts/{again}/finish", new { }, HttpStatusCode.OK);
        clean.GetProperty("status").GetString().ShouldBe("posted");
        clean.GetProperty("varianceLines").GetInt32().ShouldBe(0);
        clean.GetProperty("stockPostingId").ValueKind.ShouldBe(JsonValueKind.Null);
        await owner.AssertInvariantsAsync();
    }
}

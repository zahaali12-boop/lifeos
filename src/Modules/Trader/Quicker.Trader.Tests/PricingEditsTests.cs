using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The pricing board edited by the trader (ADR-0035 §15): an item never shipped priced from the cost typed on it, the
/// floor below which a unit sells at a loss, the margin from the entered cost, and a price taken off the list from
/// today with earlier prices kept.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PricingEditsTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task An_item_is_priced_from_the_cost_typed_on_it_with_a_floor_and_its_price_is_taken_off_again()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRE", legalName = Name("Belt Trading", "تجارة السيور"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        var belt = (await owner.PostAsync("/api/v1/items", new { code = "BELT", name = Name("Fan belt", "سير مروحة"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var listId = (await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId, code = "RETAIL", name = Name("Retail", "التجزئة"), currency = "IQD", isDefault = true })).GetProperty("id").GetGuid();

        // Nothing shipped and nothing priced: the board is empty, even once a cost is typed on the item.
        (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("rows").GetArrayLength().ShouldBe(0);
        await owner.PutAsync($"/api/v1/items/{belt}/company-settings/{companyId}", new { standardCost = 9000m });
        (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("rows").GetArrayLength().ShouldBe(0);

        // Priced at 12,000: the typed cost is the basis, the margin 33.3 %, the floor the cost itself, the suggestion 30 % over it.
        var board = await owner.PostAsync("/api/v1/trader/pricing/apply", new { companyId, prices = new[] { new { itemId = belt, price = 12000m } } }, HttpStatusCode.OK);
        var row = board.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("itemCode").GetString().ShouldBe("BELT");
        row.GetProperty("landedUnitCostRc").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        row.GetProperty("stockUnitCostRc").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        row.GetProperty("itemCostRc").GetDecimal().ShouldBe(9000m);
        row.GetProperty("costBasisRc").GetDecimal().ShouldBe(9000m);
        row.GetProperty("currentPrice").GetDecimal().ShouldBe(12000m);
        row.GetProperty("currentMarginPct").GetDecimal().ShouldBe(33.3m);
        row.GetProperty("floorPrice").GetDecimal().ShouldBe(9000m);
        row.GetProperty("suggestedPrice").GetDecimal().ShouldBe(11700m);
        row.GetProperty("alerts").GetArrayLength().ShouldBe(0);

        // Prices rounded up to 250 and the cost corrected to 9,100: the floor is 9,250, the suggestion 12,000.
        await owner.PutAsync("/api/v1/organization/settings/trader.pricing.round_to", new { value = 250, valueType = "number" });
        await owner.PutAsync($"/api/v1/items/{belt}/company-settings/{companyId}", new { standardCost = 9100m });
        row = (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("itemCostRc").GetDecimal().ShouldBe(9100m);
        row.GetProperty("floorPrice").GetDecimal().ShouldBe(9250m);
        row.GetProperty("suggestedPrice").GetDecimal().ShouldBe(12000m);
        row.GetProperty("currentMarginPct").GetDecimal().ShouldBe(31.9m);

        // Sold at 9,000 it loses money against the typed cost.
        board = await owner.PostAsync("/api/v1/trader/pricing/apply", new { companyId, prices = new[] { new { itemId = belt, price = 9000m } } }, HttpStatusCode.OK);
        row = board.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("alerts").EnumerateArray().Select(static a => a.GetString()).ShouldBe(["loss_at_cost"]);
        board.GetProperty("losingItems").GetInt32().ShouldBe(1);

        // Taken off the list: the entry that began today is deleted and the item leaves the board; taking it off again is fine.
        var response = await owner.DeleteAsync($"/api/v1/trader/pricing/prices/{belt}?companyId={companyId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("rows").GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/pricing/price-lists/{listId}/items")).GetArrayLength().ShouldBe(0);
        (await owner.DeleteAsync($"/api/v1/trader/pricing/prices/{belt}?companyId={companyId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A price that began earlier is ended the day before today instead, so the history stays.
        await owner.PostAsync($"/api/v1/pricing/price-lists/{listId}/items", new { itemId = belt, minQuantity = 1m, price = 10000m, validFrom = "2026-09-01" });
        (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("rows").EnumerateArray().Single().GetProperty("currentPrice").GetDecimal().ShouldBe(10000m);
        (await owner.DeleteAsync($"/api/v1/trader/pricing/prices/{belt}?companyId={companyId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("rows").GetArrayLength().ShouldBe(0);
        var kept = (await owner.GetOkAsync($"/api/v1/pricing/price-lists/{listId}/items")).EnumerateArray().Single();
        kept.GetProperty("price").GetDecimal().ShouldBe(10000m);
        kept.GetProperty("validTo").GetString().ShouldBe("2026-09-21");
        await owner.AssertInvariantsAsync();
    }
}

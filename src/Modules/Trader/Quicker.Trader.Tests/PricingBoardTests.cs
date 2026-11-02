using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The pricing board (trader edition, ADR-0035 §5): an item's landed cost from its shipment at the rate paid, its cost
/// to buy again at today's rate, the price it sells at and the margin, the suggested price at the target margin rounded
/// up to the increment, the alerts, and applying the suggestion into the sales price list.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PricingBoardTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task The_board_prices_from_landed_cost_at_todays_rate_warns_of_a_loss_and_writes_the_price_list()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "TRP", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        // The dollar was 1,300 when the order was placed and is 1,400 today (the fixture's today is 22 September 2026).
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-20", rate = 1400m });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 45 });

        // 100 pads at 10 USD; freight 200 USD paid at 1,350; the goods paid at 1,350 too; received: landed 16,200 a unit.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "USD", orderDate = "2026-09-01", lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS", unitPrice = 10m } } });
        var shipment = await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid() });
        var shipmentId = shipment.GetProperty("id").GetGuid();
        var freight = (await owner.GetOkAsync("/api/v1/purchasing/charge-types")).EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FREIGHT").GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = freight, amount = 200m, currency = "USD", exchangeRate = 1350m, paidOn = "2026-09-10" }, HttpStatusCode.OK);
        await owner.PutAsync($"/api/v1/purchasing/shipments/{shipmentId}/payment", new { paidAmountRc = 1350000m, paidOn = "2026-09-02" });
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { warehouseId, postingDate = "2026-09-21" }, HttpStatusCode.OK);

        // No price list yet: the board still prices the item from its landed cost, re-valued at today's 1,400.
        var board = await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}");
        board.GetProperty("hints").EnumerateArray().Select(static h => h.GetString()).ShouldBe(["no_price_list"]);
        board.GetProperty("priceListId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        board.GetProperty("targetMarginPct").GetDecimal().ShouldBe(30m);
        board.GetProperty("marketRates").EnumerateArray().Single().GetProperty("rate").GetDecimal().ShouldBe(1400m);
        var row = board.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("itemCode").GetString().ShouldBe("PADS");
        row.GetProperty("onHand").GetDecimal().ShouldBe(100m);
        row.GetProperty("unitPriceFc").GetDecimal().ShouldBe(10m);
        row.GetProperty("ratePaid").GetDecimal().ShouldBe(1350m);
        row.GetProperty("marketRate").GetDecimal().ShouldBe(1400m);
        row.GetProperty("rateMovePct").GetDecimal().ShouldBe(3.7m);
        row.GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(16200m);
        // Stock carries the receipt at the posting day's rate plus the landed freight: 14,000 + 2,700.
        row.GetProperty("stockUnitCostRc").GetDecimal().ShouldBe(16700m);
        row.GetProperty("replacementUnitCostRc").GetDecimal().ShouldBe(16700m);
        row.GetProperty("currentPrice").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        row.GetProperty("suggestedPrice").GetDecimal().ShouldBe(21710m);
        row.GetProperty("alerts").EnumerateArray().Select(static a => a.GetString()).ShouldBe(["no_price", "rate_up"]);
        board.GetProperty("unpricedItems").GetInt32().ShouldBe(1);

        // The trader's settings: a 20 % target and prices rounded up to 250; a retail list in dinars with the pads at 15,000.
        (await owner.PutAsync("/api/v1/organization/settings/trader.pricing.target_margin_pct", new { value = 20, valueType = "number" })).GetProperty("key").GetString().ShouldBe("trader.pricing.target_margin_pct");
        await owner.PutAsync("/api/v1/organization/settings/trader.pricing.round_to", new { value = 250, valueType = "number" });
        var list = await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId, code = "RETAIL", name = Name("Retail", "التجزئة"), currency = "IQD", isDefault = true });
        var listId = list.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/pricing/price-lists/{listId}/items", new { itemId = pads, minQuantity = 1m, price = 15000m });

        // Selling at 15,000 loses money against the 16,200 paid; the target says 20,040, rounded up to 20,250.
        board = await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}");
        board.GetProperty("hints").GetArrayLength().ShouldBe(0);
        board.GetProperty("priceListId").GetGuid().ShouldBe(listId);
        board.GetProperty("targetMarginPct").GetDecimal().ShouldBe(20m);
        board.GetProperty("roundTo").GetDecimal().ShouldBe(250m);
        row = board.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("currentPrice").GetDecimal().ShouldBe(15000m);
        row.GetProperty("currentMarginPct").GetDecimal().ShouldBe(-10.2m);
        row.GetProperty("suggestedPrice").GetDecimal().ShouldBe(20250m);
        row.GetProperty("suggestedMarginPct").GetDecimal().ShouldBe(21.3m);
        row.GetProperty("alerts").EnumerateArray().Select(static a => a.GetString()).ShouldBe(["loss_at_cost", "rate_up"]);
        board.GetProperty("losingItems").GetInt32().ShouldBe(1);

        // Applying writes the price into the list from today; applying the same again is fine; a wrong list or price is refused.
        var applied = await owner.PostAsync("/api/v1/trader/pricing/apply", new { companyId, prices = new[] { new { itemId = pads, price = 20250m } } }, HttpStatusCode.OK);
        row = applied.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("currentPrice").GetDecimal().ShouldBe(20250m);
        row.GetProperty("currentMarginPct").GetDecimal().ShouldBe(21.3m);
        row.GetProperty("alerts").EnumerateArray().Select(static a => a.GetString()).ShouldBe(["rate_up"]);
        applied.GetProperty("losingItems").GetInt32().ShouldBe(0);
        await owner.PostAsync("/api/v1/trader/pricing/apply", new { companyId, prices = new[] { new { itemId = pads, price = 20250m } } }, HttpStatusCode.OK);
        var entries = await owner.GetOkAsync($"/api/v1/pricing/price-lists/{listId}/items");
        entries.EnumerateArray().Single().GetProperty("price").GetDecimal().ShouldBe(20250m);
        (await owner.PostErrorAsync("/api/v1/trader/pricing/apply", new { companyId, priceListId = Guid.CreateVersion7(), prices = new[] { new { itemId = pads, price = 20250m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("trader.price_list_unknown");
        (await owner.PostErrorAsync("/api/v1/trader/pricing/apply", new { companyId, prices = new[] { new { itemId = pads, price = -1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("trader.price_invalid");
        await owner.AssertInvariantsAsync();
    }
}

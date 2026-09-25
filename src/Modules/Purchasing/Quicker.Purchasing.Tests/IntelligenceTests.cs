using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>Supplier intelligence (roadmap 4.8): price history from orders and invoices, lead times from order to receipt, and a scorecard whose weights, tolerance and look-back are company configuration.</summary>
[Collection(ApiCollection.Name)]
public sealed class IntelligenceTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static JsonElement Row(JsonElement rows, string code) => rows.EnumerateArray().Single(r => r.GetProperty("partnerCode").GetString() == code);

    private static async Task BuyAsync(HttpClient owner, Guid companyId, Guid warehouseId, Guid supplier, Guid tea, decimal price, string expected, string received, decimal returned)
    {
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, orderDate = "2026-09-01", expectedDate = expected, lines = new[] { new { itemId = tea, quantity = 10m, uom = "PCS", unitPrice = price, discountPct = 0m } } });
        var orderId = order.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = received, lines = new[] { new { orderLineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 10m } } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var receiptLine = (await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK)).GetProperty("lines")[0].GetProperty("id").GetGuid();
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId, partnerId = supplier, documentDate = received, lines = new[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = price } } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        if (returned > 0m)
        {
            var ret = await owner.PostAsync("/api/v1/purchasing/returns", new { receiptId, postingDate = received, lines = new[] { new { receiptLineId = receiptLine, quantity = returned } } });
            await owner.PostAsync($"/api/v1/purchasing/returns/{ret.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Price_history_lead_times_and_a_configurable_scorecard_rank_an_on_time_supplier_above_a_late_one()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "SI", legalName = Name("Scoring Co", "شركة التقييم"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var alpha = (await owner.PostAsync("/api/v1/partners", new { code = "ALPHA", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{alpha}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 4 });
        var beta = (await owner.PostAsync("/api/v1/partners", new { code = "BETA", legalName = Name("Beta Trading", "بيتا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{beta}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 2 });

        // Alpha: ordered on the 1st, expected on the 5th, received on the 4th. Beta: expected on the 3rd, received on the 8th, 2 of 10 sent back.
        await BuyAsync(owner, companyId, warehouseId, alpha, tea, 1000m, "2026-09-05", "2026-09-04", 0m);
        await BuyAsync(owner, companyId, warehouseId, beta, tea, 1100m, "2026-09-03", "2026-09-08", 2m);

        var history = await owner.GetOkAsync($"/api/v1/purchasing/intelligence/price-history?companyId={companyId}&itemId={tea}");
        history.GetProperty("points").GetArrayLength().ShouldBe(4);
        var summary = history.GetProperty("summary").EnumerateArray().ToList();
        summary.Count.ShouldBe(2);
        summary[0].GetProperty("partnerCode").GetString().ShouldBe("ALPHA");
        summary[0].GetProperty("averagePriceFc").GetDecimal().ShouldBe(1000m);
        summary[1].GetProperty("lastPriceFc").GetDecimal().ShouldBe(1100m);
        (await owner.GetOkAsync($"/api/v1/purchasing/intelligence/price-history?companyId={companyId}&partnerId={beta}")).GetProperty("points").GetArrayLength().ShouldBe(2);

        var leadTimes = await owner.GetOkAsync($"/api/v1/purchasing/intelligence/lead-times?companyId={companyId}");
        Row(leadTimes, "ALPHA").GetProperty("averageDays").GetDecimal().ShouldBe(3m);
        Row(leadTimes, "ALPHA").GetProperty("statedLeadTimeDays").GetInt32().ShouldBe(4);
        Row(leadTimes, "ALPHA").GetProperty("onTimePct").GetDecimal().ShouldBe(100m);
        Row(leadTimes, "BETA").GetProperty("maxDays").GetInt32().ShouldBe(7);
        Row(leadTimes, "BETA").GetProperty("onTimePct").GetDecimal().ShouldBe(0m);

        // Default weights 30/25/20/25: Alpha 100 (A); Beta late (0), 80 % kept, prices and invoices clean: 65 (C).
        var card = await owner.GetOkAsync($"/api/v1/purchasing/intelligence/scorecard?companyId={companyId}");
        card.GetProperty("settings").GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        var rows = card.GetProperty("rows");
        rows[0].GetProperty("partnerCode").GetString().ShouldBe("ALPHA");
        Row(rows, "ALPHA").GetProperty("score").GetDecimal().ShouldBe(100m);
        Row(rows, "ALPHA").GetProperty("grade").GetString().ShouldBe("A");
        var betaRow = Row(rows, "BETA");
        betaRow.GetProperty("onTimePct").GetDecimal().ShouldBe(0m);
        betaRow.GetProperty("quantityPct").GetDecimal().ShouldBe(80m);
        betaRow.GetProperty("pricePct").GetDecimal().ShouldBe(100m);
        betaRow.GetProperty("invoicePct").GetDecimal().ShouldBe(100m);
        betaRow.GetProperty("score").GetDecimal().ShouldBe(65m);
        betaRow.GetProperty("grade").GetString().ShouldBe("C");

        // Five days of on-time tolerance puts Beta's receipt on time: 95 (A). Invalid settings are refused.
        (await owner.PutErrorAsync("/api/v1/purchasing/intelligence/scoring-settings", new { companyId, onTimeWeight = 0m, quantityWeight = 0m, priceWeight = 0m, invoiceWeight = 0m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("scoring.weights_invalid");
        (await owner.PutErrorAsync("/api/v1/purchasing/intelligence/scoring-settings", new { companyId, onTimeWeight = 30m, quantityWeight = 25m, priceWeight = 20m, invoiceWeight = 25m, lookbackMonths = 0 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("scoring.window_invalid");
        var saved = await owner.PutAsync("/api/v1/purchasing/intelligence/scoring-settings", new { companyId, onTimeWeight = 30m, quantityWeight = 25m, priceWeight = 20m, invoiceWeight = 25m, onTimeToleranceDays = 5, lookbackMonths = 12 });
        saved.GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        var tolerant = Row((await owner.GetOkAsync($"/api/v1/purchasing/intelligence/scorecard?companyId={companyId}")).GetProperty("rows"), "BETA");
        tolerant.GetProperty("onTimePct").GetDecimal().ShouldBe(100m);
        tolerant.GetProperty("score").GetDecimal().ShouldBe(95m);
        tolerant.GetProperty("grade").GetString().ShouldBe("A");

        // A scorecard dated before any receipt has nothing to score.
        (await owner.GetOkAsync($"/api/v1/purchasing/intelligence/scorecard?companyId={companyId}&asOf=2026-08-31")).GetProperty("rows").GetArrayLength().ShouldBe(0);
        await owner.AssertInvariantsAsync();
    }
}

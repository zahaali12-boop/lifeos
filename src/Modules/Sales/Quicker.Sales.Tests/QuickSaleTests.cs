using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// The one-step sale of the trader edition (ADR-0035 §6): one request creates and confirms the order, ships it in full
/// and posts the invoice; stock and the receivable follow, a payment settles part of it, and a sale the stock cannot
/// cover keeps nothing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuickSaleTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task A_sale_in_one_step_ships_the_goods_posts_the_invoice_and_is_settled_by_a_cash_receipt()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "QSL", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "ALI", legalName = Name("Ali Auto Repair", "ورشة علي"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "IQD" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        var opening = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId, kind = "opening", lines = new object[] { new { itemId = pads, quantity = 50m, unitCost = 15000m, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{opening.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();

        // Ten pads at 21,000: the order, the shipment and the posted invoice come back as one result.
        var sale = await owner.PostAsync("/api/v1/sales/quick-sales", new { companyId, partnerId = customer, warehouseId, date = "2026-09-22", lines = new[] { new { itemCode = "PADS", quantity = 10m, unitPrice = 21000m } } });
        sale.GetProperty("invoiceStatus").GetString().ShouldBe("posted");
        sale.GetProperty("totalGross").GetDecimal().ShouldBe(210000m);
        sale.GetProperty("currency").GetString().ShouldBe("IQD");
        sale.GetProperty("orderNumber").GetString().ShouldNotBeNullOrEmpty();
        sale.GetProperty("shipmentNumber").GetString().ShouldNotBeNullOrEmpty();
        var openItemId = sale.GetProperty("openItemId").GetGuid();
        (await owner.GetOkAsync($"/api/v1/sales/orders/{sale.GetProperty("orderId").GetGuid()}")).GetProperty("status").GetString().ShouldBe("shipped");
        var stock = await owner.GetOkAsync($"/api/v1/inventory/stock?companyId={companyId}&q=PADS");
        stock.GetProperty("items").EnumerateArray().Single().GetProperty("onHand").GetDecimal().ShouldBe(40m);

        // A cash receipt of 100,000 leaves 110,000 owed.
        var receipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId, partnerId = customer, bankAccountId = cash, receiptDate = "2026-09-22", method = "cash", lines = new[] { new { openItemId, amount = 100000m } } });
        await owner.PostAsync($"/api/v1/banking/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var open = await owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={companyId}");
        open.EnumerateArray().Single(static i => i.GetProperty("item").GetProperty("kind").GetString() == "invoice").GetProperty("item").GetProperty("remainingTc").GetDecimal().ShouldBe(110000m);

        // More than the store holds: refused, and no second invoice exists.
        var refused = await owner.PostAsJsonAsync("/api/v1/sales/quick-sales", new { companyId, partnerId = customer, warehouseId, lines = new[] { new { itemCode = "PADS", quantity = 1000m, unitPrice = 21000m } } }, Json);
        refused.IsSuccessStatusCode.ShouldBeFalse();
        (await owner.GetOkAsync($"/api/v1/sales/invoices?companyId={companyId}")).GetArrayLength().ShouldBe(1);
        (await owner.PostErrorAsync("/api/v1/sales/quick-sales", new { companyId, partnerId = customer, warehouseId, lines = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("quick_sale.lines_required");
        await owner.AssertInvariantsAsync();
    }
}

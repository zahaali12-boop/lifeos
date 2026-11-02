using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The health of the project (trader edition, ADR-0035 §8): the month's income statement in plain words from the
/// ledger (sales, the cost of the goods sold, the rent), the previous month and six months of results; fixed costs
/// unknown until the trader says them, then break-even, the pace of the month, cash runway and the money position;
/// the shipment on the way with its money and hint; sales by item and by customer; what to reorder; the warnings.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HealthTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task The_health_screen_reads_the_month_from_the_books_and_says_what_the_project_needs()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRH", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var filter = (await owner.PostAsync("/api/v1/items", new { code = "FILTER", name = Name("Oil filter", "فلتر زيت"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 45 });
        var freight = (await owner.GetOkAsync("/api/v1/purchasing/charge-types")).EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FREIGHT").GetProperty("id").GetGuid();

        // Nothing in the books yet: every figure is zero, the fixed costs unknown, no sales in the last ninety days.
        var empty = await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}");
        empty.GetProperty("income").GetProperty("netProfit").GetDecimal().ShouldBe(0m);
        empty.GetProperty("viability").GetProperty("fixedCostsSource").GetString().ShouldBe("none");
        empty.GetProperty("viability").GetProperty("marginSource").GetString().ShouldBe("target");
        empty.GetProperty("viability").GetProperty("marginPct").GetDecimal().ShouldBe(23.1m, "a 30 % target on cost is 23.1 % of the price");
        empty.GetProperty("months").GetArrayLength().ShouldBe(6);
        empty.GetProperty("warnings").EnumerateArray().Select(static w => w.GetProperty("code").GetString()).ShouldBe(["fixed_costs_unknown", "no_recent_sales"]);

        // A shipment of 100 pads at 10 USD received on 21 September: freight 200 USD at 1,350, the goods paid at 1,350, the invoice recorded.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "USD", orderDate = "2026-09-01", lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS", unitPrice = 10m } } });
        var arrived = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/shipments/{arrived}/costs", new { chargeTypeId = freight, amount = 200m, currency = "USD", exchangeRate = 1350m, paidOn = "2026-09-10" }, HttpStatusCode.OK);
        await owner.PutAsync($"/api/v1/purchasing/shipments/{arrived}/payment", new { paidAmountRc = 1350000m, paidOn = "2026-09-02" });
        await owner.PostAsync($"/api/v1/purchasing/shipments/{arrived}/receive", new { warehouseId, postingDate = "2026-09-21" }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/shipments/{arrived}/invoice", new { supplierInvoiceNumber = "SZ-2026-41", documentDate = "2026-09-22", postingDate = "2026-09-22" }, HttpStatusCode.OK);

        // A second shipment of 200 filters at 4 USD, shipped on 15 September with freight paid, the goods not paid yet.
        var filters = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "USD", orderDate = "2026-09-10", lines = new[] { new { itemId = filter, quantity = 200m, uom = "PCS", unitPrice = 4m } } });
        var underway = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = filters.GetProperty("id").GetGuid(), expectedArrival = "2026-10-30" })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/shipments/{underway}/advance", new { status = "shipped", occurredOn = "2026-09-15", note = "MSKU 884" }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/shipments/{underway}/costs", new { chargeTypeId = freight, amount = 100m, currency = "USD", exchangeRate = 1350m, paidOn = "2026-09-16" }, HttpStatusCode.OK);

        // Eighty pads sold at 21,000 on 22 September, 500,000 of it received in cash; the rent of September recorded, due on the 15th.
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "ALI", legalName = Name("Ali Auto Repair", "ورشة علي"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "IQD" });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var sale = await owner.PostAsync("/api/v1/sales/quick-sales", new { companyId, partnerId = customer, warehouseId, date = "2026-09-22", lines = new[] { new { itemCode = "PADS", quantity = 80m, unitPrice = 21000m } } });
        var receipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId, partnerId = customer, bankAccountId = cash, receiptDate = "2026-09-22", method = "cash", lines = new[] { new { openItemId = sale.GetProperty("openItemId").GetGuid(), amount = 500000m } } });
        await owner.PostAsync($"/api/v1/banking/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var landlord = (await owner.PostAsync("/api/v1/partners", new { code = "LANDLORD", legalName = Name("Al Rasheed Properties", "عقارات الرشيد"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{landlord}/supplier-accounts/{companyId}", new { currency = "IQD" });
        var rent = (await owner.PostAsync("/api/v1/expenses/categories", new { code = "RENT", name = Name("Shop rent", "إيجار المحل"), accountCode = "6110" })).GetProperty("id").GetGuid();
        var expense = await owner.PostAsync("/api/v1/expenses", new { companyId, kind = "supplier", partnerId = landlord, expenseDate = "2026-09-05", dueDate = "2026-09-15", lines = new[] { new { categoryId = rent, amount = 300000m, description = "September" } } });
        await owner.PostAsync($"/api/v1/expenses/{expense.GetProperty("id").GetGuid()}/confirm", new { }, HttpStatusCode.OK);

        // September so far: 1,680,000 sold, the eighty pads had cost 15,700 each landed, the rent is the only expense.
        var health = await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}");
        health.GetProperty("currency").GetString().ShouldBe("IQD");
        health.GetProperty("asOf").GetString().ShouldBe("2026-09-22");
        var income = health.GetProperty("income");
        income.GetProperty("from").GetString().ShouldBe("2026-09-01");
        income.GetProperty("to").GetString().ShouldBe("2026-09-22");
        income.GetProperty("sales").GetDecimal().ShouldBe(1680000m);
        income.GetProperty("returnsAndDiscounts").GetDecimal().ShouldBe(0m);
        income.GetProperty("netSales").GetDecimal().ShouldBe(1680000m);
        income.GetProperty("costOfGoods").GetDecimal().ShouldBe(1256000m);
        income.GetProperty("grossProfit").GetDecimal().ShouldBe(424000m);
        income.GetProperty("grossMarginPct").GetDecimal().ShouldBe(25.2m);
        income.GetProperty("otherIncome").GetDecimal().ShouldBe(0m);
        income.GetProperty("expenses").GetDecimal().ShouldBe(300000m);
        var rentLine = income.GetProperty("expenseLines").EnumerateArray().Single();
        rentLine.GetProperty("code").GetString().ShouldBe("6110");
        rentLine.GetProperty("name").GetProperty("ar").GetString().ShouldBe("الإيجار");
        rentLine.GetProperty("amount").GetDecimal().ShouldBe(300000m);
        income.GetProperty("financeCosts").GetDecimal().ShouldBe(0m);
        income.GetProperty("incomeTax").GetDecimal().ShouldBe(0m);
        income.GetProperty("netProfit").GetDecimal().ShouldBe(124000m);
        income.GetProperty("netMarginPct").GetDecimal().ShouldBe(7.4m);
        var previous = health.GetProperty("previous");
        previous.GetProperty("from").GetString().ShouldBe("2026-08-01");
        previous.GetProperty("to").GetString().ShouldBe("2026-08-31");
        previous.GetProperty("netProfit").GetDecimal().ShouldBe(0m);
        var months = health.GetProperty("months").EnumerateArray().ToList();
        months.Count.ShouldBe(6);
        months[0].GetProperty("month").GetString().ShouldBe("2026-04-01");
        months[5].GetProperty("month").GetString().ShouldBe("2026-09-01");
        months[5].GetProperty("netProfit").GetDecimal().ShouldBe(124000m);
        months[4].GetProperty("netSales").GetDecimal().ShouldBe(0m);

        // Fixed costs unknown: the month pays for itself, but nobody can say what it needs; the cash is 500,000.
        var viability = health.GetProperty("viability");
        viability.GetProperty("fixedCostsMonthly").ValueKind.ShouldBe(JsonValueKind.Null);
        viability.GetProperty("fixedCostsSource").GetString().ShouldBe("none");
        viability.GetProperty("marginPct").GetDecimal().ShouldBe(25.2m);
        viability.GetProperty("marginSource").GetString().ShouldBe("period");
        viability.GetProperty("breakEvenSalesMonthly").ValueKind.ShouldBe(JsonValueKind.Null);
        viability.GetProperty("monthToDateSales").GetDecimal().ShouldBe(1680000m);
        viability.GetProperty("projectedMonthSales").GetDecimal().ShouldBe(2290909.091m, "1,680,000 in 22 of 30 days");
        viability.GetProperty("cash").GetDecimal().ShouldBe(500000m);
        viability.GetProperty("cashAccounts").EnumerateArray().Single().GetProperty("balanceFc").GetDecimal().ShouldBe(500000m);
        viability.GetProperty("owedToYou").GetDecimal().ShouldBe(1180000m);
        viability.GetProperty("owedToYouOverdue").GetDecimal().ShouldBe(0m);
        viability.GetProperty("youOwe").GetDecimal().ShouldBe(1600000m, "the pads invoice at 1,300 and the rent");
        viability.GetProperty("youOweOverdue").GetDecimal().ShouldBe(300000m, "the rent was due on the 15th");
        viability.GetProperty("netPosition").GetDecimal().ShouldBe(80000m);
        viability.GetProperty("cashRunwayMonths").ValueKind.ShouldBe(JsonValueKind.Null);

        // The filters are on the way: 800 USD of goods not paid yet, 135,000 of freight paid; the shipment wants its payment recorded.
        var shipment = health.GetProperty("shipments").EnumerateArray().Single();
        shipment.GetProperty("shipmentId").GetGuid().ShouldBe(underway);
        shipment.GetProperty("status").GetString().ShouldBe("shipped");
        shipment.GetProperty("expectedArrival").GetString().ShouldBe("2026-10-30");
        shipment.GetProperty("goodsTotal").GetDecimal().ShouldBe(800m);
        shipment.GetProperty("goodsTotalRc").GetDecimal().ShouldBe(1040000m);
        shipment.GetProperty("paidAmountRc").ValueKind.ShouldBe(JsonValueKind.Null);
        shipment.GetProperty("costsTotalRc").GetDecimal().ShouldBe(135000m);
        shipment.GetProperty("hints").EnumerateArray().Select(static h => h.GetString()).ShouldBe(["payment_missing"]);
        health.GetProperty("goodsOnTheWayRc").GetDecimal().ShouldBe(1040000m);
        health.GetProperty("paidOnTheWayRc").GetDecimal().ShouldBe(135000m);
        health.GetProperty("unpaidOnTheWayRc").GetDecimal().ShouldBe(1040000m);

        // Sales by item and by customer; the pads will run out: eighty sold in ninety days, twenty left, none on the way.
        var byItem = health.GetProperty("byItem").EnumerateArray().Single();
        byItem.GetProperty("itemCode").GetString().ShouldBe("PADS");
        byItem.GetProperty("quantity").GetDecimal().ShouldBe(80m);
        byItem.GetProperty("netSales").GetDecimal().ShouldBe(1680000m);
        byItem.GetProperty("sharePct").GetDecimal().ShouldBe(100m);
        byItem.GetProperty("invoices").GetInt32().ShouldBe(1);
        var byCustomer = health.GetProperty("byCustomer").EnumerateArray().Single();
        byCustomer.GetProperty("partnerCode").GetString().ShouldBe("ALI");
        byCustomer.GetProperty("netSales").GetDecimal().ShouldBe(1680000m);
        health.GetProperty("coverDays").GetInt32().ShouldBe(90);
        var reorder = health.GetProperty("reorder").EnumerateArray().Single();
        reorder.GetProperty("itemCode").GetString().ShouldBe("PADS");
        reorder.GetProperty("onHand").GetDecimal().ShouldBe(20m);
        reorder.GetProperty("soldInCoverDays").GetDecimal().ShouldBe(80m);
        reorder.GetProperty("daysOfCover").GetDecimal().ShouldBe(22.5m);
        reorder.GetProperty("onTheWay").GetDecimal().ShouldBe(0m);
        reorder.GetProperty("suggestedQuantity").GetDecimal().ShouldBe(60m);
        health.GetProperty("lastSaleOn").GetString().ShouldBe("2026-09-22");
        health.GetProperty("warnings").EnumerateArray().Select(static w => w.GetProperty("code").GetString()).ShouldBe(["fixed_costs_unknown", "overdue_payables", "shipments_attention", "reorder_needed"]);

        // The trader says the fixed costs are 300,000 a month: break-even, the pace of the month and the runway follow; the cash covers 1.7 months.
        await owner.PutAsync("/api/v1/organization/settings/trader.health.fixed_costs_monthly", new { value = 300000, valueType = "number" });
        health = await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}");
        viability = health.GetProperty("viability");
        viability.GetProperty("fixedCostsMonthly").GetDecimal().ShouldBe(300000m);
        viability.GetProperty("fixedCostsSource").GetString().ShouldBe("setting");
        viability.GetProperty("breakEvenSalesMonthly").GetDecimal().ShouldBe(1188679.245m, "300,000 of fixed costs at a 25.24 % margin");
        viability.GetProperty("breakEvenCoveragePct").GetDecimal().ShouldBe(192.7m);
        viability.GetProperty("cashRunwayMonths").GetDecimal().ShouldBe(1.7m);
        var warnings = health.GetProperty("warnings").EnumerateArray().Select(w => (w.GetProperty("code").GetString(), w.GetProperty("value").ValueKind == JsonValueKind.Null ? (decimal?)null : w.GetProperty("value").GetDecimal())).ToList();
        warnings.ShouldBe([("cash_runway_short", 1.7m), ("overdue_payables", 300000m), ("shipments_attention", 1m), ("reorder_needed", 1m)]);

        // A period of the trader's choosing: the whole of August has nothing, and a period that ends before it starts is refused.
        var august = await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}&from=2026-08-01&to=2026-08-31");
        august.GetProperty("income").GetProperty("netSales").GetDecimal().ShouldBe(0m);
        august.GetProperty("previous").GetProperty("from").GetString().ShouldBe("2026-07-01");
        august.GetProperty("viability").GetProperty("marginSource").GetString().ShouldBe("target");
        august.GetProperty("viability").GetProperty("monthToDateSales").GetDecimal().ShouldBe(1680000m, "the pace of this month does not depend on the period shown");
        august.GetProperty("byItem").GetArrayLength().ShouldBe(0);
        august.GetProperty("reorder").GetArrayLength().ShouldBe(1);
        var invalid = await owner.GetAsync(new Uri($"/api/v1/trader/health?companyId={companyId}&from=2026-09-10&to=2026-09-01", UriKind.Relative));
        invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await owner.AssertInvariantsAsync();
    }
}

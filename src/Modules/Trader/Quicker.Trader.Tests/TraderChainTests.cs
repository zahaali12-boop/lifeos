using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The whole trader chain on one company, checked link by link against the books (ADR-0035 §11): the owner's opening
/// cash; offers compared and the cheapest supplier ordered from; the order followed as a shipment, the goods and the
/// freight paid from the cash box before arrival; arrival posting the receipt and the landed freight, the forwarder's
/// invoice taking its advance; the supplier's invoice taking the goods advance with the exchange difference realised;
/// the price set from the landed cost; a sale at that price; the customer paying part; a count finding one missing.
/// Then every screen's figure is compared with the ledger: the health income statement with the accountant's profit and
/// loss, the cash with the bank and cash accounts, what is owed each way with the control accounts, the stock value with
/// the inventory account, the clearing accounts empty, and the invariant harness.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TraderChainTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private async Task<decimal> RoleAsync(Guid tenantId, Guid companyId, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = tenantId, c = companyId, role });
    }

    [Fact]
    public async Task Every_link_from_the_offer_to_the_health_screen_agrees_with_the_ledger()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRC", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1400m });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        async Task<Guid> PartnerAsync(string code, string en, string ar, bool supplier, string currency)
        {
            var id = (await owner.PostAsync("/api/v1/partners", new { code, legalName = Name(en, ar), isSupplier = supplier, isCustomer = !supplier })).GetProperty("id").GetGuid();
            await owner.PutAsync(supplier ? $"/api/v1/partners/{id}/supplier-accounts/{companyId}" : $"/api/v1/partners/{id}/customer-accounts/{companyId}", new { currency });
            return id;
        }

        var shenzhen = await PartnerAsync("SHENZ", "Shenzhen Auto Parts", "شنجن لقطع السيارات", true, "USD");
        var guangzhou = await PartnerAsync("GUANG", "Guangzhou Motors", "غوانزو موتورز", true, "USD");
        var forwarder = await PartnerAsync("FWD", "Gulf Freight", "الخليج للشحن", true, "IQD");
        var customer = await PartnerAsync("ALI", "Ali Auto Repair", "ورشة علي", false, "IQD");
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();

        // The owner puts 5,000,000 into the cash box (opening balances, as the setup wizard posts them).
        await owner.PostAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-08-31", new { lines = new object[] { new { account = "1111", debit = 5000000m, credit = 0m, bankAccount = "CASH" }, new { account = "3100", debit = 0m, credit = 5000000m } } }, HttpStatusCode.OK);

        // Offers: Shenzhen at 10 USD, Guangzhou at 11; Shenzhen is recommended and ordered from.
        var rfq = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Brake pads", lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS" } }, partnerIds = new[] { shenzhen, guangzhou } });
        var rfqId = rfq.GetProperty("id").GetGuid();
        var rfqLine = rfq.GetProperty("lines").EnumerateArray().Single().GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = guangzhou, currency = "USD", leadTimeDays = 40, lines = new[] { new { rfqLineId = rfqLine, unitPrice = 11m } } }, HttpStatusCode.OK);
        var quoted = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = shenzhen, currency = "USD", leadTimeDays = 45, lines = new[] { new { rfqLineId = rfqLine, unitPrice = 10m } } }, HttpStatusCode.OK);
        var comparison = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/compare", new { }, HttpStatusCode.OK);
        comparison.GetProperty("recommendation").GetProperty("partnerCode").GetString().ShouldBe("SHENZ");
        var quoteId = quoted.GetProperty("quotes").EnumerateArray().Single(q => q.GetProperty("partnerId").GetGuid() == shenzhen).GetProperty("id").GetGuid();
        var order = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/award", new { quoteId, warehouseId }, HttpStatusCode.OK);
        order.GetProperty("totalGross").GetDecimal().ShouldBe(1000m);

        // The order as a shipment: freight of 100 USD at 1,400 to the forwarder; the goods (1,000 USD for 1,380,000) and the freight paid from the cash box.
        var shipmentId = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        var types = await owner.GetOkAsync("/api/v1/purchasing/charge-types");
        var freightType = types.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FREIGHT").GetProperty("id").GetGuid();
        var withFreight = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = freightType, amount = 100m, currency = "USD", partnerId = forwarder }, HttpStatusCode.OK);
        var freightId = withFreight.GetProperty("costs").EnumerateArray().Single().GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, paidRc = 1380000m }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs/{freightId}/pay", new { bankAccountId = cash }, HttpStatusCode.OK);

        // Arrival and the supplier's invoice: stock 1,400,000 + 140,000; the advances settle both invoices; 20,000 exchange gain.
        var received = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { warehouseId }, HttpStatusCode.OK);
        var landedUnit = received.GetProperty("lines").EnumerateArray().Single().GetProperty("landedUnitCostRc").GetDecimal();
        landedUnit.ShouldBe(15200m, "1,380,000 paid and 140,000 freight for 100 pads");
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/invoice", new { supplierInvoiceNumber = "SZ-1" }, HttpStatusCode.OK);

        // The price from the landed cost: the board shows the same landed cost; 20,000 is written into the sales list.
        var list = (await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId, code = "RETAIL", name = Name("Retail", "التجزئة"), currency = "IQD", isDefault = true })).GetProperty("id").GetGuid();
        var board = await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}");
        board.GetProperty("rows").EnumerateArray().Single().GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(landedUnit);
        await owner.PostAsync("/api/v1/trader/pricing/apply", new { companyId, priceListId = list, prices = new[] { new { itemId = pads, price = 20000m } } }, HttpStatusCode.OK);

        // Sixty pads sold at the list's price; Ali pays 700,000 into the cash box.
        var sale = await owner.PostAsync("/api/v1/sales/quick-sales", new { companyId, partnerId = customer, warehouseId, lines = new[] { new { itemCode = "PADS", quantity = 60m } } });
        sale.GetProperty("totalGross").GetDecimal().ShouldBe(1200000m, "the price came from the list");
        var receipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId, partnerId = customer, bankAccountId = cash, method = "cash", lines = new[] { new { openItemId = sale.GetProperty("openItemId").GetGuid(), amount = 700000m } } });
        await owner.PostAsync($"/api/v1/banking/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);

        // The count: 39 on the shelf where 40 are expected; finished, one pad is written off.
        var countId = (await owner.PostAsync("/api/v1/inventory/counts", new { companyId, warehouseId, scope = "full" })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/counts/{countId}/freeze", new { }, HttpStatusCode.OK);
        var line = (await owner.GetOkAsync($"/api/v1/inventory/counts/{countId}/sheet")).GetProperty("lines").EnumerateArray().Single().GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/counts/{countId}/entries", new { entries = new[] { new { lineId = line, countedQty = 39m } } }, HttpStatusCode.OK);
        (await owner.PostAsync($"/api/v1/inventory/counts/{countId}/finish", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");

        // Health against the books.
        var health = await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}");
        var income = health.GetProperty("income");
        income.GetProperty("netSales").GetDecimal().ShouldBe(1200000m);
        income.GetProperty("costOfGoods").GetDecimal().ShouldBe(939400m, "60 sold and 1 missing at 15,400 each");
        income.GetProperty("otherIncome").GetDecimal().ShouldBe(20000m, "the exchange gain on the advance");
        var statement = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL", from = income.GetProperty("from").GetString(), to = income.GetProperty("to").GetString() }, HttpStatusCode.OK);
        var net = statement.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "NET").GetProperty("values")[0].GetDecimal();
        income.GetProperty("netProfit").GetDecimal().ShouldBe(net, "the trader's income statement is the accountant's");
        net.ShouldBe(280600m);

        var viability = health.GetProperty("viability");
        var box = await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}");
        box.GetProperty("balanceTc").GetDecimal().ShouldBe(4180000m, "5,000,000 − 1,380,000 − 140,000 + 700,000");
        viability.GetProperty("cash").GetDecimal().ShouldBe(4180000m);
        (await RoleAsync(ws.TenantId, companyId, "Cash")).ShouldBe(4180000m);
        var arAging = await owner.GetOkAsync($"/api/v1/receivables/open-items/aging?companyId={companyId}");
        viability.GetProperty("owedToYou").GetDecimal().ShouldBe(arAging.GetProperty("totals").GetProperty("totalFc").GetDecimal());
        (await RoleAsync(ws.TenantId, companyId, "AR")).ShouldBe(500000m);
        viability.GetProperty("owedToYou").GetDecimal().ShouldBe(500000m);
        var apAging = await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={companyId}");
        apAging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(0m);
        viability.GetProperty("youOwe").GetDecimal().ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "SupplierAdvances")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "LandedCostClearing")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "GRNI")).ShouldBe(0m);

        var valuation = await owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={companyId}");
        valuation.GetProperty("totalValue").GetDecimal().ShouldBe(600600m, "39 pads at 15,400");
        (await RoleAsync(ws.TenantId, companyId, "Inventory")).ShouldBe(600600m);
        var stock = await owner.GetOkAsync($"/api/v1/inventory/stock?companyId={companyId}");
        stock.GetProperty("items").EnumerateArray().Single().GetProperty("onHand").GetDecimal().ShouldBe(39m);
        health.GetProperty("warnings").EnumerateArray().Select(static w => w.GetProperty("code").GetString()).ShouldNotContain("cash_negative");
        await owner.AssertInvariantsAsync();
    }
}

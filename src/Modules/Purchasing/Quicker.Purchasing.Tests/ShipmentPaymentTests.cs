using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// Money paid from a shipment is real (trader edition, ADR-0035 §11): a deposit and the balance of the goods paid in
/// dinars from the cash box become advances to the supplier at the rates paid, applied to the supplier's invoice when it
/// is recorded (the rate difference realised); the freight paid to the forwarder before arrival is an advance applied
/// to the forwarder's invoice made on arrival; customs named after arrival gets the broker's invoice and is paid at
/// once. The cash box, what is owed to each, the clearing account, the stock and the invariants all agree.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShipmentPaymentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private async Task<decimal> RoleAsync(Guid tenantId, Guid companyId, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = tenantId, c = companyId, role });
    }

    private static decimal Remaining(JsonElement items, Guid partnerId) =>
        items.EnumerateArray().Where(i => i.GetProperty("item").GetProperty("partnerId").GetGuid() == partnerId).Sum(static i => i.GetProperty("item").GetProperty("remainingFc").GetDecimal());

    [Fact]
    public async Task Payments_from_a_shipment_move_the_cash_settle_the_supplier_and_the_agents_and_clear_the_landed_costs()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "PAY", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-20", rate = 1400m });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        async Task<Guid> SupplierAsync(string code, string en, string ar, string currency)
        {
            var id = (await owner.PostAsync("/api/v1/partners", new { code, legalName = Name(en, ar), isSupplier = true })).GetProperty("id").GetGuid();
            await owner.PutAsync($"/api/v1/partners/{id}/supplier-accounts/{companyId}", new { currency });
            return id;
        }

        var supplier = await SupplierAsync("SHENZ", "Shenzhen Auto Parts", "شنجن لقطع السيارات", "USD");
        var forwarder = await SupplierAsync("FWD", "Gulf Freight", "الخليج للشحن", "IQD");
        var broker = await SupplierAsync("CUSTOMS", "Umm Qasr customs broker", "مخلص جمركي أم قصر", "IQD");
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var types = await owner.GetOkAsync("/api/v1/purchasing/charge-types");
        Guid TypeId(string code) => types.EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        // 100 pads at 10 USD ordered on 1 September at 1,300.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "USD", orderDate = "2026-09-01", lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS", unitPrice = 10m } } });
        var shipmentId = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();

        // A 300 USD deposit paid with 405,000 dinars from the cash box: an advance at 1,350. Without the dinars it is refused.
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, amount = 300m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.paid_rc_required");
        var paid = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, amount = 300m, paidRc = 405000m, paidOn = "2026-09-02" }, HttpStatusCode.OK);
        var deposit = paid.GetProperty("payments").EnumerateArray().Single();
        deposit.GetProperty("paymentKind").GetString().ShouldBe("supplier_advance");
        deposit.GetProperty("amountTc").GetDecimal().ShouldBe(300m);
        deposit.GetProperty("amountRc").GetDecimal().ShouldBe(405000m);
        paid.GetProperty("goodsOutstandingTc").GetDecimal().ShouldBe(700m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, amount = 800m, paidRc = 1000000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.overpaid");

        // The balance, 700 USD, with 952,000 dinars on 10 September (1,360): the goods are paid, 1,357,000 in all, at 1,357 on average.
        paid = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, paidRc = 952000m, paidOn = "2026-09-10" }, HttpStatusCode.OK);
        paid.GetProperty("goodsPaidTc").GetDecimal().ShouldBe(1000m);
        paid.GetProperty("goodsOutstandingTc").GetDecimal().ShouldBe(0m);
        paid.GetProperty("paidAmountRc").GetDecimal().ShouldBe(1357000m);
        paid.GetProperty("effectiveRate").GetDecimal().ShouldBe(1357m);
        (await owner.PutErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/payment", new { paidAmountRc = 1m }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.paid_by_payments");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, paidRc = 1m }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.goods_paid");
        (await RoleAsync(ws.TenantId, companyId, "SupplierAdvances")).ShouldBe(1357000m, "two advances at the rates paid");

        // Freight of 200 USD at 1,350 (270,000) to the forwarder, paid before arrival; customs of 130,000 with nobody named yet.
        var withFreight = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("FREIGHT"), amount = 200m, currency = "USD", exchangeRate = 1350m, partnerId = forwarder }, HttpStatusCode.OK);
        var freightId = withFreight.GetProperty("costs").EnumerateArray().Single().GetProperty("id").GetGuid();
        var withCustoms = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("CUSTOMS"), amount = 130000m }, HttpStatusCode.OK);
        var customsId = withCustoms.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("chargeTypeCode").GetString() == "CUSTOMS").GetProperty("id").GetGuid();
        var freightPaid = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs/{freightId}/pay", new { bankAccountId = cash, paidOn = "2026-09-12" }, HttpStatusCode.OK);
        freightPaid.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == freightId).GetProperty("paymentNumber").GetString().ShouldNotBeNullOrEmpty();
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs/{freightId}/pay", new { bankAccountId = cash }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.cost_paid");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs/{customsId}/pay", new { bankAccountId = cash }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.cost_partner_required");
        (await owner.DeleteAsync(new Uri($"/api/v1/purchasing/shipments/{shipmentId}/costs/{freightId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Arrival on the 21st: the receipt at 1,400 and the landed costs post; the forwarder's invoice clears the freight and takes the advance.
        var received = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { warehouseId, postingDate = "2026-09-21" }, HttpStatusCode.OK);
        received.GetProperty("status").GetString().ShouldBe("received");
        var freight = received.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == freightId);
        freight.GetProperty("invoiceNumber").GetString().ShouldNotBeNullOrEmpty();
        received.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == customsId).GetProperty("invoiceId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await RoleAsync(ws.TenantId, companyId, "LandedCostClearing")).ShouldBe(-130000m, "only the customs nobody was named for waits in the clearing account");

        // Customs paid to the broker named now: the broker's invoice is made and paid at once.
        var customsPaid = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs/{customsId}/pay", new { bankAccountId = cash, partnerId = broker, paidOn = "2026-09-21" }, HttpStatusCode.OK);
        var customs = customsPaid.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == customsId);
        customs.GetProperty("partnerCode").GetString().ShouldBe("CUSTOMS");
        customs.GetProperty("invoiceNumber").GetString().ShouldNotBeNullOrEmpty();
        customsPaid.GetProperty("payments").EnumerateArray().Last().GetProperty("paymentKind").GetString().ShouldBe("supplier_payment");
        (await RoleAsync(ws.TenantId, companyId, "LandedCostClearing")).ShouldBe(0m);

        // The supplier's invoice on the 22nd at 1,400 (1,400,000): the advances settle it; 43,000 is the exchange gain.
        var invoiced = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/invoice", new { supplierInvoiceNumber = "SZ-77", documentDate = "2026-09-22", postingDate = "2026-09-22" }, HttpStatusCode.OK);
        invoiced.GetProperty("invoiceStatus").GetString().ShouldBe("posted");
        invoiced.GetProperty("payments").EnumerateArray().Where(static p => p.GetProperty("kind").GetString() == "goods").Sum(static p => p.GetProperty("appliedTc").GetDecimal()).ShouldBe(1000m);
        (await RoleAsync(ws.TenantId, companyId, "SupplierAdvances")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "FxGainRealized")).ShouldBe(-43000m);

        // Nobody is owed anything; the cash box paid out exactly what was paid; the stock carries the goods at 1,400 plus the costs.
        var open = await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={companyId}");
        Remaining(open, supplier).ShouldBe(0m);
        Remaining(open, forwarder).ShouldBe(0m);
        Remaining(open, broker).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(0m);
        var box = await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}");
        box.GetProperty("balanceTc").GetDecimal().ShouldBe(-1757000m, "405,000 + 952,000 + 270,000 + 130,000");
        (await RoleAsync(ws.TenantId, companyId, "Inventory")).ShouldBe(1800000m, "1,000 USD at 1,400 and 400,000 of costs");
        (await RoleAsync(ws.TenantId, companyId, "GRNI")).ShouldBe(0m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task What_is_left_to_pay_on_a_shipment_is_what_its_invoice_still_asks_for_whoever_paid_the_rest()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "LEFT", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "GUANG", legalName = Name("Guangzhou Motors", "غوانزو موتورز"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD" });
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "BANK", name = Name("Bank", "المصرف"), kind = "bank", currency = "IQD" })).GetProperty("id").GetGuid();

        // 100 pads at 10,000: a deposit of 400,000 from the shipment, then arrival and the invoice, which takes the deposit.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "IQD", orderDate = "2026-09-01", lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS", unitPrice = 10000m } } });
        var shipmentId = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = bank, amount = 400000m, paidOn = "2026-09-02" }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { warehouseId, postingDate = "2026-09-20" }, HttpStatusCode.OK);
        var invoiced = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/invoice", new { supplierInvoiceNumber = "GZ-9", documentDate = "2026-09-20", postingDate = "2026-09-20" }, HttpStatusCode.OK);
        invoiced.GetProperty("goodsOutstandingTc").GetDecimal().ShouldBe(600000m);

        // 250,000 paid on the invoice from Money (a payment of its open item, as the Money screen sends it): the shipment knows.
        var item = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={companyId}&partnerId={supplier}&status=live")).EnumerateArray()
            .Single(static i => i.GetProperty("item").GetProperty("kind").GetString() == "invoice").GetProperty("item").GetProperty("id").GetGuid();
        var fromMoney = await owner.PostAsync("/api/v1/banking/payments", new { companyId, partnerId = supplier, bankAccountId = bank, paymentDate = "2026-09-21", lines = new[] { new { openItemId = item, amount = 250000m } } });
        await owner.PostAsync($"/api/v1/banking/payments/{fromMoney.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var shipment = await owner.GetOkAsync($"/api/v1/purchasing/shipments/{shipmentId}");
        shipment.GetProperty("goodsOutstandingTc").GetDecimal().ShouldBe(350000m);
        shipment.GetProperty("goodsPaidTc").GetDecimal().ShouldBe(650000m);

        // The shipment pays no more than the invoice still asks for; the rest settles it, and then nothing is left to pay.
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = bank, amount = 400000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.overpaid");
        var rest = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = bank, paidOn = "2026-09-22" }, HttpStatusCode.OK);
        rest.GetProperty("payments").EnumerateArray().Last().GetProperty("paymentKind").GetString().ShouldBe("supplier_payment");
        rest.GetProperty("payments").EnumerateArray().Last().GetProperty("amountTc").GetDecimal().ShouldBe(350000m);
        rest.GetProperty("goodsOutstandingTc").GetDecimal().ShouldBe(0m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = bank }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.goods_paid");
        Remaining(await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={companyId}"), supplier).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-1000000m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "SupplierAdvances")).ShouldBe(0m);
        await owner.AssertInvariantsAsync();
    }
}

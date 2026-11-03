using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// Goods already in hand bought in one step (trader edition, ADR-0035 §22): the order, the shipment that arrives at
/// once with its receipt and landed costs, the agent's invoice for the freight, and the supplier's invoice posted into
/// the payable; the stock carries the landed cost, what is owed is on the books, and paying is the shipment's own
/// step (part now, the rest later). Nothing is kept when a line refuses.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuickPurchaseTests(ApiHostFixture host)
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
    public async Task Goods_in_hand_are_bought_in_one_step_landed_on_the_stock_owed_to_the_supplier_and_paid_in_parts()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "QPR", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/items", new { code = "FILTER", name = Name("Oil filter", "فلتر زيت"), baseUom = "PCS" });
        async Task<Guid> SupplierAsync(string code, string en, string ar, string currency)
        {
            var id = (await owner.PostAsync("/api/v1/partners", new { code, legalName = Name(en, ar), isSupplier = true })).GetProperty("id").GetGuid();
            await owner.PutAsync($"/api/v1/partners/{id}/supplier-accounts/{companyId}", new { currency });
            return id;
        }

        var supplier = await SupplierAsync("SHENZ", "Shenzhen Auto Parts", "شنجن لقطع السيارات", "USD");
        var forwarder = await SupplierAsync("FWD", "Gulf Freight", "الخليج للشحن", "IQD");
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var freight = (await owner.GetOkAsync("/api/v1/purchasing/charge-types")).EnumerateArray().Single(c => c.GetProperty("code").GetString() == "FREIGHT").GetProperty("id").GetGuid();

        // Refused before anything is written: no lines, a zero quantity.
        (await owner.PostErrorAsync("/api/v1/purchasing/quick-purchases", new { companyId, partnerId = supplier, warehouseId, lines = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("quick_purchase.lines_required");
        (await owner.PostErrorAsync("/api/v1/purchasing/quick-purchases", new { companyId, partnerId = supplier, warehouseId, lines = new[] { new { itemCode = "PADS", quantity = 0m, unitPrice = 10m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("quick_purchase.quantity_invalid");
        (await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={companyId}")).GetArrayLength().ShouldBe(0);

        // 50 pads at 10 USD and 100 filters at 3 USD, here since 20 September, invoice SZ-77; 260,000 IQD of freight owed to the forwarder.
        var bought = await owner.PostAsync("/api/v1/purchasing/quick-purchases", new
        {
            companyId,
            partnerId = supplier,
            warehouseId,
            currency = "USD",
            date = "2026-09-20",
            supplierInvoiceNumber = "SZ-77",
            reference = "Container MSKU-1",
            lines = new object[] { new { itemId = pads, quantity = 50m, unitPrice = 10m }, new { itemCode = "FILTER", quantity = 100m, unitPrice = 3m } },
            costs = new[] { new { chargeTypeId = freight, amount = 260000m, currency = "IQD", partnerId = forwarder } },
        });
        bought.GetProperty("currency").GetString().ShouldBe("USD");
        bought.GetProperty("totalGross").GetDecimal().ShouldBe(800m);
        bought.GetProperty("costsTotalRc").GetDecimal().ShouldBe(260000m);
        bought.GetProperty("landedTotalRc").GetDecimal().ShouldBe(1300000m, "800 USD at 1,300 plus the freight");
        bought.GetProperty("invoiceStatus").GetString().ShouldBe("posted");
        bought.GetProperty("outstandingTc").GetDecimal().ShouldBe(800m);
        bought.GetProperty("openItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        var lines = bought.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(2);
        // The freight lands by value: 650,000 of goods against 390,000, so 162,500 and 97,500 of it.
        lines.Single(l => l.GetProperty("itemCode").GetString() == "PADS").GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(16250m);
        lines.Single(l => l.GetProperty("itemCode").GetString() == "FILTER").GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(4875m);
        var shipmentId = bought.GetProperty("shipmentId").GetGuid();

        // The shipment is the record: received the same day, invoiced, with the freight owed to its agent.
        var shipment = await owner.GetOkAsync($"/api/v1/purchasing/shipments/{shipmentId}");
        shipment.GetProperty("status").GetString().ShouldBe("received");
        shipment.GetProperty("invoiceNumber").GetString().ShouldBe(bought.GetProperty("invoiceNumber").GetString());
        shipment.GetProperty("events").EnumerateArray().Select(static e => e.GetProperty("status").GetString()).ShouldBe(["ordered", "received"]);

        // The books: stock at the landed cost, the supplier and the forwarder owed, nothing left in the clearing accounts.
        (await RoleAsync(ws.TenantId, companyId, "Inventory")).ShouldBe(1300000m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(-1300000m, "1,040,000 to the supplier and 260,000 to the forwarder");
        (await RoleAsync(ws.TenantId, companyId, "GRNI")).ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "LandedCostClearing")).ShouldBe(0m);
        var stock = await owner.GetOkAsync($"/api/v1/inventory/stock?companyId={companyId}");
        stock.GetProperty("items").EnumerateArray().Single(static i => i.GetProperty("itemCode").GetString() == "PADS").GetProperty("onHand").GetDecimal().ShouldBe(50m);

        // The pricing board prices both from the landed cost, and the invoice shows under what is owed.
        var board = await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}");
        board.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("itemCode").GetString() == "PADS").GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(16250m);
        var invoices = await owner.GetOkAsync($"/api/v1/purchasing/invoices?companyId={companyId}&partnerId={supplier}");
        var invoice = invoices.EnumerateArray().Single();
        invoice.GetProperty("supplierInvoiceNumber").GetString().ShouldBe("SZ-77");
        invoice.GetProperty("openItems").EnumerateArray().Single().GetProperty("remainingTc").GetDecimal().ShouldBe(800m);

        // Paid in two parts from the cash box in dinars: 300 USD for 390,000, then the rest for 650,000 at the same rate.
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, amount = 300m, paidRc = 390000m, paidOn = "2026-09-21" }, HttpStatusCode.OK);
        invoices = await owner.GetOkAsync($"/api/v1/purchasing/invoices?companyId={companyId}&partnerId={supplier}");
        invoices.EnumerateArray().Single().GetProperty("openItems").EnumerateArray().Single().GetProperty("remainingTc").GetDecimal().ShouldBe(500m);
        var settlements = await owner.GetOkAsync($"/api/v1/payables/settlements?companyId={companyId}&openItemId={bought.GetProperty("openItemId").GetGuid()}");
        settlements.EnumerateArray().Single().GetProperty("amountTc").GetDecimal().ShouldBe(300m);
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/pay", new { bankAccountId = cash, paidRc = 650000m, paidOn = "2026-09-25" }, HttpStatusCode.OK);
        invoices = await owner.GetOkAsync($"/api/v1/purchasing/invoices?companyId={companyId}&partnerId={supplier}");
        invoices.EnumerateArray().Single().GetProperty("openItems").EnumerateArray().Single().GetProperty("remainingTc").GetDecimal().ShouldBe(0m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(-260000m, "only the forwarder is still owed");
        (await RoleAsync(ws.TenantId, companyId, "Cash")).ShouldBe(-1040000m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_purchase_in_the_companys_currency_without_costs_is_one_receipt_and_one_invoice()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "QPL", legalName = Name("Local Parts", "القطع المحلية"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/items", new { code = "BELT", name = Name("Timing belt", "سير التوقيت"), baseUom = "PCS" });
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "LOCAL", legalName = Name("Baghdad Parts", "قطع بغداد"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD" });

        var bought = await owner.PostAsync("/api/v1/purchasing/quick-purchases", new { companyId, partnerId = supplier, warehouseId, lines = new[] { new { itemCode = "BELT", quantity = 20m, unitPrice = 8000m } } });
        bought.GetProperty("currency").GetString().ShouldBe("IQD");
        bought.GetProperty("totalGross").GetDecimal().ShouldBe(160000m);
        bought.GetProperty("landedTotalRc").GetDecimal().ShouldBe(160000m);
        bought.GetProperty("lines").EnumerateArray().Single().GetProperty("landedUnitCostRc").GetDecimal().ShouldBe(8000m);
        (await RoleAsync(ws.TenantId, companyId, "Inventory")).ShouldBe(160000m);
        (await RoleAsync(ws.TenantId, companyId, "AP")).ShouldBe(-160000m);
        (await owner.GetOkAsync($"/api/v1/purchasing/receipts?companyId={companyId}")).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("posted");
        await owner.AssertInvariantsAsync();
    }
}

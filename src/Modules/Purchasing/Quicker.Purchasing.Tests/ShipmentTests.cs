using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// Shipments (trader edition, ADR-0035 §4): an order tracked from ordered to received with dated stages, the costs paid
/// along the way at the rates paid, what was paid for the goods, the landed cost per unit, the hints; arrival posts the
/// receipt and the landed costs through the purchasing services, and the supplier's invoice lands in the payable.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShipmentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Pads, Guid Filters, Guid Supplier);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "SHP", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") });
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var filters = (await owner.PostAsync("/api/v1/items", new { code = "FILTER", name = Name("Oil filter", "فلتر زيت"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var partner = await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true, email = "parts@example.test" });
        var supplier = partner.GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 45 });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), pads, filters, supplier);
    }

    [Fact]
    public async Task A_shipment_tracks_the_order_lands_its_costs_on_arrival_and_records_the_supplier_invoice()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // 100 pads at 10 USD and 50 filters at 4 USD, ordered on 1 September at 1,300: 1,200 USD, 1,560,000 IQD.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new
        {
            companyId = s.CompanyId,
            partnerId = s.Supplier,
            warehouseId = s.WarehouseId,
            currency = "USD",
            orderDate = "2026-09-01",
            expectedDate = "2026-10-15",
            lines = new object[] { new { itemId = s.Pads, quantity = 100m, uom = "PCS", unitPrice = 10m }, new { itemId = s.Filters, quantity = 50m, uom = "PCS", unitPrice = 4m } },
        });
        var orderId = order.GetProperty("id").GetGuid();
        order.GetProperty("status").GetString().ShouldBe("draft");

        // Tracking the draft submits it; the shipment starts at "ordered" on the order date with the order's expected date.
        var shipment = await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId, trackingReference = "MSCU1234567" });
        var shipmentId = shipment.GetProperty("id").GetGuid();
        shipment.GetProperty("status").GetString().ShouldBe("ordered");
        shipment.GetProperty("orderStatus").GetString().ShouldBe("approved");
        shipment.GetProperty("orderNumber").GetString().ShouldStartWith("PO-");
        shipment.GetProperty("goodsTotal").GetDecimal().ShouldBe(1200m);
        shipment.GetProperty("goodsTotalRc").GetDecimal().ShouldBe(1560000m);
        shipment.GetProperty("landedTotalRc").GetDecimal().ShouldBe(1560000m);
        shipment.GetProperty("expectedArrival").GetString().ShouldBe("2026-10-15");
        shipment.GetProperty("events").EnumerateArray().Single().GetProperty("occurredOn").GetString().ShouldBe("2026-09-01");
        shipment.GetProperty("hints").GetArrayLength().ShouldBe(0);
        (await owner.PostErrorAsync("/api/v1/purchasing/shipments", new { orderId }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.exists");

        // Forward only, and never straight to received.
        await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/advance", new { status = "in_production", occurredOn = "2026-09-05" }, HttpStatusCode.OK);
        var shipped = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/advance", new { status = "shipped", occurredOn = "2026-09-20", note = "On the vessel" }, HttpStatusCode.OK);
        shipped.GetProperty("status").GetString().ShouldBe("shipped");
        shipped.GetProperty("events").GetArrayLength().ShouldBe(3);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/advance", new { status = "in_production" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.status_backwards");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/advance", new { status = "received" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.status_invalid");
        // Shipped with nothing paid and no costs: the hints say so.
        shipped.GetProperty("hints").EnumerateArray().Select(static h => h.GetProperty("code").GetString()).ShouldBe(["no_costs", "payment_missing"]);

        // Costs: sea freight 200 USD at the 1,350 actually paid, customs 130,000 IQD; a euro cost with no rate anywhere is refused.
        var chargeTypes = await owner.GetOkAsync("/api/v1/purchasing/charge-types");
        Guid TypeId(string code) => chargeTypes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var withFreight = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("FREIGHT"), amount = 200m, currency = "USD", exchangeRate = 1350m, description = "Sea freight", paidOn = "2026-09-20" }, HttpStatusCode.OK);
        withFreight.GetProperty("costs").EnumerateArray().Single().GetProperty("amountRc").GetDecimal().ShouldBe(270000m);
        var withCustoms = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("CUSTOMS"), amount = 130000m }, HttpStatusCode.OK);
        var customs = withCustoms.GetProperty("costs").EnumerateArray().Single(c => c.GetProperty("chargeTypeCode").GetString() == "CUSTOMS");
        customs.GetProperty("currency").GetString().ShouldBe("IQD");
        customs.GetProperty("exchangeRate").GetDecimal().ShouldBe(1m);
        withCustoms.GetProperty("costsTotalRc").GetDecimal().ShouldBe(400000m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("FREIGHT"), amount = 50m, currency = "EUR" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.rate_missing");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("FREIGHT"), amount = 0m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.amount_invalid");

        // Paid 1,620,000 IQD for the goods: an effective rate of 1,350, 3.8 % above the order's; the landed cost per unit follows.
        var paid = await owner.PutAsync($"/api/v1/purchasing/shipments/{shipmentId}/payment", new { paidAmountRc = 1620000m, paidOn = "2026-09-02" });
        paid.GetProperty("effectiveRate").GetDecimal().ShouldBe(1350m);
        paid.GetProperty("landedTotalRc").GetDecimal().ShouldBe(2020000m);
        paid.GetProperty("hints").EnumerateArray().Select(static h => (h.GetProperty("code").GetString(), h.GetProperty("value").ValueKind == JsonValueKind.Null ? (decimal?)null : h.GetProperty("value").GetDecimal())).ShouldBe([("rate_above_order", 3.8m)]);
        var lines = paid.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(static l => l.GetProperty("itemCode").GetString()).ShouldBe(["PADS", "FILTER"]);
        lines[0].GetProperty("goodsRc").GetDecimal().ShouldBe(1350000m);
        lines[1].GetProperty("goodsRc").GetDecimal().ShouldBe(270000m);
        lines.Sum(static l => l.GetProperty("costsRc").GetDecimal()).ShouldBe(400000m);
        Math.Round(lines[0].GetProperty("landedUnitCostRc").GetDecimal(), 0).ShouldBe(16833m);
        Math.Round(lines[1].GetProperty("landedUnitCostRc").GetDecimal(), 0).ShouldBe(6733m);

        // Arrival: the receipt is posted into the warehouse and the two costs become a posted landed-cost document on it.
        var received = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { warehouseId = s.WarehouseId, postingDate = "2026-10-20" }, HttpStatusCode.OK);
        received.GetProperty("status").GetString().ShouldBe("received");
        received.GetProperty("orderStatus").GetString().ShouldBe("received");
        received.GetProperty("hints").EnumerateArray().Select(static h => h.GetProperty("code").GetString()).ShouldBe(["invoice_missing"]);
        var receiptId = received.GetProperty("receiptId").GetGuid();
        var receipt = await owner.GetOkAsync($"/api/v1/purchasing/receipts/{receiptId}");
        receipt.GetProperty("status").GetString().ShouldBe("posted");
        receipt.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("quantity").GetDecimal()).ShouldBe([100m, 50m]);
        received.GetProperty("receiptNumber").GetString().ShouldBe(receipt.GetProperty("number").GetString());
        var landed = await owner.GetOkAsync($"/api/v1/purchasing/landed-costs/{received.GetProperty("landedCostId").GetGuid()}");
        landed.GetProperty("status").GetString().ShouldBe("posted");
        landed.GetProperty("totalAmountFc").GetDecimal().ShouldBe(400000m);
        landed.GetProperty("charges").GetArrayLength().ShouldBe(2);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/costs", new { chargeTypeId = TypeId("HANDLING"), amount = 5000m }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.costs_locked");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.closed");

        // The supplier's invoice at the ordered prices matches the receipt and is posted into the payable.
        var invoiced = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/invoice", new { supplierInvoiceNumber = "SZ-2026-77", documentDate = "2026-10-21", postingDate = "2026-10-21" }, HttpStatusCode.OK);
        invoiced.GetProperty("invoiceStatus").GetString().ShouldBe("posted");
        invoiced.GetProperty("hints").GetArrayLength().ShouldBe(0);
        var invoice = await owner.GetOkAsync($"/api/v1/purchasing/invoices/{invoiced.GetProperty("invoiceId").GetGuid()}");
        invoice.GetProperty("totalGross").GetDecimal().ShouldBe(1200m);
        invoice.GetProperty("currency").GetString().ShouldBe("USD");
        (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(1);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/invoice", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.invoiced");

        var list = await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={s.CompanyId}");
        list.EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(shipmentId);
        (await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={s.CompanyId}&status=ordered")).GetArrayLength().ShouldBe(0);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_shipment_is_cancelled_before_arrival_and_nothing_is_received_through_it()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Supplier, warehouseId = s.WarehouseId, currency = "USD", orderDate = "2026-09-01", lines = new[] { new { itemId = s.Pads, quantity = 10m, uom = "PCS", unitPrice = 10m } } });
        var shipment = await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = order.GetProperty("id").GetGuid(), expectedArrival = "2026-09-10" });
        var shipmentId = shipment.GetProperty("id").GetGuid();
        var cancelled = await owner.PostAsync($"/api/v1/purchasing/shipments/{shipmentId}/cancel", new { }, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        cancelled.GetProperty("hints").GetArrayLength().ShouldBe(0);
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/receive", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.closed");
        (await owner.PostErrorAsync($"/api/v1/purchasing/shipments/{shipmentId}/advance", new { status = "shipped" }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.closed");
        (await owner.GetOkAsync($"/api/v1/purchasing/receipts?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(0);
    }
}

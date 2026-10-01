using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>Lots and serials (roadmap 3.5, hard scenarios 12 and 13): expiry and FEFO, quarantine and recall with traceability, the serial timeline.</summary>
[Collection(ApiCollection.Name)]
public sealed class TrackingTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Main, Guid Branch, Guid Transit);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var branch = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra branch", "فرع البصرة") });
        var transit = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "TRANSIT", name = Name("In transit", "في الطريق"), kind = "in_transit" });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), branch.GetProperty("id").GetGuid(), transit.GetProperty("id").GetGuid());
    }

    private static DateOnly D(int day) => new(2026, 9, day);

    private Task<StockPostingResult> PostAsync(Setup s, StockLine line, DateOnly date, string docType = "test_document", Guid? docId = null) =>
        host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, date, docType, docId ?? Guid.CreateVersion7(), [line]));

    private async Task<string> RefusedAsync(Setup s, StockLine line, DateOnly date)
    {
        var failed = await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(s, line, date));
        return failed.Message.Split(' ')[3];
    }

    [Fact]
    public async Task Lots_expire_are_picked_first_expiry_first_and_a_recall_holds_the_stock_lists_the_customers_and_releases_reservations()
    {
        var s = await SetUpAsync();
        var milk = (await s.Owner.PostAsync("/api/v1/items", new { code = "MILK", name = Name("Milk 1L", "حليب ١ لتر"), baseUom = "PCS", tracking = "lot", expiryRequired = true, fefo = true, shelfLifeDays = 30 })).GetProperty("id").GetGuid();
        var plain = (await s.Owner.PostAsync("/api/v1/items", new { code = "PLAIN", name = Name("Plain", "عادي"), baseUom = "PCS" })).GetProperty("id").GetGuid();

        // Receipts create lots by number; the expiry comes from the line or the shelf life; a lot-tracked item cannot move without one.
        (await RefusedAsync(s, new StockLine(milk, StockEntryTypes.PurchaseReceipt, 10m, s.Main, UnitCost: 1m), D(1))).ShouldBe("stock.lot_required");
        (await RefusedAsync(s, new StockLine(plain, StockEntryTypes.PurchaseReceipt, 10m, s.Main, UnitCost: 1m, LotNumber: "X"), D(1))).ShouldBe("stock.tracking_not_used");
        var supplier = Guid.CreateVersion7();
        var late = await PostAsync(s, new StockLine(milk, StockEntryTypes.PurchaseReceipt, 20m, s.Main, UnitCost: 1m, LotNumber: "L-SEP30", ExpiresOn: D(30), PartnerId: supplier), D(1));
        late.Entries[0].LotNumber.ShouldBe("L-SEP30");
        var early = await PostAsync(s, new StockLine(milk, StockEntryTypes.PurchaseReceipt, 15m, s.Main, UnitCost: 1.2m, LotNumber: "L-SEP20", ExpiresOn: D(20)), D(2));
        var shelf = await PostAsync(s, new StockLine(milk, StockEntryTypes.PurchaseReceipt, 5m, s.Main, UnitCost: 1m, LotNumber: "L-SHELF"), D(3));
        var lots = await s.Owner.GetOkAsync($"/api/v1/inventory/lots?itemId={milk}");
        lots.GetArrayLength().ShouldBe(3);
        lots.EnumerateArray().Single(static l => l.GetProperty("lotNumber").GetString() == "L-SHELF").GetProperty("expiresOn").GetString().ShouldBe("2026-10-03", "the shelf life of 30 days from the posting date");
        lots.EnumerateArray().Single(static l => l.GetProperty("lotNumber").GetString() == "L-SEP30").GetProperty("supplierPartnerId").GetGuid().ShouldBe(supplier);
        var earlyId = early.Entries[0].LotId!.Value;
        var lateId = late.Entries[0].LotId!.Value;

        // A shipment without a lot is refused with the FEFO plan; the plan takes the earliest expiry first.
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 18m, s.Main), D(5)));
        refused.Message.ShouldContain("stock.lot_required");
        var plan = await s.Owner.GetOkAsync($"/api/v1/inventory/lots/suggest?companyId={s.CompanyId}&itemId={milk}&warehouseId={s.Main}&quantity=18&asOf=2026-09-05");
        plan.EnumerateArray().Select(static p => (p.GetProperty("lotNumber").GetString(), p.GetProperty("take").GetDecimal())).ShouldBe([("L-SEP20", 15m), ("L-SEP30", 3m), ("L-SHELF", 0m)]);
        var customerA = Guid.CreateVersion7();
        var customerB = Guid.CreateVersion7();
        await PostAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 15m, s.Main, LotId: earlyId, PartnerId: customerA), D(5), "sales_shipment");
        await PostAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 3m, s.Main, LotNumber: "L-SEP30", PartnerId: customerA), D(5), "sales_shipment");
        await PostAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 4m, s.Main, LotNumber: "L-SEP30", PartnerId: customerB), D(6), "sales_shipment");
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={milk}");
        balances.EnumerateArray().Select(static b => (b.GetProperty("lotNumber").GetString(), b.GetProperty("onHand").GetDecimal())).ShouldBe([("L-SEP30", 13m), ("L-SHELF", 5m)]);

        // An expired lot is not shipped; scrap still takes it out.
        var october = new DateOnly(2026, 10, 1);
        (await RefusedAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 1m, s.Main, LotNumber: "L-SEP30"), october)).ShouldBe("stock.lot_expired");
        await PostAsync(s, new StockLine(milk, StockEntryTypes.Scrap, 1m, s.Main, LotNumber: "L-SEP30"), october);

        // A reservation on the lot, then a recall: the stock goes on hold everywhere, the reservation is released, the customers are listed.
        var order = Guid.CreateVersion7();
        var reservation = await s.Owner.PostAsync("/api/v1/inventory/reservations", new { companyId = s.CompanyId, itemId = milk, quantity = 2, warehouseId = s.Main, lotId = lateId, sourceDocumentType = "sales_order", sourceDocumentId = order });
        reservation.GetProperty("status").GetString().ShouldBe("active");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={milk}&warehouseId={s.Main}")).Dec("available").ShouldBe(15m);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/lots/{lateId}/status", new { status = "recalled" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("lot.recall_reference_required");
        var recall = await s.Owner.PostAsync($"/api/v1/inventory/lots/{lateId}/status", new { status = "recalled", reason = "Supplier notice: contamination", recallReference = "RC-2026-07" }, HttpStatusCode.OK);
        recall.GetProperty("lot").GetProperty("status").GetString().ShouldBe("recalled");
        recall.GetProperty("reservationsReleased").GetInt32().ShouldBe(1);
        var impact = recall.GetProperty("impact");
        impact.GetProperty("onHand").EnumerateArray().Select(static b => (b.GetProperty("warehouseCode").GetString(), b.GetProperty("onHand").GetDecimal(), b.GetProperty("qualityHold").GetDecimal())).ShouldBe([("MAIN", 12m, 12m)]);
        impact.GetProperty("shippedTo").EnumerateArray().Select(static p => (p.GetProperty("partnerId").GetGuid(), p.GetProperty("quantity").GetDecimal())).ShouldBe([(customerB, 4m), (customerA, 3m)]);
        impact.GetProperty("inbound").GetArrayLength().ShouldBe(1);
        impact.GetProperty("inbound")[0].GetProperty("partnerId").GetGuid().ShouldBe(supplier);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/reservations?sourceDocumentType=sales_order&sourceDocumentId={order}"))[0].GetProperty("status").GetString().ShouldBe("released");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={milk}&warehouseId={s.Main}")).Dec("available").ShouldBe(5m, "only the shelf-life lot is available");
        (await RefusedAsync(s, new StockLine(milk, StockEntryTypes.SaleShipment, 1m, s.Main, LotId: lateId), D(7))).ShouldBe("stock.lot_blocked");

        // A customer returns two units of the recalled lot: they come back on hold, the trace shows the return, and quarantine → active frees the lot again.
        var returned = await PostAsync(s, new StockLine(milk, StockEntryTypes.SaleReturn, 2m, s.Main, LotId: lateId, PartnerId: customerA), D(8), "sales_return");
        returned.Entries[0].LotNumber.ShouldBe("L-SEP30");
        var trace = await s.Owner.GetOkAsync($"/api/v1/inventory/lots/{lateId}/trace");
        trace.GetProperty("onHand")[0].GetProperty("onHand").GetDecimal().ShouldBe(14m);
        trace.GetProperty("onHand")[0].GetProperty("qualityHold").GetDecimal().ShouldBe(14m, "what came back is held with the rest of the lot");
        trace.GetProperty("inbound").GetArrayLength().ShouldBe(2);
        trace.GetProperty("outbound").GetArrayLength().ShouldBe(3);
        await s.Owner.PostAsync($"/api/v1/inventory/lots/{lateId}/status", new { status = "active", reason = "Tested clean" }, HttpStatusCode.OK);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={milk}&warehouseId={s.Main}")).Dec("available").ShouldBe(19m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/lots?itemId={milk}&expiringBefore=2026-09-25")).GetArrayLength().ShouldBe(1);
        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_serial_is_received_sold_returned_repaired_transferred_and_resold_and_its_whole_history_is_one_query()
    {
        var s = await SetUpAsync();
        var phone = (await s.Owner.PostAsync("/api/v1/items", new { code = "PHONE", name = Name("Phone", "هاتف"), baseUom = "PCS", tracking = "serial" })).GetProperty("id").GetGuid();

        // Serial numbers, one per unit, unique per item; the receipt splits into one entry per serial.
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.PurchaseReceipt, 3m, s.Main, UnitCost: 300m, SerialNumbers: ["SN1", "SN2"]), D(1))).ShouldBe("stock.serial_count");
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.PurchaseReceipt, 2m, s.Main, UnitCost: 300m, SerialNumbers: ["SN1", "SN1"]), D(1))).ShouldBe("stock.serial_duplicate");
        var receipt = await PostAsync(s, new StockLine(phone, StockEntryTypes.PurchaseReceipt, 3m, s.Main, UnitCost: 300m, SerialNumbers: ["SN1", "SN2", "SN3"]), D(1));
        receipt.Entries.Count.ShouldBe(3);
        receipt.Entries.Select(static e => e.SerialNumber).ShouldBe(["SN1", "SN2", "SN3"]);
        receipt.Entries.ShouldAllBe(static e => e.Quantity == 1m && e.CostAmount == 300m);
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.PurchaseReceipt, 1m, s.Main, UnitCost: 300m, SerialNumbers: ["SN1"]), D(2))).ShouldBe("stock.serial_in_stock");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/serials?itemId={phone}&status=in_stock")).GetArrayLength().ShouldBe(3);

        // Sold to a customer, returned, sent to repair (held), repaired, transferred to the branch and resold there.
        var customer = Guid.CreateVersion7();
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Main, SerialNumbers: ["SN9"]), D(3))).ShouldBe("stock.serial_unknown");
        await PostAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Main, SerialNumbers: ["SN2"], PartnerId: customer), D(3), "sales_shipment");
        var sn2 = await s.Owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={phone}&serialNumber=SN2");
        sn2.GetProperty("status").GetString().ShouldBe("sold");
        sn2.GetProperty("currentPartnerId").GetGuid().ShouldBe(customer);
        sn2.GetProperty("currentWarehouseId").ValueKind.ShouldBe(JsonValueKind.Null);
        var sn2Id = sn2.GetProperty("id").GetGuid();
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Main, SerialNumbers: ["SN2"]), D(4))).ShouldBe("stock.serial_not_on_hand");
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Branch, SerialNumbers: ["SN1"]), D(4))).ShouldBe("stock.serial_not_in_warehouse");
        await PostAsync(s, new StockLine(phone, StockEntryTypes.SaleReturn, 1m, s.Main, SerialNumbers: ["SN2"], PartnerId: customer, AppliesToSleId: null), D(5), "sales_return");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/serials/{sn2Id}")).GetProperty("status").GetString().ShouldBe("returned");
        var repair = await s.Owner.PostAsync($"/api/v1/inventory/serials/{sn2Id}/status", new { status = "in_repair", note = "Cracked screen" }, HttpStatusCode.OK);
        repair.GetProperty("status").GetString().ShouldBe("in_repair");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={phone}&warehouseId={s.Main}")).Dec("available").ShouldBe(2m, "a unit in repair is on hold");
        (await RefusedAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Main, SerialNumbers: ["SN2"]), D(6))).ShouldBe("stock.serial_in_repair");
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/serials/{sn2Id}/status", new { status = "sold" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("serial.status.invalid");
        await s.Owner.PostAsync($"/api/v1/inventory/serials/{sn2Id}/status", new { status = "in_stock", note = "Screen replaced" }, HttpStatusCode.OK);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={phone}&warehouseId={s.Main}")).Dec("available").ShouldBe(3m);

        var transfer = await s.Owner.PostAsync("/api/v1/inventory/transfers", new { companyId = s.CompanyId, fromWarehouseId = s.Main, toWarehouseId = s.Branch, lines = new object[] { new { itemId = phone, quantity = 1, serialNumbers = new[] { "SN2" } } } });
        var transferId = transfer.GetProperty("id").GetGuid();
        transfer.GetProperty("lines")[0].GetProperty("serialNumbers")[0].GetString().ShouldBe("SN2");
        await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transferId}/ship", new { shipDate = "2026-09-08" }, HttpStatusCode.OK);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/serials/{sn2Id}")).GetProperty("status").GetString().ShouldBe("in_transit");
        await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transferId}/receive", new { receiveDate = "2026-09-09" }, HttpStatusCode.OK);
        var atBranch = await s.Owner.GetOkAsync($"/api/v1/inventory/serials/{sn2Id}");
        atBranch.GetProperty("status").GetString().ShouldBe("in_stock");
        atBranch.GetProperty("currentWarehouseCode").GetString().ShouldBe("BASRA");
        var other = Guid.CreateVersion7();
        await PostAsync(s, new StockLine(phone, StockEntryTypes.SaleShipment, 1m, s.Branch, SerialNumbers: ["SN2"], PartnerId: other), D(10), "sales_shipment");

        // The whole life of SN2 on one screen, oldest first.
        var history = await s.Owner.GetOkAsync($"/api/v1/inventory/serials/{sn2Id}/history");
        history.GetProperty("serial").GetProperty("status").GetString().ShouldBe("sold");
        history.GetProperty("serial").GetProperty("currentPartnerId").GetGuid().ShouldBe(other);
        history.GetProperty("events").EnumerateArray().Select(static e => (e.GetProperty("kind").GetString(), e.GetProperty("toStatus").GetString())).ShouldBe(
        [
            ("movement", "in_stock"), ("movement", "sold"), ("movement", "returned"), ("status", "in_repair"), ("status", "in_stock"),
            ("movement", "in_stock"), ("movement", "in_transit"), ("movement", "in_transit"), ("movement", "in_stock"), ("movement", "sold"),
        ]);
        history.GetProperty("events")[1].GetProperty("costAmount").GetDecimal().ShouldBe(-300m);
        history.GetProperty("events")[3].GetProperty("note").GetString().ShouldBe("Cracked screen");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={phone}")).EnumerateArray().Select(static b => b.GetProperty("serialNumber").GetString()).ShouldBe(["SN1", "SN3"]);
        await s.Owner.AssertInvariantsAsync();
    }
}

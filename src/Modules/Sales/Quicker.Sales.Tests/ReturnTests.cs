using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Sales returns (roadmap 5.8a, POSTING_RULES §2 "Sales return", hard scenarios 12 and 13): goods of posted shipment
/// lines are requested back, authorised, received into stock against the shipment's own ledger entries at the cost
/// they left with (Dr Inventory / Cr Cogs), inspected with a disposition, credited by a credit note on the invoice
/// that billed them at the invoice's price on the SalesReturns role, and closed; a recalled lot comes back on hold
/// and is quarantined or scrapped, never restocked; a serial comes back as returned, goes into repair, back in stock
/// and sells again with its whole chain in one history; scrap leaves at the return's cost; a reversal undoes the lot.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Warehouse, Guid Customer);

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RMA", legalName = Name("Returns Co", "شركة المرتجعات"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BAGHDAD-MALL", legalName = Name("Baghdad Mall LLC", "بغداد مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "DEFECT", name = Name("Defective", "معيب"), appliesTo = "return" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "OTHER", name = Name("Other", "أخرى"), appliesTo = "return", requiresNote = true });
        return new Setup(ws, owner, company, warehouse, customer);
    }

    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } }, HttpStatusCode.Created);
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid());
    }

    /// <summary>A confirmed order of the item shipped in full and posted; returns the shipment and its line.</summary>
    private static async Task<(Guid ShipmentId, Guid ShipmentLineId, JsonElement Line)> ShippedAsync(Setup s, Guid itemId, decimal quantity)
    {
        var order = await s.Owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId, quantity } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        var shipment = await s.Owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity } } });
        var posted = await s.Owner.PostAsync($"/api/v1/sales/shipments/{shipment.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var line = posted.GetProperty("lines")[0];
        return (posted.GetProperty("id").GetGuid(), line.GetProperty("id").GetGuid(), line);
    }

    private static async Task<(Guid InvoiceId, Guid InvoiceLineId)> InvoicedAsync(Setup s, Guid shipmentLineId, decimal quantity)
    {
        var draft = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "shipment", shipmentLineId, quantity } } });
        var posted = await s.Owner.PostAsync($"/api/v1/sales/invoices/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        return (posted.GetProperty("id").GetGuid(), posted.GetProperty("lines")[0].GetProperty("id").GetGuid());
    }

    private static async Task<(decimal OnHand, decimal QualityHold)> BalanceAsync(Setup s, Guid itemId)
    {
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={itemId}&warehouseId={s.Warehouse}");
        return (balances.EnumerateArray().Sum(static b => b.GetProperty("onHand").GetDecimal()), balances.EnumerateArray().Sum(static b => b.GetProperty("qualityHold").GetDecimal()));
    }

    private static Dictionary<string, (decimal Debit, decimal Credit)> ByRole(JsonElement entry) =>
        entry.GetProperty("lines").EnumerateArray().GroupBy(static l => l.GetProperty("accountRole").GetString()!, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => (g.Sum(static l => l.GetProperty("debitTc").GetDecimal()), g.Sum(static l => l.GetProperty("creditTc").GetDecimal())), StringComparer.Ordinal);

    /// <summary>Hard scenario 12: a lot sold, then recalled; the customer sends part of it back. The goods come back
    /// at the shipment's cost and on hold, the line records the recall, restocking is refused, quarantine is
    /// accepted, the credit note credits the invoice's price on the SalesReturns role and applies to the invoice.</summary>
    [Fact]
    public async Task A_recalled_lot_comes_back_on_hold_is_quarantined_never_restocked_and_credited_at_the_invoices_price()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var milk = (await owner.PostAsync("/api/v1/items", new { code = "MILK", name = Name("Milk 1L", "حليب ١ لتر"), baseUom = "PCS", tracking = "lot", expiryRequired = true, fefo = true, listPrice = 5m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = milk, quantity = 8m, unitCost = 2m, reasonCode = "FOUND", lotNumber = "LOT-A", expiresOn = new DateOnly(2026, 12, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var (_, shipmentLineId, shipmentLine) = await ShippedAsync(s, milk, 5m);
        shipmentLine.GetProperty("lotNumber").GetString().ShouldBe("LOT-A");
        shipmentLine.GetProperty("cogsAmount").GetDecimal().ShouldBe(10m);
        var (invoiceId, _) = await InvoicedAsync(s, shipmentLineId, 5m);

        // The supplier recalls the lot: the 3 left on hand go on hold.
        var lotId = (await owner.GetOkAsync($"/api/v1/inventory/lots?itemId={milk}")).EnumerateArray().Single().GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/lots/{lotId}/status", new { status = "recalled", reason = "Supplier notice: contamination", recallReference = "RC-2026-11" }, HttpStatusCode.OK);
        (await BalanceAsync(s, milk)).ShouldBe((3m, 3m));

        // The customer asks to send 3 back; what can come back is the shipped 5.
        var returnable = await owner.GetOkAsync($"/api/v1/sales/returns/returnable?companyId={s.CompanyId}&partnerId={s.Customer}");
        returnable.EnumerateArray().Single().GetProperty("remaining").GetDecimal().ShouldBe(5m);
        (await owner.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 6m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("return.over_return");
        (await owner.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 3m, reasonCode = "OTHER" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("return.reason_note_required");
        var requested = await owner.PostAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, reason = "Recall RC-2026-11", lines = new object[] { new { shipmentLineId, quantity = 3m, reasonCode = "DEFECT", disposition = "restock" } } });
        requested.GetProperty("status").GetString().ShouldBe("requested");
        requested.GetProperty("number").GetString().ShouldStartWith("RMA-");
        var returnId = requested.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{returnId}/receive", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("return.not_authorised");
        (await owner.PostAsync($"/api/v1/sales/returns/{returnId}/authorise", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("authorised");

        // Received: Dr Inventory 6 / Cr Cogs 6 (3 at the shipment's 2), on hold, the recall recorded and restocking overridden to quarantine.
        var received = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/receive", new { }, HttpStatusCode.OK);
        received.GetProperty("status").GetString().ShouldBe("received");
        received.GetProperty("totalCost").GetDecimal().ShouldBe(6m);
        var line = received.GetProperty("lines")[0];
        (line.GetProperty("lotNumber").GetString(), line.GetProperty("lotStatus").GetString(), line.GetProperty("lotBlocked").GetBoolean(), line.GetProperty("disposition").GetString()).ShouldBe(("LOT-A", "recalled", true, "quarantine"));
        line.GetProperty("applied").EnumerateArray().Single().GetProperty("quantity").GetDecimal().ShouldBe(3m);
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{received.GetProperty("journalEntryId").GetGuid()}");
        var byRole = ByRole(entry);
        byRole["Inventory"].ShouldBe((6m, 0m));
        byRole["Cogs"].ShouldBe((0m, 6m));
        (await BalanceAsync(s, milk)).ShouldBe((6m, 6m));
        (await owner.GetOkAsync($"/api/v1/sales/returns/returnable?companyId={s.CompanyId}&partnerId={s.Customer}")).EnumerateArray().Single().GetProperty("remaining").GetDecimal().ShouldBe(2m);
        await owner.AssertInvariantsAsync();

        // Inspection: a recalled lot is never put back on sale.
        var lineId = line.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{returnId}/inspect", new { lines = new object[] { new { lineId, disposition = "restock" } } }, HttpStatusCode.Conflict)).Code.ShouldBe("return.lot_blocked");
        var inspected = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/inspect", new { lines = new object[] { new { lineId, disposition = "quarantine", note = "Held for the recall" } } }, HttpStatusCode.OK);
        inspected.GetProperty("status").GetString().ShouldBe("inspected");

        // Credited: one credit note on the invoice at its price (3 × 5), on the SalesReturns role, applied to the invoice at once.
        var credited = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/credit", new { }, HttpStatusCode.OK);
        credited.GetProperty("status").GetString().ShouldBe("credited");
        credited.GetProperty("totalCredited").GetDecimal().ShouldBe(15m);
        var note = credited.GetProperty("creditNotes").EnumerateArray().ShouldHaveSingleItem();
        (note.GetProperty("status").GetString(), note.GetProperty("totalNet").GetDecimal()).ShouldBe(("posted", 15m));
        note.GetProperty("number").GetString().ShouldStartWith("CN-");
        credited.GetProperty("lines")[0].GetProperty("qtyCredited").GetDecimal().ShouldBe(3m);
        var noteDetail = await owner.GetOkAsync($"/api/v1/sales/invoices/{note.GetProperty("id").GetGuid()}");
        noteDetail.GetProperty("returnId").GetGuid().ShouldBe(returnId);
        noteDetail.GetProperty("creditsInvoiceId").GetGuid().ShouldBe(invoiceId);
        var noteEntry = ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{noteDetail.GetProperty("journalEntryId").GetGuid()}"));
        noteEntry["SalesReturns"].ShouldBe((15m, 0m));
        noteEntry["AR"].ShouldBe((0m, 15m));
        var invoice = await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}");
        invoice.GetProperty("lines")[0].GetProperty("qtyCredited").GetDecimal().ShouldBe(3m);
        invoice.GetProperty("openItems").EnumerateArray().Single().GetProperty("remainingTc").GetDecimal().ShouldBe(10m);
        // The note is the return's: it is undone through the return, which is refused while the note is applied.
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{note.GetProperty("id").GetGuid()}/reverse", new { reason = "By hand" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.credited_by_return");
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{returnId}/reverse", new { reason = "Changed our mind" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.settled");
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{returnId}/credit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("return.not_received");
        (await owner.PostAsync($"/api/v1/sales/returns/{returnId}/close", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("closed");
        await owner.AssertInvariantsAsync();
    }

    /// <summary>Hard scenario 13: a serial sold, returned, repaired, restocked and resold, with the whole chain in its
    /// history; a returned unit that was never invoiced needs no credit and is not invoiced either.</summary>
    [Fact]
    public async Task A_serial_comes_back_as_returned_goes_into_repair_back_in_stock_and_sells_again_with_its_chain_in_one_history()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var phone = (await owner.PostAsync("/api/v1/items", new { code = "PHONE", name = Name("Phone", "هاتف"), baseUom = "PCS", tracking = "serial", listPrice = 500m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = phone, quantity = 2m, unitCost = 200m, reasonCode = "FOUND", serialNumbers = new[] { "SN-1", "SN-2" } } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var (_, shipmentLineId, shipmentLine) = await ShippedAsync(s, phone, 2m);
        shipmentLine.GetProperty("serialNumbers").EnumerateArray().Select(static e => e.GetString()).ToList().ShouldBe(["SN-1", "SN-2"]);
        var (invoiceId, _) = await InvoicedAsync(s, shipmentLineId, 1m);

        // A serialised line names its serials, and only ones that were shipped.
        (await owner.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("return.serials_required");
        (await owner.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 1m, serialNumbers = new[] { "SN-9" } } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("return.serial_not_shipped");
        var requested = await owner.PostAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 1m, serialNumbers = new[] { "SN-1" }, reasonCode = "DEFECT", reason = "Screen flickers", disposition = "repair" } } });
        var returnId = requested.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/returns/{returnId}/authorise", new { }, HttpStatusCode.OK);
        var received = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/receive", new { }, HttpStatusCode.OK);
        received.GetProperty("totalCost").GetDecimal().ShouldBe(200m);
        received.GetProperty("lines")[0].GetProperty("applied").EnumerateArray().Single().GetProperty("serialNumber").GetString().ShouldBe("SN-1");
        var serial = await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={phone}&serialNumber=SN-1");
        serial.GetProperty("status").GetString().ShouldBe("returned");
        var serialId = serial.GetProperty("id").GetGuid();
        (await owner.GetOkAsync($"/api/v1/sales/returns/returnable?companyId={s.CompanyId}&partnerId={s.Customer}")).EnumerateArray().Single().GetProperty("serialNumbers").EnumerateArray().Select(static e => e.GetString()).ToList().ShouldBe(["SN-2"]);
        await owner.AssertInvariantsAsync();

        // Inspection sends it to repair; the repair done, the serial goes back in stock through the serial itself.
        var inspected = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/inspect", new { lines = new object[] { new { lineId = received.GetProperty("lines")[0].GetProperty("id").GetGuid(), disposition = "repair", note = "Replace the display" } } }, HttpStatusCode.OK);
        inspected.GetProperty("lines")[0].GetProperty("disposition").GetString().ShouldBe("repair");
        (await owner.GetOkAsync($"/api/v1/inventory/serials/{serialId}")).GetProperty("status").GetString().ShouldBe("in_repair");
        (await BalanceAsync(s, phone)).ShouldBe((1m, 1m));
        await owner.PostAsync($"/api/v1/inventory/serials/{serialId}/status", new { status = "in_stock", note = "Display replaced" }, HttpStatusCode.OK);
        (await BalanceAsync(s, phone)).ShouldBe((1m, 0m));

        // The invoiced unit was SN-1's line's first: the credit note credits the one unit invoiced, the other unit of the shipment is still invoicable... less the returned one.
        (await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}")).GetArrayLength().ShouldBe(0, "2 shipped, 1 invoiced, 1 returned uninvoiced: nothing left to invoice");
        var credited = await owner.PostAsync($"/api/v1/sales/returns/{returnId}/credit", new { }, HttpStatusCode.OK);
        credited.GetProperty("totalCredited").GetDecimal().ShouldBe(500m);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("lines")[0].GetProperty("qtyCredited").GetDecimal().ShouldBe(1m);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}")).EnumerateArray().Single().GetProperty("remaining").GetDecimal().ShouldBe(1m, "the credited unit was invoiced, so the other shipped unit is invoicable again");

        // Resold: the only unit on hand is SN-1 itself.
        var (_, _, resoldLine) = await ShippedAsync(s, phone, 1m);
        resoldLine.GetProperty("serialNumbers").EnumerateArray().Select(static e => e.GetString()).ToList().ShouldBe(["SN-1"]);
        var history = await owner.GetOkAsync($"/api/v1/inventory/serials/{serialId}/history");
        history.GetProperty("serial").GetProperty("status").GetString().ShouldBe("sold");
        history.GetProperty("events").EnumerateArray().Select(static e => e.GetProperty("toStatus").GetString()).ToList().ShouldBe(["in_stock", "sold", "returned", "in_repair", "in_stock", "sold"]);
        history.GetProperty("events").EnumerateArray().Count(static e => e.GetProperty("sourceDocumentType").GetString() == "sales_return").ShouldBe(1);
        await owner.AssertInvariantsAsync();
    }

    /// <summary>Scrap leaves again at the return's cost (Dr Scrap / Cr Inventory) and pins the return; a return with
    /// nothing scrapped is reversed as a whole; a second return of the rest is refused beyond what was shipped; a
    /// member without the keys is refused.</summary>
    [Fact]
    public async Task Scrap_leaves_at_the_returns_cost_and_pins_the_return_while_an_untouched_return_is_reversed_and_a_reader_is_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = tea, quantity = 10m, unitCost = 40m, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var (shipmentId, shipmentLineId, _) = await ShippedAsync(s, tea, 10m);
        (await BalanceAsync(s, tea)).ShouldBe((0m, 0m));

        // Four cartons crushed in transit: received at 160, scrapped at 160.
        var reader = await InviteAsync(owner, s.Ws, "returns_reader", "sales.return.read", "sales.shipment.read");
        (await reader.Client.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 4m } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");
        var crushed = await owner.PostAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, reason = "Crushed in transit", lines = new object[] { new { shipmentLineId, quantity = 4m, reasonCode = "DEFECT", disposition = "scrap" } } });
        var crushedId = crushed.GetProperty("id").GetGuid();
        (await reader.Client.GetOkAsync($"/api/v1/sales/returns/{crushedId}")).GetProperty("number").GetString().ShouldBe(crushed.GetProperty("number").GetString());
        await owner.PostAsync($"/api/v1/sales/returns/{crushedId}/authorise", new { }, HttpStatusCode.OK);
        (await reader.Client.PostErrorAsync($"/api/v1/sales/returns/{crushedId}/receive", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");
        var received = await owner.PostAsync($"/api/v1/sales/returns/{crushedId}/receive", new { }, HttpStatusCode.OK);
        received.GetProperty("totalCost").GetDecimal().ShouldBe(160m);
        (await BalanceAsync(s, tea)).ShouldBe((4m, 0m));
        (await owner.GetOkAsync($"/api/v1/sales/shipments/{shipmentId}")).GetProperty("lines")[0].GetProperty("qtyReturned").GetDecimal().ShouldBe(4m);
        var inspected = await owner.PostAsync($"/api/v1/sales/returns/{crushedId}/inspect", new { lines = new object[] { new { lineId = received.GetProperty("lines")[0].GetProperty("id").GetGuid(), disposition = "scrap" } } }, HttpStatusCode.OK);
        inspected.GetProperty("lines")[0].GetProperty("scrapCostAmount").GetDecimal().ShouldBe(160m);
        (await BalanceAsync(s, tea)).ShouldBe((0m, 0m));
        var ledger = (await owner.GetOkAsync($"/api/v1/inventory/stock/ledger?companyId={s.CompanyId}&itemId={tea}")).GetProperty("items");
        var scrapEntry = ledger.EnumerateArray().Single(static e => e.GetProperty("entryType").GetString() == "scrap");
        (scrapEntry.GetProperty("quantity").GetDecimal(), scrapEntry.GetProperty("sourceDocumentType").GetString()).ShouldBe((-4m, "sales_return"));
        var journals = new List<Dictionary<string, (decimal Debit, decimal Credit)>>();
        foreach (var summary in (await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/journal-entries?sourceDocumentType=sales_return")).GetProperty("items").EnumerateArray())
        {
            journals.Add(ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{summary.GetProperty("id").GetGuid()}")));
        }

        var scrapJournal = journals.Single(static r => r.ContainsKey("Scrap"));
        scrapJournal["Scrap"].ShouldBe((160m, 0m));
        scrapJournal["Inventory"].ShouldBe((0m, 160m));
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{crushedId}/reverse", new { reason = "Oops" }, HttpStatusCode.Conflict)).Code.ShouldBe("return.disposed");
        await owner.AssertInvariantsAsync();

        // The rest: no more than the six still out can come back; two come back untouched and the return is reversed as a whole.
        (await owner.PostErrorAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 7m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("return.over_return");
        var changed = await owner.PostAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 2m, reasonCode = "OTHER", reason = "Changed their mind", disposition = "restock" } } });
        var changedId = changed.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/sales/returns/{changedId}/reject", new { reason = "Outside the return window" }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("rejected");
        var again = await owner.PostAsync("/api/v1/sales/returns", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { shipmentLineId, quantity = 2m, reasonCode = "OTHER", reason = "Changed their mind", disposition = "restock" } } });
        var againId = again.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/returns/{againId}/authorise", new { }, HttpStatusCode.OK);
        var receivedAgain = await owner.PostAsync($"/api/v1/sales/returns/{againId}/receive", new { }, HttpStatusCode.OK);
        receivedAgain.GetProperty("totalCost").GetDecimal().ShouldBe(80m);
        (await BalanceAsync(s, tea)).ShouldBe((2m, 0m));
        (await owner.PostErrorAsync($"/api/v1/sales/returns/{againId}/credit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("return.nothing_to_credit");
        var reversed = await owner.PostAsync($"/api/v1/sales/returns/{againId}/reverse", new { reason = "The customer kept them after all" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await BalanceAsync(s, tea)).ShouldBe((0m, 0m));
        (await owner.GetOkAsync($"/api/v1/sales/shipments/{shipmentId}")).GetProperty("lines")[0].GetProperty("qtyReturned").GetDecimal().ShouldBe(4m);
        (await owner.GetOkAsync($"/api/v1/sales/returns?companyId={s.CompanyId}")).EnumerateArray().Select(static r => r.GetProperty("status").GetString()).OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(["inspected", "rejected", "reversed"]);
        await owner.AssertInvariantsAsync();
    }
}

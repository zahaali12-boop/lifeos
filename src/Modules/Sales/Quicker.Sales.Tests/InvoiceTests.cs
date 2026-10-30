using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Sales invoices and credit notes (roadmap 5.6a, POSTING_RULES §2): an invoice bills posted shipment lines at the order's
/// price (in part, and consolidated across shipments), posts Dr receivables / Cr revenue / Cr output tax, writes the
/// tax ledger and opens the customer's open items so the receivables control equals them (the harness checks it); a
/// credit note against the invoice applies to it at once; a direct invoice prices non-stock items and service lines,
/// schedules its instalments and allocates the booked functional value across them; open receivables count in the
/// customer's credit exposure.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Tea, Guid Cleaning, Guid Warehouse, Guid Customer, Guid Terms, Guid DomesticGroup, Guid StandardCode);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "INV", legalName = Name("Riyadh Retail", "الرياض للتجزئة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "SAR", validFrom = "2026-01-01", rate = 3.75m });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = company, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "STANDARD") })).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service", listPrice = 50m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "SERVICES") })).GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "RUH", name = Name("Riyadh warehouse", "مستودع الرياض") })).GetProperty("id").GetGuid();
        var terms = (await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "HALF", name = Name("Half now, half in 30 days", "نصف الآن ونصف بعد ٣٠ يومًا"), dueBasis = "invoice_date", dueDays = 30, lines = new[] { new { sequence = 1, percentage = 50m, days = 0 }, new { sequence = 2, percentage = 50m, days = 30 } } })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "JEDDAH-MALL", legalName = Name("Jeddah Mall LLC", "جدة مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = company, warehouseId = warehouse, kind = "opening", lines = new object[] { new { itemId = tea, quantity = 50m, unitCost = 40m, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var standardCode = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        return new Setup(ws, owner, company, tea, cleaning, warehouse, customer, terms, Group("partner", "DOMESTIC"), standardCode);
    }

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } }, HttpStatusCode.Created);
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid());
    }

    /// <summary>A confirmed order of tea shipped in the given parts, each shipment posted; returns the order line and the shipment lines.</summary>
    private static async Task<(Guid OrderId, Guid OrderLineId, List<Guid> ShipmentLineIds)> ShippedOrderAsync(Setup s, decimal quantity, params decimal[] parts)
    {
        var order = await s.Owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        var shipmentLines = new List<Guid>();
        foreach (var part in parts)
        {
            var shipment = await s.Owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = part } } });
            var posted = await s.Owner.PostAsync($"/api/v1/sales/shipments/{shipment.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
            shipmentLines.Add(posted.GetProperty("lines")[0].GetProperty("id").GetGuid());
        }

        return (orderId, lineId, shipmentLines);
    }

    private static Dictionary<string, (decimal Debit, decimal Credit)> ByRole(JsonElement entry) =>
        entry.GetProperty("lines").EnumerateArray().GroupBy(static l => l.GetProperty("accountRole").GetString()!, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => (g.Sum(static l => l.GetProperty("debitTc").GetDecimal()), g.Sum(static l => l.GetProperty("creditTc").GetDecimal())), StringComparer.Ordinal);

    [Fact]
    public async Task An_invoice_bills_shipped_lines_at_the_orders_price_in_part_and_across_shipments_posts_receivables_revenue_and_tax_and_the_last_invoice_takes_the_remainder()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var (orderId, orderLineId, shipmentLines) = await ShippedOrderAsync(s, 10m, 6m, 4m);

        var invoicable = await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}");
        invoicable.EnumerateArray().Select(static l => (l.GetProperty("kind").GetString(), l.GetProperty("remaining").GetDecimal(), l.GetProperty("unitPrice").GetDecimal())).ShouldBe([("shipment", 6m, 100m), ("shipment", 4m, 100m)]);

        // Four of the first shipment's six and all of the second, on one invoice: the order's price, 15% VAT from the item's and the customer's groups.
        var draft = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, customerReference = "PO-778", lines = new object[] { new { kind = "shipment", shipmentLineId = shipmentLines[0], quantity = 4m }, new { kind = "shipment", shipmentLineId = shipmentLines[1], quantity = 4m } } });
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("number").GetString().ShouldStartWith("INV-");
        (draft.GetProperty("totalNet").GetDecimal(), draft.GetProperty("totalTax").GetDecimal(), draft.GetProperty("totalGross").GetDecimal()).ShouldBe((800m, 120m, 920m));
        draft.GetProperty("lines")[0].GetProperty("taxRatePct").GetDecimal().ShouldBe(15m);
        var invoiceId = draft.GetProperty("id").GetGuid();

        var posted = await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{posted.GetProperty("journalEntryId").GetGuid()}");
        var byRole = ByRole(entry);
        byRole["AR"].ShouldBe((920m, 0m));
        byRole["Revenue"].ShouldBe((0m, 800m));
        byRole["OutputTax"].ShouldBe((0m, 120m));
        var arLine = entry.GetProperty("lines").EnumerateArray().Single(static l => l.GetProperty("accountRole").GetString() == "AR");
        arLine.GetProperty("subledgerType").GetString().ShouldBe("AR");
        arLine.GetProperty("subledgerRef").GetGuid().ShouldBe(invoiceId);
        var openItem = posted.GetProperty("openItems").EnumerateArray().ShouldHaveSingleItem();
        (openItem.GetProperty("kind").GetString(), openItem.GetProperty("originalTc").GetDecimal(), openItem.GetProperty("remainingTc").GetDecimal(), openItem.GetProperty("customerReference").GetString()).ShouldBe(("invoice", 920m, 920m, "PO-778"));

        var order = await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}");
        order.GetProperty("lines")[0].GetProperty("qtyInvoiced").GetDecimal().ShouldBe(8m);
        var left = await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}");
        left.EnumerateArray().Select(static l => (l.GetProperty("shipmentLineId").GetGuid(), l.GetProperty("remaining").GetDecimal())).ShouldBe([(shipmentLines[0], 2m)]);
        await owner.AssertInvariantsAsync();

        // More than what is left is refused; the exact remainder takes the line's remaining net to the cent.
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "shipment", shipmentLineId = shipmentLines[0], quantity = 3m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.quantity_exceeds_uninvoiced");
        var rest = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "shipment", shipmentLineId = shipmentLines[0], quantity = 2m } } });
        rest.GetProperty("totalGross").GetDecimal().ShouldBe(230m);
        var restId = rest.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/invoices/{restId}/post", new { }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}")).GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}")).GetProperty("lines")[0].GetProperty("qtyInvoiced").GetDecimal().ShouldBe(10m);

        var aging = await owner.GetOkAsync($"/api/v1/receivables/open-items/aging?companyId={s.CompanyId}");
        aging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(1150m);
        await owner.AssertInvariantsAsync();

        // Reversal mirrors the journal, closes the items and gives the quantity back; a reversed invoice is not reversed again.
        var reversed = await owner.PostAsync($"/api/v1/sales/invoices/{restId}/reverse", new { reason = "Billed before delivery was accepted" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("openItems").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("reversed");
        (await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}")).GetProperty("lines")[0].GetProperty("qtyInvoiced").GetDecimal().ShouldBe(8m);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Customer}")).EnumerateArray().Single().GetProperty("remaining").GetDecimal().ShouldBe(2m);
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{restId}/reverse", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.not_posted");
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{restId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.not_draft");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_credit_note_against_an_invoice_line_is_applied_to_the_invoice_as_it_posts_and_guards_both_documents_from_reversal_until_unapplied()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var (_, _, shipmentLines) = await ShippedOrderAsync(s, 8m, 8m);
        var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "shipment", shipmentLineId = shipmentLines[0], quantity = 8m } } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        var invoiceLineId = invoice.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);

        // A credit line is a credit note's; two of the eight pieces are credited at the invoice's price and tax.
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "invoice", invoiceLineId, quantity = 2m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.credit_needs_credit_note");
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "credit_note", lines = new object[] { new { kind = "invoice", invoiceLineId, quantity = 9m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.quantity_exceeds_invoiced");
        var note = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "credit_note", lines = new object[] { new { kind = "invoice", invoiceLineId, quantity = 2m, description = "Damaged in transit" } } });
        note.GetProperty("number").GetString().ShouldStartWith("CN-");
        note.GetProperty("creditsInvoiceId").GetGuid().ShouldBe(invoiceId);
        (note.GetProperty("totalNet").GetDecimal(), note.GetProperty("totalTax").GetDecimal(), note.GetProperty("totalGross").GetDecimal()).ShouldBe((200m, 30m, 230m));
        var noteId = note.GetProperty("id").GetGuid();

        var posted = await owner.PostAsync($"/api/v1/sales/invoices/{noteId}/post", new { }, HttpStatusCode.OK);
        var byRole = ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{posted.GetProperty("journalEntryId").GetGuid()}"));
        byRole["AR"].ShouldBe((0m, 230m));
        byRole["Revenue"].ShouldBe((200m, 0m));
        byRole["OutputTax"].ShouldBe((30m, 0m));
        var creditItem = posted.GetProperty("openItems").EnumerateArray().Single();
        (creditItem.GetProperty("kind").GetString(), creditItem.GetProperty("originalTc").GetDecimal(), creditItem.GetProperty("status").GetString()).ShouldBe(("credit_note", -230m, "settled"));
        var invoiceItem = (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("openItems").EnumerateArray().Single();
        (invoiceItem.GetProperty("remainingTc").GetDecimal(), invoiceItem.GetProperty("status").GetString()).ShouldBe((690m, "partially_settled"));
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("lines")[0].GetProperty("qtyCredited").GetDecimal().ShouldBe(2m);
        await owner.AssertInvariantsAsync();

        // Neither document is reversed from under the application; unapply it first, then the credit note reverses and gives the credited quantity back.
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{invoiceId}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.settled");
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{noteId}/reverse", new { reason = "Wrong line" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.settled");
        var settlement = (await owner.GetOkAsync($"/api/v1/receivables/settlements?companyId={s.CompanyId}&openItemId={creditItem.GetProperty("id").GetGuid()}")).EnumerateArray().Single();
        settlement.GetProperty("kind").GetString().ShouldBe("credit_application");
        await owner.PostAsync($"/api/v1/receivables/settlements/{settlement.GetProperty("id").GetGuid()}/reverse", new { reason = "Credit note issued in error" }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/sales/invoices/{noteId}/reverse", new { reason = "Credit note issued in error" }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("lines")[0].GetProperty("qtyCredited").GetDecimal().ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("openItems").EnumerateArray().Single().GetProperty("remainingTc").GetDecimal().ShouldBe(920m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_direct_invoice_prices_non_stock_items_and_service_lines_schedules_its_instalments_in_the_invoice_currency_and_counts_in_the_customers_credit_exposure()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/customer-accounts/{s.CompanyId}", new { currency = "USD", taxGroupId = s.DomesticGroup, paymentTermsId = s.Terms, creditLimit = 500m, creditExposureBasis = "open_ar" });

        // A stock item has to come through its shipment; a service line names its price and what it is for, and, having
        // no item to take a tax group from, its tax code.
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "item", itemId = s.Tea, quantity = 1m, unitPrice = 100m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.stock_item_needs_shipment");
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "service", quantity = 1m, unitPrice = 40m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.description_required");

        var draft = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, documentDate = "2026-09-10", lines = new object[] { new { kind = "item", itemId = s.Cleaning, quantity = 3m, unitPrice = 50m }, new { kind = "service", quantity = 1m, unitPrice = 40m, description = "Delivery", accountRole = "Revenue", taxCodeId = s.StandardCode } } });
        draft.GetProperty("currency").GetString().ShouldBe("USD");
        (draft.GetProperty("totalNet").GetDecimal(), draft.GetProperty("totalTax").GetDecimal(), draft.GetProperty("totalGross").GetDecimal()).ShouldBe((190m, 28.5m, 218.5m));
        draft.GetProperty("paymentTermsCode").GetString().ShouldBe("HALF");
        var invoiceId = draft.GetProperty("id").GetGuid();

        // Two instalments in the invoice currency; the functional value is allocated from the receivables line actually booked.
        var posted = await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("dueDate").GetString().ShouldBe("2026-10-10");
        var items = posted.GetProperty("openItems").EnumerateArray().ToList();
        items.Select(static i => (i.GetProperty("instalment").GetInt32(), i.GetProperty("dueDate").GetString(), i.GetProperty("originalTc").GetDecimal())).ShouldBe([(1, "2026-09-10", 109.25m), (2, "2026-10-10", 109.25m)]);
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{posted.GetProperty("journalEntryId").GetGuid()}");
        var arLine = entry.GetProperty("lines").EnumerateArray().Single(static l => l.GetProperty("accountRole").GetString() == "AR");
        arLine.GetProperty("debitTc").GetDecimal().ShouldBe(218.5m);
        items.Sum(static i => i.GetProperty("originalFc").GetDecimal()).ShouldBe(arLine.GetProperty("debitFc").GetDecimal());
        await owner.AssertInvariantsAsync();

        // The open invoice counts against the customer's limit: 218.50 open plus a 300 order passes 500, so the order is held.
        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Cleaning, quantity = 6m, unitPrice = 50m } } });
        var held = await owner.PostAsync($"/api/v1/sales/orders/{order.GetProperty("id").GetGuid()}/confirm", new { }, HttpStatusCode.OK);
        held.GetProperty("status").GetString().ShouldBe("on_hold");
        held.GetProperty("blockKind").GetString().ShouldBe("credit_limit");

        // A member with invoice.read lists and reads but may not create or post.
        var reader = await InviteAsync(owner, s.Ws, "invoice_reader", "sales.invoice.read");
        (await reader.Client.GetOkAsync($"/api/v1/sales/invoices?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(1);
        (await reader.Client.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "service", quantity = 1m, unitPrice = 1m, description = "x", taxCodeId = s.StandardCode } } }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        (await reader.Client.PostErrorAsync($"/api/v1/sales/invoices/{invoiceId}/reverse", new { reason = "x" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();

        // A draft that will never post is cancelled and keeps its number.
        var abandoned = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { kind = "service", quantity = 1m, unitPrice = 10m, description = "Setup fee", taxCodeId = s.StandardCode } } });
        var cancelled = await owner.PostAsync($"/api/v1/sales/invoices/{abandoned.GetProperty("id").GetGuid()}/cancel", new { reason = "Customer declined the setup" }, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        cancelled.GetProperty("number").GetString().ShouldBe(abandoned.GetProperty("number").GetString());
        await owner.AssertInvariantsAsync();
    }
}

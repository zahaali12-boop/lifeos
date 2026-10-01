using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// The document flow behind the smart buttons: from any purchasing document, the requisition, request for quotation
/// and order it came from and the receipts, returns, invoices and debit notes that followed, limited to what the
/// member may read.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DocumentFlowTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static async Task<List<(string Type, string Number, bool IsCurrent)>> FlowAsync(HttpClient client, string type, Guid id)
    {
        var flow = await client.GetOkAsync($"/api/v1/purchasing/document-flow/{type}/{id}");
        flow.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        return flow.GetProperty("documents").EnumerateArray()
            .Select(static d => (d.GetProperty("documentType").GetString()!, d.GetProperty("number").GetString()!, d.GetProperty("isCurrent").GetBoolean())).ToList();
    }

    [Fact]
    public async Task Every_document_of_a_purchase_shows_the_whole_chain_from_requisition_to_debit_note_in_order()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "DF", legalName = Name("Flow Co", "شركة التدفق"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-F", legalName = Name("Flow Supplies", "توريدات التدفق"), isSupplier = true, email = "flow@example.test" })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });

        // Requisition, approved at once (no workflow definition), ordered; the order submitted, received and invoiced.
        var requisition = await owner.PostAsync("/api/v1/purchasing/requisitions", new { companyId, neededBy = "2026-10-15", lines = new[] { new { itemId = tea, quantity = 10m, uom = "PCS", estimatedPrice = 1000m, suggestedSupplierId = supplier, warehouseId } } });
        var requisitionId = requisition.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/requisitions/{requisitionId}/submit", new { }, HttpStatusCode.OK);
        var order = (await owner.PostAsync($"/api/v1/purchasing/requisitions/{requisitionId}/orders", new { }, HttpStatusCode.OK)).GetProperty("orders")[0];
        var orderId = order.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        var orderLine = order.GetProperty("lines")[0].GetProperty("id").GetGuid();

        // Before anything follows, the requisition and the order see each other and nothing else.
        (await FlowAsync(owner, "purchase_requisition", requisitionId)).Select(static d => (d.Type, d.IsCurrent))
            .ShouldBe([("purchase_requisition", true), ("purchase_order", false)]);

        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-05", lines = new[] { new { orderLineId = orderLine, quantity = 10m } } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var postedReceipt = await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK);
        var receiptLine = postedReceipt.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId, partnerId = supplier, supplierInvoiceNumber = "F-1", documentDate = "2026-09-06", lines = new[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = 1000m } } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);

        // Two units go back and the supplier's debit note (still a draft) credits them.
        var ret = await owner.PostAsync("/api/v1/purchasing/returns", new { receiptId, postingDate = "2026-09-10", reason = "Damaged", lines = new[] { new { receiptLineId = receiptLine, quantity = 2m } } });
        var returnId = ret.GetProperty("id").GetGuid();
        var postedReturn = await owner.PostAsync($"/api/v1/purchasing/returns/{returnId}/post", new { }, HttpStatusCode.OK);
        var returnLine = postedReturn.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var note = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId, partnerId = supplier, kind = "debit_note", supplierInvoiceNumber = "CN-F", documentDate = "2026-09-12", lines = new[] { new { kind = "return", returnLineId = returnLine, quantity = 2m, unitPrice = 1000m } } });
        var noteId = note.GetProperty("id").GetGuid();

        // From every document the same chain, in the order a purchase moves; only the document itself is marked current.
        var expected = new[]
        {
            ("purchase_requisition", requisition.GetProperty("number").GetString()!),
            ("purchase_order", order.GetProperty("number").GetString()!),
            ("purchase_receipt", postedReceipt.GetProperty("number").GetString()!),
            ("purchase_return", postedReturn.GetProperty("number").GetString()!),
            ("purchase_invoice", (await owner.GetOkAsync($"/api/v1/purchasing/invoices/{invoiceId}")).GetProperty("number").GetString()!),
            ("purchase_invoice", note.GetProperty("number").GetString()!),
        };
        foreach (var (type, id) in new[] { ("purchase_requisition", requisitionId), ("purchase_order", orderId), ("purchase_receipt", receiptId), ("purchase_return", returnId), ("purchase_invoice", invoiceId), ("purchase_invoice", noteId) })
        {
            var flow = await FlowAsync(owner, type, id);
            flow.Select(static d => (d.Type, d.Number)).ToArray().ShouldBe(expected, $"flow of {type}");
            flow.Count(static d => d.IsCurrent).ShouldBe(1, $"flow of {type}");
        }

        var orderFlow = await owner.GetOkAsync($"/api/v1/purchasing/document-flow/purchase_order/{orderId}");
        var receiptInFlow = orderFlow.GetProperty("documents").EnumerateArray().Single(static d => d.GetProperty("documentType").GetString() == "purchase_receipt");
        receiptInFlow.GetProperty("status").GetString().ShouldBe("posted");
        receiptInFlow.GetProperty("date").GetString().ShouldBe("2026-09-05");
        var orderInFlow = orderFlow.GetProperty("documents").EnumerateArray().Single(static d => d.GetProperty("documentType").GetString() == "purchase_order");
        orderInFlow.GetProperty("amount").GetDecimal().ShouldBe(10000m);
        orderInFlow.GetProperty("currency").GetString().ShouldBe("IQD");
        orderFlow.GetProperty("documents").EnumerateArray().Where(static d => d.GetProperty("documentType").GetString() == "purchase_invoice")
            .Select(static d => d.GetProperty("kind").GetString()).ShouldBe(["invoice", "debit_note"]);

        // An expense invoice stands alone; unknown types and documents are refused.
        var expense = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId, partnerId = supplier, kind = "expense", documentDate = "2026-09-07", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 50m, description = "Courier" } } });
        (await FlowAsync(owner, "purchase_invoice", expense.GetProperty("id").GetGuid())).ShouldHaveSingleItem().IsCurrent.ShouldBeTrue();
        (await owner.GetAsync(new Uri($"/api/v1/purchasing/document-flow/sales_order/{orderId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await owner.GetAsync(new Uri($"/api/v1/purchasing/document-flow/purchase_order/{Guid.NewGuid()}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_awarded_request_for_quotation_leads_to_its_order_and_a_member_sees_only_the_documents_they_may_read()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RQ", legalName = Name("Quote Co", "شركة العروض"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-Q", legalName = Name("Quote Supplies", "توريدات العروض"), isSupplier = true, email = "quotes@example.test" })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 5 });

        var rfq = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Tea", dueOn = "2026-10-01", lines = new[] { new { itemId = tea, quantity = 5m, uom = "PCS" } }, partnerIds = new[] { supplier } });
        var rfqId = rfq.GetProperty("id").GetGuid();
        (await FlowAsync(owner, "purchase_rfq", rfqId)).ShouldHaveSingleItem().Type.ShouldBe("purchase_rfq");
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/send", new { }, HttpStatusCode.OK);
        var quoted = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = supplier, currency = "IQD", leadTimeDays = 5, lines = new[] { new { rfqLineId = rfq.GetProperty("lines")[0].GetProperty("id").GetGuid(), unitPrice = 900m } } }, HttpStatusCode.OK);
        var order = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/award", new { quoteId = quoted.GetProperty("quotes")[0].GetProperty("id").GetGuid(), warehouseId }, HttpStatusCode.OK);
        var orderId = order.GetProperty("id").GetGuid();
        (await FlowAsync(owner, "purchase_rfq", rfqId)).Select(static d => d.Type).ShouldBe(["purchase_rfq", "purchase_order"]);
        (await FlowAsync(owner, "purchase_order", orderId)).Select(static d => (d.Type, d.IsCurrent)).ShouldBe([("purchase_rfq", false), ("purchase_order", true)]);

        // A buyer who reads orders but not requests for quotation sees the order alone, and cannot open the RFQ's flow.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "order_reader", name = Name("Orders", "الأوامر"), description = "", grants = new[] { "purchasing.order.read" } });
        var email = $"buyer-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Buyer", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var buyer = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await FlowAsync(buyer, "purchase_order", orderId)).ShouldHaveSingleItem().Type.ShouldBe("purchase_order");
        (await buyer.GetAsync(new Uri($"/api/v1/purchasing/document-flow/purchase_rfq/{rfqId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

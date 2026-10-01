using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>Supplier returns and debit notes (roadmap 4.6): goods go back at the exact cost of their receipt with GRNI as the offset, the supplier's debit note clears GRNI and opens a credit that is applied to the invoice, a credit above or below cost is a purchase price variance, and reversals refuse what has been credited or settled.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReturnTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Tea, Guid Supplier);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "RT", legalName = Name("Returns Co", "شركة المرتجعات"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), tea, supplier);
    }

    private async Task<decimal> BookedAsync(Setup s, string sql, Guid? reference = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = reference ?? Guid.Empty });
    }

    private Task<decimal> RoleAsync(Setup s, string role) => BookedAsync(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private Task<decimal> GrniAsync(Setup s, Guid reference) => BookedAsync(s, "SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.subledger_type = 'GRNI' AND l.subledger_ref = @r", reference);

    private Task<decimal> OnHandAsync(Setup s) => BookedAsync(s, "SELECT coalesce(sum(e.quantity), 0) FROM app.inv_stock_ledger_entries e WHERE e.tenant_id = @t AND e.company_id = @c");

    private static JsonElement Line(JsonElement doc, int index = 0) => doc.GetProperty("lines")[index];

    [Fact]
    public async Task Goods_go_back_at_exact_cost_the_debit_note_clears_grni_and_settles_the_invoice_and_a_credit_below_cost_is_a_price_variance()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Ten units received and invoiced in full: GRNI cleared, 10 000 owed.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Supplier, warehouseId = s.WarehouseId, orderDate = "2026-09-01", lines = new[] { new { itemId = s.Tea, quantity = 10m, uom = "PCS", unitPrice = 1000m, discountPct = 0m } } });
        var orderId = order.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        var orderLine = Line(order).GetProperty("id").GetGuid();
        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-05", lines = new[] { new { orderLineId = orderLine, quantity = 10m } } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var receiptLine = Line(await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK)).GetProperty("id").GetGuid();
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, supplierInvoiceNumber = "A-1", documentDate = "2026-09-06", lines = new[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = 1000m } } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        var postedInvoice = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        var invoiceItem = postedInvoice.GetProperty("openItems").EnumerateArray().Single();
        (await GrniAsync(s, receiptId)).ShouldBe(0m);
        (await RoleAsync(s, "AP")).ShouldBe(-10000m);

        // What can go back: the whole receipt line, in the receipt's warehouse.
        var returnable = await owner.GetOkAsync($"/api/v1/purchasing/returns/returnable?companyId={s.CompanyId}&receiptId={receiptId}");
        returnable.GetArrayLength().ShouldBe(1);
        returnable[0].GetProperty("remaining").GetDecimal().ShouldBe(10m);
        (await owner.PostErrorAsync("/api/v1/purchasing/returns", new { receiptId, lines = new[] { new { receiptLineId = receiptLine, quantity = 11m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("return.over_return");
        (await owner.PostErrorAsync("/api/v1/purchasing/returns", new { receiptId, postingDate = "2026-09-04", lines = new[] { new { receiptLineId = receiptLine, quantity = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("return.before_receipt");

        // Four units back at the receipt's cost: stock down 4 000, GRNI up 4 000 under the return's reference.
        var draft = await owner.PostAsync("/api/v1/purchasing/returns", new { receiptId, postingDate = "2026-09-10", reason = "Damaged in transit", supplierRma = "RMA-7", lines = new[] { new { receiptLineId = receiptLine, quantity = 4m, reason = "Torn packs" } } });
        var returnId = draft.GetProperty("id").GetGuid();
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("number").GetString().ShouldStartWith("RTN-2026-");
        (await owner.PostErrorAsync($"/api/v1/purchasing/returns/{returnId}/reverse", new { reason = "x" }, HttpStatusCode.Conflict)).Code.ShouldBe("return.not_posted");
        var posted = await owner.PostAsync($"/api/v1/purchasing/returns/{returnId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("totalCostFc").GetDecimal().ShouldBe(4000m);
        Line(posted).GetProperty("costAmountFc").GetDecimal().ShouldBe(4000m);
        Line(posted).GetProperty("sleId").ValueKind.ShouldBe(JsonValueKind.String);
        (await OnHandAsync(s)).ShouldBe(6m);
        (await RoleAsync(s, "Inventory")).ShouldBe(6000m);
        (await GrniAsync(s, returnId)).ShouldBe(4000m);
        (await GrniAsync(s, receiptId)).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/purchasing/returns/returnable?companyId={s.CompanyId}&receiptId={receiptId}"))[0].GetProperty("remaining").GetDecimal().ShouldBe(6m);
        (await owner.PutErrorAsync($"/api/v1/purchasing/returns/{returnId}", new { receiptId, lines = new[] { new { receiptLineId = receiptLine, quantity = 1m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("return.not_draft");
        await owner.AssertInvariantsAsync();

        // The supplier's debit note: only on a debit note, never more than went back, and it clears the return's GRNI.
        var creditable = (await owner.GetOkAsync($"/api/v1/purchasing/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Supplier}")).EnumerateArray().Single(static l => l.GetProperty("kind").GetString() == "return");
        creditable.GetProperty("remaining").GetDecimal().ShouldBe(4m);
        creditable.GetProperty("unitPrice").GetDecimal().ShouldBe(1000m);
        var returnLine = creditable.GetProperty("returnLineId").GetGuid();
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, lines = new[] { new { kind = "return", returnLineId = returnLine, quantity = 4m, unitPrice = 1000m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.return_needs_debit_note");
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "debit_note", lines = new[] { new { kind = "return", returnLineId = returnLine, quantity = 5m, unitPrice = 1000m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.return_over_credited");
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "debit_note", lines = new[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 1m, unitPrice = 1000m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.debit_note_lines_only");
        var note = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "debit_note", supplierInvoiceNumber = "CN-1", documentDate = "2026-09-12", lines = new[] { new { kind = "return", returnLineId = returnLine, quantity = 3m, unitPrice = 1000m } } });
        var noteId = note.GetProperty("id").GetGuid();
        Line(note).GetProperty("returnNumber").GetString().ShouldBe(posted.GetProperty("number").GetString());
        note.GetProperty("totalPayable").GetDecimal().ShouldBe(3000m);
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{noteId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        var postedNote = await owner.PostAsync($"/api/v1/purchasing/invoices/{noteId}/post", new { }, HttpStatusCode.OK);
        var creditItem = postedNote.GetProperty("openItems").EnumerateArray().Single();
        creditItem.GetProperty("kind").GetString().ShouldBe("debit_note");
        creditItem.GetProperty("originalTc").GetDecimal().ShouldBe(-3000m);
        creditItem.GetProperty("remainingTc").GetDecimal().ShouldBe(-3000m);
        (await GrniAsync(s, returnId)).ShouldBe(1000m);
        (await RoleAsync(s, "AP")).ShouldBe(-7000m);
        var afterNote = await owner.GetOkAsync($"/api/v1/purchasing/returns/{returnId}");
        Line(afterNote).GetProperty("qtyCredited").GetDecimal().ShouldBe(3m);
        Line(afterNote).GetProperty("creditedAmountFc").GetDecimal().ShouldBe(3000m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/returns/{returnId}/reverse", new { reason = "Changed our mind" }, HttpStatusCode.Conflict)).Code.ShouldBe("return.credited");
        await owner.AssertInvariantsAsync();

        // The credit applied to the invoice: both items move, nothing is booked (same currency, same rate).
        var creditId = creditItem.GetProperty("id").GetGuid();
        var invoiceItemId = invoiceItem.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/payables/settlements/apply", new { settlingItemId = creditId, settledItemId = invoiceItemId, amount = 3500m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("settlement.amount_exceeds");
        (await owner.PostErrorAsync("/api/v1/payables/settlements/apply", new { settlingItemId = invoiceItemId, settledItemId = creditId, amount = 1m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("settlement.kinds_invalid");
        var settlement = await owner.PostAsync("/api/v1/payables/settlements/apply", new { settlingItemId = creditId, settledItemId = invoiceItemId, amount = 3000m }, HttpStatusCode.OK);
        settlement.GetProperty("kind").GetString().ShouldBe("credit_application");
        settlement.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(0m);
        settlement.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        var items = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).EnumerateArray().ToList();
        items.Single(i => i.GetProperty("item").GetProperty("id").GetGuid() == invoiceItemId).GetProperty("item").GetProperty("remainingTc").GetDecimal().ShouldBe(7000m);
        items.Single(i => i.GetProperty("item").GetProperty("id").GetGuid() == creditId).GetProperty("item").GetProperty("status").GetString().ShouldBe("settled");
        (await owner.GetOkAsync($"/api/v1/payables/settlements?companyId={s.CompanyId}&openItemId={creditId}")).GetArrayLength().ShouldBe(1);
        (await owner.PostErrorAsync("/api/v1/payables/settlements/apply", new { settlingItemId = creditId, settledItemId = invoiceItemId, amount = 1m }, HttpStatusCode.Conflict)).Code.ShouldBe("settlement.item_closed");
        (await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{noteId}/reverse", new { reason = "Wrong note" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.settled");
        (await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{invoiceId}/reverse", new { reason = "Wrong invoice" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.settled");
        await owner.AssertInvariantsAsync();

        // The last unit credited below cost: 900 off the payable, the return's 1 000 cleared, 100 purchase price variance.
        var cheap = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "debit_note", supplierInvoiceNumber = "CN-2", documentDate = "2026-09-14", lines = new[] { new { kind = "return", returnLineId = returnLine, quantity = 1m, unitPrice = 900m } } });
        var cheapId = cheap.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{cheapId}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{cheapId}/post", new { }, HttpStatusCode.OK);
        (await GrniAsync(s, returnId)).ShouldBe(0m);
        (await RoleAsync(s, "PurchasePriceVariance")).ShouldBe(100m);
        (await RoleAsync(s, "AP")).ShouldBe(-6100m);
        (await owner.GetOkAsync($"/api/v1/purchasing/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Supplier}")).EnumerateArray().Count(static l => l.GetProperty("kind").GetString() == "return").ShouldBe(0);
        await owner.AssertInvariantsAsync();

        // A second return that nobody credited is reversed: the goods come back at the same cost and the receipt forgets it.
        var second = await owner.PostAsync("/api/v1/purchasing/returns", new { receiptId, postingDate = "2026-09-15", lines = new[] { new { receiptLineId = receiptLine, quantity = 2m } } });
        var secondId = second.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/returns/{secondId}/post", new { }, HttpStatusCode.OK);
        (await OnHandAsync(s)).ShouldBe(4m);
        (await GrniAsync(s, secondId)).ShouldBe(2000m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/returns/{secondId}/reverse", new { reason = "Too early", reversalDate = "2026-09-14" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("return.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/purchasing/returns/{secondId}/reverse", new { reason = "Supplier refused the RMA", reversalDate = "2026-09-16" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await OnHandAsync(s)).ShouldBe(6m);
        (await RoleAsync(s, "Inventory")).ShouldBe(6000m);
        (await GrniAsync(s, secondId)).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/purchasing/returns/returnable?companyId={s.CompanyId}&receiptId={receiptId}"))[0].GetProperty("remaining").GetDecimal().ShouldBe(6m);
        (await owner.GetOkAsync($"/api/v1/purchasing/returns?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();
    }
}

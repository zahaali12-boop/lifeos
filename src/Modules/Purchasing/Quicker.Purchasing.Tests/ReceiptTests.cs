using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Purchasing.Contracts;

namespace Quicker.Purchasing.Tests;

/// <summary>Goods receipts (roadmap 4.3): partial receipts against an order within the supplier's tolerance, stock in at the expected cost against GRNI, lots passed to the stock engine, the order's received quantities and status, reversal at exact cost, and the invariant that GRNI equals the uninvoiced receipt value.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReceiptTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Tea, Guid Milk, Guid Cleaning, Guid Supplier);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "GRN", legalName = Name("Receiving Co", "شركة الاستلام"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var milk = (await owner.PostAsync("/api/v1/items", new { code = "MILK", name = Name("Milk 1L", "حليب ١ لتر"), baseUom = "PCS", tracking = "lot", expiryRequired = true })).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service" })).GetProperty("id").GetGuid();
        var partner = await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true, email = "alpha@example.test" });
        var supplier = partner.GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 7, qtyTolerancePct = 10 });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), tea, milk, cleaning, supplier);
    }

    private async Task<decimal> GrniAsync(Setup s, Guid receiptId)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.credit_fc - l.debit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.subledger_type = 'GRNI' AND l.subledger_ref = @r", new { t = s.Ws.TenantId, c = s.CompanyId, r = receiptId });
    }

    private static async Task<decimal> OnHandAsync(Setup s, Guid itemId)
    {
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={itemId}&warehouseId={s.WarehouseId}");
        return balances.EnumerateArray().Sum(static b => b.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task A_receipt_posts_stock_at_the_expected_cost_against_grni_within_the_tolerance_and_reverses_at_exact_cost()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new
        {
            companyId = s.CompanyId,
            partnerId = s.Supplier,
            warehouseId = s.WarehouseId,
            lines = new object[]
            {
                new { itemId = s.Tea, quantity = 10m, uom = "PCS", unitPrice = 2m, discountPct = 10m },
                new { itemId = s.Milk, quantity = 5m, uom = "PCS", unitPrice = 1m, discountPct = 0m },
                new { itemId = s.Cleaning, quantity = 2m, uom = "HR", unitPrice = 30m, discountPct = 0m },
            },
        });
        var orderId = order.GetProperty("id").GetGuid();
        order.GetProperty("currency").GetString().ShouldBe("USD");
        var teaLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Tea).GetProperty("id").GetGuid();
        var milkLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Milk).GetProperty("id").GetGuid();
        var cleaningLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Cleaning).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/purchasing/receipts", new { orderId, lines = new[] { new { orderLineId = teaLine, quantity = 1m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("receipt.order_not_receivable");
        (await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");

        // What can be received: the ordered quantity plus the supplier's 10 % tolerance.
        var receivable = await owner.GetOkAsync($"/api/v1/purchasing/receipts/receivable?companyId={s.CompanyId}");
        receivable.GetArrayLength().ShouldBe(3);
        var teaReceivable = receivable.EnumerateArray().Single(l => l.GetProperty("orderLineId").GetGuid() == teaLine);
        teaReceivable.GetProperty("remaining").GetDecimal().ShouldBe(10m);
        teaReceivable.GetProperty("maxReceivable").GetDecimal().ShouldBe(11m);
        receivable.EnumerateArray().Single(l => l.GetProperty("orderLineId").GetGuid() == milkLine).GetProperty("tracking").GetString().ShouldBe("lot");

        // Services are received on the invoice; a lot-tracked item needs its lot at posting.
        (await owner.PostErrorAsync("/api/v1/purchasing/receipts", new { orderId, lines = new[] { new { orderLineId = cleaningLine, quantity = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.non_stock_line");
        var draft = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, supplierDeliveryNote = "DN-778", postingDate = "2026-09-22", lines = new object[] { new { orderLineId = teaLine, quantity = 6m }, new { orderLineId = milkLine, quantity = 5m } } });
        var receiptId = draft.GetProperty("id").GetGuid();
        draft.GetProperty("number").GetString().ShouldStartWith("GRN-2026-");
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("exchangeRate").GetDecimal().ShouldBe(1300m);
        draft.GetProperty("totalExpectedCost").GetDecimal().ShouldBe(20540m); // 6 × 1.80 × 1300 + 5 × 1 × 1300
        (await owner.PostErrorAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("stock.lot_required");
        var edited = await owner.PutAsync($"/api/v1/purchasing/receipts/{receiptId}", new { orderId, supplierDeliveryNote = "DN-778", postingDate = "2026-09-22", lines = new object[] { new { orderLineId = teaLine, quantity = 6m }, new { orderLineId = milkLine, quantity = 5m, lotNumber = "L-2026-09", expiresOn = "2027-03-01" } } });
        edited.GetProperty("lines")[1].GetProperty("lotNumber").GetString().ShouldBe("L-2026-09");

        // Posted: stock in at the expected cost, GRNI credited per receipt, the order partly received.
        var posted = await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        posted.GetProperty("totalExpectedCost").GetDecimal().ShouldBe(20540m);
        var teaReceipt = posted.GetProperty("lines")[0];
        teaReceipt.GetProperty("expectedUnitCost").GetDecimal().ShouldBe(2340m);
        teaReceipt.GetProperty("expectedCostAmount").GetDecimal().ShouldBe(14040m);
        teaReceipt.GetProperty("sleId").ValueKind.ShouldBe(JsonValueKind.String);
        (await GrniAsync(s, receiptId)).ShouldBe(20540m);

        // The entry, posted by the costing engine, names the receipt by the number it was issued, in the entry, the
        // browser (found by that number too) and the ledger.
        var receiptNumber = posted.GetProperty("number").GetString()!;
        var entryId = posted.GetProperty("journalEntryId").GetGuid();
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{entryId}");
        entry.GetProperty("sourceDocumentNumber").GetString().ShouldBe(receiptNumber);
        var byNumber = (await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/journal-entries?number={receiptNumber}")).GetProperty("items").EnumerateArray().ToList();
        byNumber.Select(static e => e.GetProperty("id").GetGuid()).ShouldContain(entryId);
        byNumber.ShouldAllBe(e => e.GetProperty("sourceDocumentNumber").GetString() == receiptNumber);
        var ledger = await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/reports/ledger?accountCode={entry.GetProperty("lines")[0].GetProperty("accountCode").GetString()}&from={entry.GetProperty("postingDate").GetString()}");
        ledger.GetProperty("items").EnumerateArray().First(l => l.GetProperty("entryId").GetGuid() == entryId).GetProperty("sourceDocumentNumber").GetString().ShouldBe(receiptNumber);
        (await OnHandAsync(s, s.Tea)).ShouldBe(6m);
        (await OnHandAsync(s, s.Milk)).ShouldBe(5m);
        var afterFirst = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        afterFirst.GetProperty("status").GetString().ShouldBe("partially_received");
        var teaAfterFirst = afterFirst.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == teaLine);
        teaAfterFirst.GetProperty("qtyReceived").GetDecimal().ShouldBe(6m);
        teaAfterFirst.GetProperty("status").GetString().ShouldBe("partially_received");
        afterFirst.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == milkLine).GetProperty("status").GetString().ShouldBe("received");
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{orderId}/change", new { order = new { companyId = s.CompanyId, partnerId = s.Supplier, lines = new[] { new { itemId = s.Tea, quantity = 20m, uom = "PCS", unitPrice = 2m, discountPct = 0m } } }, reason = "More" }, HttpStatusCode.Conflict)).Code.ShouldBe("order.change_after_receipt");
        (await owner.PutErrorAsync($"/api/v1/purchasing/receipts/{receiptId}", new { orderId, lines = new[] { new { orderLineId = teaLine, quantity = 1m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("receipt.not_draft");

        // Over-receipt: 6 more tea would make 12 of 10 (max 11); 5 more is exactly the tolerance.
        var over = await owner.PostErrorAsync("/api/v1/purchasing/receipts", new { orderId, lines = new[] { new { orderLineId = teaLine, quantity = 6m } } }, HttpStatusCode.Conflict);
        over.Code.ShouldBe("receipt.over_receipt");
        over.Problem.GetProperty("why").GetProperty("maxReceivable").GetDecimal().ShouldBe(11m);
        var second = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-22", lines = new[] { new { orderLineId = teaLine, quantity = 5m } } });
        var secondId = second.GetProperty("id").GetGuid();
        var secondPosted = await owner.PostAsync($"/api/v1/purchasing/receipts/{secondId}/post", new { }, HttpStatusCode.OK);
        secondPosted.GetProperty("totalExpectedCost").GetDecimal().ShouldBe(11700m);
        var afterSecond = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        afterSecond.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == teaLine).GetProperty("status").GetString().ShouldBe("received");
        afterSecond.GetProperty("status").GetString().ShouldBe("partially_received"); // the service line is still open for its invoice
        (await OnHandAsync(s, s.Tea)).ShouldBe(11m);

        // The directory other modules read: both receipts' lines are uninvoiced.
        var open = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPurchaseReceiptDirectory>().OpenLinesAsync(s.CompanyId, s.Supplier, null, ct));
        open.Count.ShouldBe(3);
        open.Sum(static l => l.ExpectedCostAmount - l.InvoicedCostAmount - l.ReturnedCostAmount).ShouldBe(32240m);

        // Reversal: the reason is required; the stock leaves at exactly what came in, GRNI nets to zero, the order line goes back to 5 of 10.
        (await owner.PostErrorAsync($"/api/v1/purchasing/receipts/{receiptId}/reverse", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.reason_required");
        (await owner.PostErrorAsync($"/api/v1/purchasing/receipts/{receiptId}/reverse", new { reason = "Too early", reversalDate = "2026-09-01" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/reverse", new { reason = "Delivery refused at the dock" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("reversalReason").GetString().ShouldBe("Delivery refused at the dock");
        (await GrniAsync(s, receiptId)).ShouldBe(0m);
        (await GrniAsync(s, secondId)).ShouldBe(11700m);
        (await OnHandAsync(s, s.Tea)).ShouldBe(5m);
        (await OnHandAsync(s, s.Milk)).ShouldBe(0m);
        var afterReversal = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        var teaAfterReversal = afterReversal.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == teaLine);
        teaAfterReversal.GetProperty("qtyReceived").GetDecimal().ShouldBe(5m);
        teaAfterReversal.GetProperty("status").GetString().ShouldBe("partially_received");
        afterReversal.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == milkLine).GetProperty("status").GetString().ShouldBe("open");
        (await owner.PostErrorAsync($"/api/v1/purchasing/receipts/{receiptId}/reverse", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("receipt.not_posted");
        (await owner.GetOkAsync($"/api/v1/purchasing/receipts?companyId={s.CompanyId}&orderId={orderId}")).GetArrayLength().ShouldBe(2);

        // A draft can be deleted; a posted one cannot.
        var third = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, lines = new[] { new { orderLineId = milkLine, quantity = 1m, lotNumber = "L-2", expiresOn = "2027-01-01" } } });
        await owner.DeleteOkAsync($"/api/v1/purchasing/receipts/{third.GetProperty("id").GetGuid()}");
        var notDeleted = await owner.DeleteAsync(new Uri($"/api/v1/purchasing/receipts/{secondId}", UriKind.Relative));
        notDeleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Every invariant holds, the new GRNI one among them.
        var report = await owner.PostAsync("/api/v1/platform/integrity/run", new { }, HttpStatusCode.OK);
        report.GetProperty("passed").GetBoolean().ShouldBeTrue(report.ToString());
        var grni = report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "grni_matches_receipts");
        grni.GetProperty("checked").GetInt64().ShouldBe(2);
    }
}

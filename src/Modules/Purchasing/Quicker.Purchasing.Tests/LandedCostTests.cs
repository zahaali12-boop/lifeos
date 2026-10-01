using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Purchasing.Tests;

/// <summary>Landed costs (roadmap 4.5, hard scenario 2): freight, handling and customs allocated to two receipt lines by value, quantity and weight to the minor unit, posted three weeks after half the shipment was sold, so the cost splits between stock on hand and cost of sales; the freight invoice settles its estimate at a different amount; a settled document cannot be reversed while an unsettled one can.</summary>
[Collection(ApiCollection.Name)]
public sealed class LandedCostTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Tea, Guid Rice, Guid Supplier, Guid Forwarder);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "LC", legalName = Name("Landed Co", "شركة التكاليف"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS", weightKg = 0.5m })).GetProperty("id").GetGuid();
        var rice = (await owner.PostAsync("/api/v1/items", new { code = "RICE", name = Name("Rice", "أرز"), baseUom = "PCS", weightKg = 2m })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });
        var forwarder = (await owner.PostAsync("/api/v1/partners", new { code = "FWD", legalName = Name("Fast Forwarders", "الشحن السريع"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{forwarder}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 1 });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), tea, rice, supplier, forwarder);
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private async Task<decimal> ClearingAsync(Setup s, Guid docId)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = 'LandedCostClearing' AND l.subledger_ref = @d", new { t = s.Ws.TenantId, c = s.CompanyId, d = docId });
    }

    private static JsonElement Allocation(JsonElement doc, string chargeType, string itemCode) =>
        doc.GetProperty("allocations").EnumerateArray().Single(a => a.GetProperty("chargeTypeCode").GetString() == chargeType && a.GetProperty("itemCode").GetString() == itemCode);

    [Fact]
    public async Task Late_freight_customs_and_handling_split_between_stock_on_hand_and_cost_of_sales_to_the_minor_unit_and_the_charge_invoice_settles_the_estimate()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var types = await owner.GetOkAsync("/api/v1/purchasing/charge-types");
        types.EnumerateArray().Select(static t => t.GetProperty("code").GetString()).ShouldBe(["CUSTOMS", "DUTY", "FREIGHT", "HANDLING", "INSURANCE"]);
        var freight = types.ByCode("FREIGHT").GetProperty("id").GetGuid();
        var handling = types.ByCode("HANDLING").GetProperty("id").GetGuid();
        var customs = types.ByCode("CUSTOMS").GetProperty("id").GetGuid();
        var insurance = types.ByCode("INSURANCE").GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/purchasing/charge-types/{freight}", new { code = "SHIP", name = Name("Shipping", "الشحن") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("charge_type.system_locked");

        // An import received on the 5th: 10 tea at 1,000 and 20 rice at 500; half of each sold on the 10th.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Supplier, warehouseId = s.WarehouseId, orderDate = "2026-09-01", lines = new object[] { new { itemId = s.Tea, quantity = 10m, uom = "PCS", unitPrice = 1000m, discountPct = 0m }, new { itemId = s.Rice, quantity = 20m, uom = "PCS", unitPrice = 500m, discountPct = 0m } } });
        var orderId = order.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        var teaLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Tea).GetProperty("id").GetGuid();
        var riceLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Rice).GetProperty("id").GetGuid();
        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-05", lines = new[] { new { orderLineId = teaLine, quantity = 10m }, new { orderLineId = riceLine, quantity = 20m } } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var posted = await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK);
        var teaReceipt = posted.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var riceReceipt = posted.GetProperty("lines")[1].GetProperty("id").GetGuid();
        await host.InTenantAsync(s.Ws.TenantId, async (sp, ct) =>
        {
            var shipped = await sp.GetRequiredService<IInventoryPosting>().PostAsync(new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 10), "test_document", Guid.CreateVersion7(), [new StockLine(s.Tea, StockEntryTypes.SaleShipment, 5m, s.WarehouseId), new StockLine(s.Rice, StockEntryTypes.SaleShipment, 10m, s.WarehouseId)]), ct);
            shipped.IsSuccess.ShouldBeTrue(shipped.Error?.Code);
            return shipped.Value;
        });
        (await RoleAsync(s, "Cogs")).ShouldBe(10000m);
        (await RoleAsync(s, "Inventory")).ShouldBe(10000m);

        // The allocatable lines carry value, quantity and weight; the charges arrive three weeks later.
        var allocatable = await owner.GetOkAsync($"/api/v1/purchasing/landed-costs/allocatable?companyId={s.CompanyId}");
        allocatable.GetArrayLength().ShouldBe(2);
        allocatable.EnumerateArray().Single(l => l.GetProperty("itemCode").GetString() == "RICE").GetProperty("weightKg").GetDecimal().ShouldBe(2m);
        (await owner.PostErrorAsync("/api/v1/purchasing/landed-costs", new { companyId = s.CompanyId, postingDate = "2026-09-01", receiptLineIds = new[] { teaReceipt }, charges = new[] { new { chargeTypeId = freight, amount = 300m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("landed_cost.before_receipt");
        (await owner.PostErrorAsync("/api/v1/purchasing/landed-costs", new { companyId = s.CompanyId, receiptLineIds = new[] { teaReceipt }, charges = new[] { new { chargeTypeId = freight, amount = 300m, allocationBasis = "volume" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("landed_cost.basis_missing");
        var draft = await owner.PostAsync("/api/v1/purchasing/landed-costs", new
        {
            companyId = s.CompanyId,
            postingDate = "2026-09-26",
            reference = "BL-4471",
            receiptLineIds = new[] { teaReceipt, riceReceipt },
            charges = new object[]
            {
                new { chargeTypeId = freight, amount = 300m, partnerId = s.Forwarder },
                new { chargeTypeId = handling, amount = 90m },
                new { chargeTypeId = customs, amount = 250m, allocationBasis = "weight" },
            },
        });
        var docId = draft.GetProperty("id").GetGuid();
        draft.GetProperty("number").GetString().ShouldStartWith("LC-2026-");
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("totalAmountFc").GetDecimal().ShouldBe(640m);
        Allocation(draft, "FREIGHT", "TEA").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(150m);
        Allocation(draft, "HANDLING", "TEA").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(30m);
        Allocation(draft, "HANDLING", "RICE").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(60m);
        Allocation(draft, "CUSTOMS", "TEA").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(27.778m); // 250 × 5 kg / 45 kg, largest remainder, IQD to the fils
        Allocation(draft, "CUSTOMS", "RICE").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(222.222m);
        Allocation(draft, "CUSTOMS", "RICE").GetProperty("basisValue").GetDecimal().ShouldBe(40m);

        // Posted: half of every allocation went to cost of sales, half sits on stock; the clearing account carries the estimates.
        var landed = await owner.PostAsync($"/api/v1/purchasing/landed-costs/{docId}/post", new { }, HttpStatusCode.OK);
        landed.GetProperty("status").GetString().ShouldBe("posted");
        landed.GetProperty("soldPortionFc").GetDecimal().ShouldBe(320m);
        landed.GetProperty("onHandPortionFc").GetDecimal().ShouldBe(320m);
        Allocation(landed, "FREIGHT", "TEA").GetProperty("soldPortionFc").GetDecimal().ShouldBe(75m);
        Allocation(landed, "CUSTOMS", "TEA").GetProperty("soldPortionFc").GetDecimal().ShouldBe(13.889m);
        Allocation(landed, "CUSTOMS", "RICE").GetProperty("onHandPortionFc").GetDecimal().ShouldBe(111.111m);
        (await RoleAsync(s, "Cogs")).ShouldBe(10320m);
        (await RoleAsync(s, "Inventory")).ShouldBe(10320m);
        (await ClearingAsync(s, docId)).ShouldBe(-640m);
        (await owner.PutErrorAsync($"/api/v1/purchasing/landed-costs/{docId}", new { companyId = s.CompanyId, receiptLineIds = new[] { teaReceipt }, charges = new[] { new { chargeTypeId = freight, amount = 1m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("landed_cost.not_draft");
        await owner.AssertInvariantsAsync();

        // The forwarder's invoice settles the freight estimate at 320: the 20 more land on the same lines in the same split, the clearing account keeps only what is still estimated.
        var freightCharge = landed.GetProperty("charges").EnumerateArray().Single(c => c.GetProperty("chargeTypeCode").GetString() == "FREIGHT").GetProperty("id").GetGuid();
        var invoicable = await owner.GetOkAsync($"/api/v1/purchasing/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Forwarder}");
        invoicable.Only().GetProperty("kind").GetString().ShouldBe("charge");
        invoicable.Only().GetProperty("landedCostChargeId").GetGuid().ShouldBe(freightCharge);
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, lines = new[] { new { kind = "charge", landedCostChargeId = freightCharge, quantity = 1m, unitPrice = 320m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.charge_other_supplier");
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Forwarder, supplierInvoiceNumber = "FWD-1001", documentDate = "2026-09-27", lines = new[] { new { kind = "charge", landedCostChargeId = freightCharge, quantity = 1m, unitPrice = 320m } } });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        invoice.GetProperty("lines").Only().GetProperty("landedCostNumber").GetString().ShouldBe(landed.GetProperty("number").GetString());
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        var postedInvoice = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        postedInvoice.GetProperty("openItems").Only().GetProperty("originalTc").GetDecimal().ShouldBe(320m);
        (await ClearingAsync(s, docId)).ShouldBe(-340m);
        (await RoleAsync(s, "Cogs")).ShouldBe(10330m);
        (await RoleAsync(s, "Inventory")).ShouldBe(10330m);
        (await RoleAsync(s, "AP")).ShouldBe(-320m);
        var settled = await owner.GetOkAsync($"/api/v1/purchasing/landed-costs/{docId}");
        var settledCharge = settled.GetProperty("charges").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == freightCharge);
        settledCharge.GetProperty("isEstimate").GetBoolean().ShouldBeFalse();
        settledCharge.GetProperty("invoicedAmountFc").GetDecimal().ShouldBe(320m);
        settled.GetProperty("totalAmountFc").GetDecimal().ShouldBe(660m);
        settled.GetProperty("soldPortionFc").GetDecimal().ShouldBe(330m);
        Allocation(settled, "FREIGHT", "RICE").GetProperty("allocatedAmountFc").GetDecimal().ShouldBe(160m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/landed-costs/{docId}/reverse", new { reason = "Wrong shipment" }, HttpStatusCode.Conflict)).Code.ShouldBe("landed_cost.settled");
        (await owner.GetOkAsync($"/api/v1/purchasing/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Forwarder}")).GetArrayLength().ShouldBe(0);
        await owner.AssertInvariantsAsync();

        // A second document on the tea alone is posted and reversed: everything goes back.
        var second = await owner.PostAsync("/api/v1/purchasing/landed-costs", new { companyId = s.CompanyId, postingDate = "2026-09-26", receiptLineIds = new[] { teaReceipt }, charges = new[] { new { chargeTypeId = insurance, amount = 100m } } });
        var secondId = second.GetProperty("id").GetGuid();
        var secondPosted = await owner.PostAsync($"/api/v1/purchasing/landed-costs/{secondId}/post", new { }, HttpStatusCode.OK);
        secondPosted.GetProperty("soldPortionFc").GetDecimal().ShouldBe(50m);
        (await RoleAsync(s, "Cogs")).ShouldBe(10380m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/landed-costs/{secondId}/reverse", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("landed_cost.reason_required");
        (await owner.PostErrorAsync($"/api/v1/purchasing/landed-costs/{secondId}/reverse", new { reason = "Too early" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("landed_cost.reversal_date_invalid"); // today is the 22nd
        var reversed = await owner.PostAsync($"/api/v1/purchasing/landed-costs/{secondId}/reverse", new { reason = "Insurance was already in the freight", reversalDate = "2026-09-27" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await ClearingAsync(s, secondId)).ShouldBe(0m);
        (await RoleAsync(s, "Cogs")).ShouldBe(10330m);
        (await RoleAsync(s, "Inventory")).ShouldBe(10330m);
        (await owner.GetOkAsync($"/api/v1/purchasing/landed-costs?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2);

        // A draft can be edited and deleted.
        var third = await owner.PostAsync("/api/v1/purchasing/landed-costs", new { companyId = s.CompanyId, receiptLineIds = new[] { riceReceipt }, charges = new[] { new { chargeTypeId = handling, amount = 10m } } });
        var thirdId = third.GetProperty("id").GetGuid();
        (await owner.PutAsync($"/api/v1/purchasing/landed-costs/{thirdId}", new { companyId = s.CompanyId, receiptLineIds = new[] { riceReceipt, teaReceipt }, charges = new[] { new { chargeTypeId = handling, amount = 30m } } })).GetProperty("allocations").GetArrayLength().ShouldBe(2);
        await owner.DeleteOkAsync($"/api/v1/purchasing/landed-costs/{thirdId}");
        await owner.AssertInvariantsAsync();
    }
}

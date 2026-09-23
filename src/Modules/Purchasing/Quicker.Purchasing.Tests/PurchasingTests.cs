using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Purchasing.Tests;

/// <summary>Requisition to purchase order (roadmap 4.2): requisitions become orders per supplier, orders route to approval by amount, change orders keep revisions, RFQs are compared by landed price then lead time, blanket agreements cap releases, held suppliers are refused.</summary>
[Collection(ApiCollection.Name)]
public sealed class PurchasingTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId, string Email);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Tea, Guid Coffee, Guid SupplierA, Guid SupplierB);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "PUR", legalName = Name("Purchasing Co", "شركة المشتريات"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var coffee = (await owner.PostAsync("/api/v1/items", new { code = "COFFEE", name = Name("Coffee", "قهوة"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var a = await SupplierAsync(owner, companyId, "SUP-A", "Alpha Supplies", "alpha@example.test", "IQD");
        var b = await SupplierAsync(owner, companyId, "SUP-B", "Beta Trading", "beta@example.test", "IQD");
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), tea, coffee, a, b);
    }

    private static async Task<Guid> SupplierAsync(HttpClient owner, Guid companyId, string code, string name, string? email, string currency)
    {
        var partner = await owner.PostAsync("/api/v1/partners", new { code, legalName = Name(name, name), isSupplier = true, email });
        var id = partner.GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{id}/supplier-accounts/{companyId}", new { currency, leadTimeDays = 7 });
        return id;
    }

    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid(), email);
    }

    private static object Line(Guid itemId, decimal quantity, decimal? unitPrice = null, Guid? blanketLineId = null, Guid? suggestedSupplierId = null, Guid? warehouseId = null) =>
        new { itemId, quantity, uom = "PCS", unitPrice, estimatedPrice = unitPrice, blanketLineId, suggestedSupplierId, warehouseId };

    private static object Order(Setup s, Guid partnerId, object[] lines, string? currency = null, Guid? agreementId = null) =>
        new { companyId = s.CompanyId, partnerId, currency, warehouseId = s.WarehouseId, agreementId, lines };

    [Fact]
    public async Task A_requisition_is_approved_and_becomes_one_purchase_order_per_supplier_whose_open_lines_are_incoming_supply()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        (await owner.PostErrorAsync("/api/v1/purchasing/requisitions", new { companyId = s.CompanyId, lines = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("requisition.lines_required");
        var requisition = await owner.PostAsync("/api/v1/purchasing/requisitions", new
        {
            companyId = s.CompanyId,
            neededBy = "2026-10-15",
            justification = "Restock the canteen",
            lines = new object[] { Line(s.Tea, 10m, 1500m, suggestedSupplierId: s.SupplierA, warehouseId: s.WarehouseId), Line(s.Coffee, 5m, 4000m, suggestedSupplierId: s.SupplierB, warehouseId: s.WarehouseId) },
        });
        var requisitionId = requisition.GetProperty("id").GetGuid();
        requisition.GetProperty("number").GetString().ShouldStartWith("REQ-2026-");
        requisition.GetProperty("status").GetString().ShouldBe("draft");
        requisition.GetProperty("totalEstimated").GetDecimal().ShouldBe(35000m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/requisitions/{requisitionId}/orders", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("requisition.not_approved");

        // No active workflow definition: the submission is approved at once.
        var submitted = await owner.PostAsync($"/api/v1/purchasing/requisitions/{requisitionId}/submit", new { }, HttpStatusCode.OK);
        submitted.GetProperty("status").GetString().ShouldBe("approved");
        submitted.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.String);

        var created = await owner.PostAsync($"/api/v1/purchasing/requisitions/{requisitionId}/orders", new { }, HttpStatusCode.OK);
        var orders = created.GetProperty("orders").EnumerateArray().ToList();
        orders.Count.ShouldBe(2);
        var orderA = orders.Single(o => o.GetProperty("partnerId").GetGuid() == s.SupplierA);
        var orderB = orders.Single(o => o.GetProperty("partnerId").GetGuid() == s.SupplierB);
        orderA.GetProperty("number").GetString().ShouldStartWith("PO-2026-");
        orderA.GetProperty("status").GetString().ShouldBe("draft");
        orderA.GetProperty("requisitionId").GetGuid().ShouldBe(requisitionId);
        orderA.GetProperty("currency").GetString().ShouldBe("IQD");
        orderA.GetProperty("lines").Only().GetProperty("unitPrice").GetDecimal().ShouldBe(1500m);
        orderA.GetProperty("totalNet").GetDecimal().ShouldBe(15000m);
        var ordered = await owner.GetOkAsync($"/api/v1/purchasing/requisitions/{requisitionId}");
        ordered.GetProperty("status").GetString().ShouldBe("ordered");
        ordered.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("status").GetString()).ShouldAllBe(static st => st == "ordered");
        (await owner.PostErrorAsync($"/api/v1/purchasing/requisitions/{requisitionId}/orders", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("requisition.not_approved");

        // Approving order A records its commitment on the inventory account of the month it is needed by.
        var orderAId = orderA.GetProperty("id").GetGuid();
        var approvedA = await owner.PostAsync($"/api/v1/purchasing/orders/{orderAId}/submit", new { }, HttpStatusCode.OK);
        approvedA.GetProperty("status").GetString().ShouldBe("approved");
        var commitment = approvedA.GetProperty("commitments").Only();
        commitment.GetProperty("accountRole").GetString().ShouldBe("Inventory");
        commitment.GetProperty("amountRc").GetDecimal().ShouldBe(15000m);
        commitment.GetProperty("periodKey").GetString().ShouldBe("2026-10");
        commitment.GetProperty("status").GetString().ShouldBe("open");

        // The planner sees the approved order's open line as incoming supply; the draft is not supply yet.
        var incoming = await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IIncomingSupply>().IncomingAsync(s.CompanyId, s.WarehouseId, ct));
        var supply = incoming.ShouldHaveSingleItem();
        supply.ItemId.ShouldBe(s.Tea);
        supply.Quantity.ShouldBe(10m);
        supply.SourceDocumentType.ShouldBe("purchase_order");
        supply.SourceDocumentId.ShouldBe(orderAId);

        // Cancelling order B gives its requisition line back, so it can be ordered again.
        var orderBId = orderB.GetProperty("id").GetGuid();
        var cancelled = await owner.PostAsync($"/api/v1/purchasing/orders/{orderBId}/cancel", new { message = "Wrong supplier" }, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        var reopened = await owner.GetOkAsync($"/api/v1/purchasing/requisitions/{requisitionId}");
        reopened.GetProperty("status").GetString().ShouldBe("approved");
        reopened.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Coffee).GetProperty("status").GetString().ShouldBe("open");
        var reordered = await owner.PostAsync($"/api/v1/purchasing/requisitions/{requisitionId}/orders", new { partnerId = s.SupplierA }, HttpStatusCode.OK);
        reordered.GetProperty("orders").Only().GetProperty("partnerId").GetGuid().ShouldBe(s.SupplierA);

        // Cancelling an approved order releases its commitment and it stops being supply.
        var cancelledA = await owner.PostAsync($"/api/v1/purchasing/orders/{orderAId}/cancel", new { }, HttpStatusCode.OK);
        cancelledA.GetProperty("commitments").EnumerateArray().Select(static c => c.GetProperty("status").GetString()).ShouldAllBe(static st => st == "released");
        (await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IIncomingSupply>().IncomingAsync(s.CompanyId, s.WarehouseId, ct))).ShouldBeEmpty();
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{orderAId}/cancel", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("order.not_cancellable");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_order_above_the_threshold_routes_to_finance_and_change_orders_keep_revisions_and_go_through_approval_again()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var finance = await InviteAsync(owner, s.Ws, "finance", "workflow.request.read", "purchasing.order.read");
        var definition = await owner.PostAsync("/api/v1/workflow/definitions", new
        {
            entityType = "purchase_order",
            trigger = "on_submit",
            name = Name("Purchase approval", "اعتماد الشراء"),
            rules = new[] { new { name = Name("Large orders", "الأوامر الكبيرة"), condition = "amount_in('IQD') > 10000", steps = new[] { new { name = Name("Finance", "المالية"), approverKind = "users", approvers = new { membershipIds = new[] { finance.MembershipId } }, mode = "any" } } } },
        });
        await owner.PostAsync($"/api/v1/workflow/definitions/{definition.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);

        // Below the threshold: approved at once.
        var small = await owner.PostAsync("/api/v1/purchasing/orders", Order(s, s.SupplierA, [Line(s.Tea, 2m, 1500m)]));
        (await owner.PostAsync($"/api/v1/purchasing/orders/{small.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");

        // Above it: pending until finance approves.
        var order = await owner.PostAsync("/api/v1/purchasing/orders", Order(s, s.SupplierA, [Line(s.Tea, 10m, 1500m)]));
        var orderId = order.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{orderId}/send", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("order.not_approved");
        var pending = await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        pending.GetProperty("status").GetString().ShouldBe("pending_approval");
        var requestId = pending.GetProperty("approvalRequestId").GetGuid();
        pending.GetProperty("commitments").GetArrayLength().ShouldBe(0);
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("order.not_draft");

        await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Within budget." }, HttpStatusCode.OK);
        var approved = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        approved.GetProperty("status").GetString().ShouldBe("approved");
        approved.GetProperty("revision").GetInt32().ShouldBe(1);
        approved.GetProperty("commitments").Only().GetProperty("amountRc").GetDecimal().ShouldBe(15000m);

        // A change order keeps revision 1 as a snapshot, re-prices, and goes through approval again.
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{orderId}/change", new { order = Order(s, s.SupplierA, [Line(s.Tea, 12m, 1500m)]), reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("order.change_reason_required");
        var changed = await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/change", new { order = Order(s, s.SupplierA, [Line(s.Tea, 12m, 1500m)]), reason = "Supplier can ship two more" }, HttpStatusCode.OK);
        changed.GetProperty("revision").GetInt32().ShouldBe(2);
        changed.GetProperty("status").GetString().ShouldBe("pending_approval");
        changed.GetProperty("totalNet").GetDecimal().ShouldBe(18000m);
        changed.GetProperty("commitments").GetArrayLength().ShouldBe(0);
        var revision = changed.GetProperty("revisions").Only();
        revision.GetProperty("revision").GetInt32().ShouldBe(1);
        revision.GetProperty("reason").GetString().ShouldBe("Supplier can ship two more");
        revision.GetProperty("snapshot").GetProperty("totalNet").GetDecimal().ShouldBe(15000m);
        var secondRequest = changed.GetProperty("approvalRequestId").GetGuid();
        secondRequest.ShouldNotBe(requestId);
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{secondRequest}/approve", new { }, HttpStatusCode.OK);
        var reapproved = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        reapproved.GetProperty("status").GetString().ShouldBe("approved");
        reapproved.GetProperty("commitments").Only().GetProperty("amountRc").GetDecimal().ShouldBe(18000m);

        // Sending emails the supplier a bilingual order and marks it sent.
        var sent = await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/send", new { message = "Please confirm by Thursday." }, HttpStatusCode.OK);
        sent.GetProperty("status").GetString().ShouldBe("sent");
        sent.GetProperty("sentTo").GetString().ShouldBe("alpha@example.test");
        var mail = Api.Emails.LastTo("alpha@example.test").ShouldNotBeNull();
        mail.Subject.ShouldContain(sent.GetProperty("number").GetString()!);
        mail.HtmlBody.ShouldNotBeNull().ShouldContain("TEA");
        mail.HtmlBody.ShouldContain("أمر شراء");
        mail.HtmlBody.ShouldContain("Please confirm by Thursday.");
        mail.TextBody.ShouldContain("Total: 18000 IQD");

        // A rejection keeps the order editable with the reason.
        var other = await owner.PostAsync("/api/v1/purchasing/orders", Order(s, s.SupplierB, [Line(s.Coffee, 5m, 4000m)]));
        var otherId = other.GetProperty("id").GetGuid();
        var otherRequest = (await owner.PostAsync($"/api/v1/purchasing/orders/{otherId}/submit", new { }, HttpStatusCode.OK)).GetProperty("approvalRequestId").GetGuid();
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{otherRequest}/reject", new { comment = "Not this quarter." }, HttpStatusCode.OK);
        var rejected = await owner.GetOkAsync($"/api/v1/purchasing/orders/{otherId}");
        rejected.GetProperty("status").GetString().ShouldBe("rejected");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Not this quarter.");
        (await owner.PutAsync($"/api/v1/purchasing/orders/{otherId}", Order(s, s.SupplierB, [Line(s.Coffee, 2m, 4000m)]))).GetProperty("status").GetString().ShouldBe("draft");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_rfq_is_sent_to_invited_suppliers_and_quotes_rank_by_landed_price_in_the_company_currency_then_lead_time()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var supplierC = await SupplierAsync(owner, s.CompanyId, "SUP-C", "Gamma Imports", "gamma@example.test", "USD");
        var silent = await SupplierAsync(owner, s.CompanyId, "SUP-Q", "Quiet Co", null, "IQD");

        var rfq = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId = s.CompanyId, title = "Tea for Q4", dueOn = "2026-10-01", lines = new[] { new { itemId = s.Tea, quantity = 10m, uom = "PCS" } }, partnerIds = new[] { s.SupplierA, s.SupplierB } });
        var rfqId = rfq.GetProperty("id").GetGuid();
        rfq.GetProperty("number").GetString().ShouldStartWith("RFQ-2026-");
        rfq.GetProperty("suppliers").GetArrayLength().ShouldBe(2);
        var rfqLineId = rfq.GetProperty("lines").Only().GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/invite", new { partnerIds = new[] { supplierC, silent } }, HttpStatusCode.OK);
        (await owner.PostErrorAsync($"/api/v1/purchasing/rfqs/{rfqId}/send", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("rfq.supplier_no_email");
        var sent = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/send", new { emails = new Dictionary<Guid, string> { [silent] = "quiet@example.test" } }, HttpStatusCode.OK);
        sent.GetProperty("status").GetString().ShouldBe("sent");
        sent.GetProperty("suppliers").EnumerateArray().Select(static x => x.GetProperty("sentAt").ValueKind).ShouldAllBe(static k => k == JsonValueKind.String);
        Api.Emails.LastTo("beta@example.test").ShouldNotBeNull().Subject.ShouldContain(rfq.GetProperty("number").GetString()!);
        Api.Emails.LastTo("quiet@example.test").ShouldNotBeNull();

        // Quotes: A in IQD, B and C in USD. Landed: A 14,000; B 10.5 USD = 13,650; C 10.5 USD = 13,650 but faster.
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = s.SupplierA, currency = "IQD", leadTimeDays = 5, lines = new[] { new { rfqLineId, unitPrice = 1400m } } }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = s.SupplierB, currency = "USD", leadTimeDays = 7, freightAmount = 0.5m, lines = new[] { new { rfqLineId, unitPrice = 1m } } }, HttpStatusCode.OK);
        var quoted = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = supplierC, currency = "USD", leadTimeDays = 3, lines = new[] { new { rfqLineId, unitPrice = 1.05m } } }, HttpStatusCode.OK);
        (await owner.PostErrorAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = Guid.CreateVersion7(), currency = "USD", lines = new[] { new { rfqLineId, unitPrice = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("rfq.supplier_not_invited");
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/suppliers/{silent}/decline", new { }, HttpStatusCode.OK);

        var comparison = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/compare", new { }, HttpStatusCode.OK);
        comparison.GetProperty("currency").GetString().ShouldBe("IQD");
        var rankings = comparison.GetProperty("rankings").EnumerateArray().ToList();
        rankings.Select(static r => r.GetProperty("partnerCode").GetString()).ShouldBe(["SUP-C", "SUP-B", "SUP-A"]);
        rankings[0].GetProperty("rank").GetInt32().ShouldBe(1);
        rankings[0].GetProperty("landedTotalRc").GetDecimal().ShouldBe(13650m);
        rankings[0].GetProperty("exchangeRate").GetDecimal().ShouldBe(1300m);
        rankings[1].GetProperty("landedTotalRc").GetDecimal().ShouldBe(13650m);
        rankings[1].GetProperty("leadTimeDays").GetInt32().ShouldBe(7);
        rankings[2].GetProperty("landedTotalRc").GetDecimal().ShouldBe(14000m);

        var winner = quoted.GetProperty("quotes").EnumerateArray().Single(q => q.GetProperty("partnerId").GetGuid() == supplierC).GetProperty("id").GetGuid();
        var order = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/award", new { quoteId = winner, warehouseId = s.WarehouseId }, HttpStatusCode.OK);
        order.GetProperty("status").GetString().ShouldBe("draft");
        order.GetProperty("rfqId").GetGuid().ShouldBe(rfqId);
        order.GetProperty("partnerId").GetGuid().ShouldBe(supplierC);
        order.GetProperty("currency").GetString().ShouldBe("USD");
        order.GetProperty("exchangeRate").GetDecimal().ShouldBe(1300m);
        order.GetProperty("lines").Only().GetProperty("unitPrice").GetDecimal().ShouldBe(1.05m);
        order.GetProperty("lines").Only().GetProperty("expectedDate").GetString().ShouldBe("2026-09-25");
        var awarded = await owner.GetOkAsync($"/api/v1/purchasing/rfqs/{rfqId}");
        awarded.GetProperty("status").GetString().ShouldBe("awarded");
        awarded.GetProperty("awardedQuoteId").GetGuid().ShouldBe(winner);
        (await owner.PostErrorAsync($"/api/v1/purchasing/rfqs/{rfqId}/award", new { quoteId = winner }, HttpStatusCode.Conflict)).Code.ShouldBe("rfq.not_awardable");
    }

    [Fact]
    public async Task A_blanket_agreement_caps_what_orders_release_and_a_supplier_on_purchase_hold_is_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        (await owner.PostErrorAsync("/api/v1/purchasing/agreements", new { companyId = s.CompanyId, partnerId = s.SupplierA, validFrom = "2026-12-31", validTo = "2026-01-01", lines = new[] { new { itemId = s.Tea, agreedQty = 100m, uom = "PCS", agreedPrice = 900m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("agreement.period_invalid");
        var agreement = await owner.PostAsync("/api/v1/purchasing/agreements", new { companyId = s.CompanyId, partnerId = s.SupplierA, validFrom = "2026-01-01", validTo = "2026-12-31", lines = new[] { new { itemId = s.Tea, agreedQty = 100m, uom = "PCS", agreedPrice = 900m } } });
        var agreementId = agreement.GetProperty("id").GetGuid();
        agreement.GetProperty("number").GetString().ShouldStartWith("BPA-2026-");
        var blanketLine = agreement.GetProperty("lines").Only().GetProperty("id").GetGuid();

        // Only an active agreement is released against.
        var draftRelease = await owner.PostAsync("/api/v1/purchasing/orders", Order(s, s.SupplierA, [Line(s.Tea, 60m, 900m, blanketLine)], agreementId: agreementId));
        (await owner.PostErrorAsync($"/api/v1/purchasing/orders/{draftRelease.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("agreement.not_active");
        (await owner.PostAsync($"/api/v1/purchasing/agreements/{agreementId}/activate", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("active");
        var first = await owner.PostAsync($"/api/v1/purchasing/orders/{draftRelease.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        first.GetProperty("status").GetString().ShouldBe("approved");
        var released = await owner.GetOkAsync($"/api/v1/purchasing/agreements/{agreementId}");
        released.GetProperty("lines").Only().GetProperty("releasedQty").GetDecimal().ShouldBe(60m);
        released.GetProperty("lines").Only().GetProperty("remainingQty").GetDecimal().ShouldBe(40m);
        released.GetProperty("releasedAmount").GetDecimal().ShouldBe(54000m);

        // 50 more would exceed the agreed 100.
        var over = await owner.PostAsync("/api/v1/purchasing/orders", Order(s, s.SupplierA, [Line(s.Tea, 50m, 900m, blanketLine)], agreementId: agreementId));
        var overId = over.GetProperty("id").GetGuid();
        var refused = await owner.PostErrorAsync($"/api/v1/purchasing/orders/{overId}/submit", new { }, HttpStatusCode.Conflict);
        refused.Code.ShouldBe("agreement.over_release");
        refused.Problem.GetProperty("why").GetProperty("releasedQty").GetDecimal().ShouldBe(60m);
        (await owner.PostErrorAsync("/api/v1/purchasing/orders", Order(s, s.SupplierB, [Line(s.Tea, 1m, 900m, blanketLine)], agreementId: agreementId), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("order.agreement_invalid");

        // Cancelling the first order gives its release back.
        await owner.PostAsync($"/api/v1/purchasing/orders/{first.GetProperty("id").GetGuid()}/cancel", new { }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/purchasing/agreements/{agreementId}")).GetProperty("lines").Only().GetProperty("releasedQty").GetDecimal().ShouldBe(0m);

        // A supplier on purchase hold cannot be ordered from until released.
        await owner.PostAsync($"/api/v1/partners/{s.SupplierA}/supplier-accounts/{s.CompanyId}/hold", new { status = "purchase", reason = "Quality claim open" }, HttpStatusCode.OK);
        var held = await owner.PostErrorAsync($"/api/v1/purchasing/orders/{overId}/submit", new { }, HttpStatusCode.Conflict);
        held.Code.ShouldBe("supplier.on_hold");
        held.Problem.GetProperty("why").GetProperty("reason").GetString().ShouldBe("Quality claim open");
        await owner.PostAsync($"/api/v1/partners/{s.SupplierA}/supplier-accounts/{s.CompanyId}/release", new { }, HttpStatusCode.OK);
        (await owner.PostAsync($"/api/v1/purchasing/orders/{overId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        (await owner.GetOkAsync($"/api/v1/purchasing/agreements/{agreementId}")).GetProperty("lines").Only().GetProperty("releasedQty").GetDecimal().ShouldBe(50m);

        await owner.AssertInvariantsAsync();
    }
}

using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Sales orders (roadmap 5.4b): converted from an accepted quotation or created directly, reserving stock on
/// confirmation (a short line backorders rather than blocking the order) and checking the customer's credit exposure
/// -- hard scenario 6 (a customer over their credit limit is held, released by an authorized approver, the override
/// logged) and the roadmap's own concurrency acceptance (confirming two orders of the same customer at once can
/// never both pass an exposure only one of them should).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OrderTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Tea, Guid Warehouse, Guid Customer);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRD", legalName = Name("Trading Co", "شركة التجارة"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BAGHDAD-MALL", legalName = Name("Baghdad Mall LLC", "بغداد مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        return new Setup(ws, owner, company, tea, warehouse, customer);
    }

    private static async Task StockAsync(HttpClient owner, Guid companyId, Guid warehouseId, Guid itemId, decimal quantity)
    {
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId, kind = "opening", lines = new object[] { new { itemId, quantity, unitCost = 50m, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
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

    [Fact]
    public async Task Converting_an_accepted_quotation_carries_its_frozen_lines_and_confirming_reserves_stock()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 20m);

        var quotation = await owner.PostAsync("/api/v1/sales/quotations", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { itemId = s.Tea, quantity = 3m } } });
        var quotationId = quotation.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/quotations/{quotationId}/send", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/sales/quotations/{quotationId}/accept", new { }, HttpStatusCode.OK);

        var order = await owner.PostAsync($"/api/v1/sales/quotations/{quotationId}/convert", new { warehouseId = s.Warehouse });
        order.GetProperty("status").GetString().ShouldBe("draft");
        order.GetProperty("quotationId").GetGuid().ShouldBe(quotationId);
        order.GetProperty("totalGross").GetDecimal().ShouldBe(300m);
        var orderId = order.GetProperty("id").GetGuid();

        var quotationAfter = await owner.GetOkAsync($"/api/v1/sales/quotations/{quotationId}");
        quotationAfter.GetProperty("status").GetString().ShouldBe("converted");
        quotationAfter.GetProperty("orderId").GetGuid().ShouldBe(orderId);

        var confirmed = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        confirmed.GetProperty("status").GetString().ShouldBe("confirmed");
        var line = confirmed.GetProperty("lines")[0];
        line.GetProperty("status").GetString().ShouldBe("open");
        line.GetProperty("qtyReserved").GetDecimal().ShouldBe(3m);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_line_short_of_stock_backorders_and_a_retry_fulfills_it_once_stock_arrives()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 4m);

        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 10m } } });
        var orderId = order.GetProperty("id").GetGuid();

        var confirmed = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        confirmed.GetProperty("status").GetString().ShouldBe("confirmed");
        var line = confirmed.GetProperty("lines")[0];
        line.GetProperty("status").GetString().ShouldBe("backordered");
        line.GetProperty("qtyReserved").GetDecimal().ShouldBe(4m);

        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 10m);
        var retried = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/retry-backorders", new { }, HttpStatusCode.OK);
        var retriedLine = retried.GetProperty("lines")[0];
        retriedLine.GetProperty("status").GetString().ShouldBe("open");
        retriedLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(10m);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Cancelling_part_of_a_confirmed_line_releases_its_reservation_and_the_whole_order_cancels_the_rest()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 10m);

        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 10m } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);

        var partial = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/lines/{lineId}/cancel", new { quantity = 4m }, HttpStatusCode.OK);
        var partialLine = partial.GetProperty("lines")[0];
        partialLine.GetProperty("qtyCancelled").GetDecimal().ShouldBe(4m);
        partialLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(6m);
        partialLine.GetProperty("status").GetString().ShouldBe("open");

        var (code, _) = await owner.PostErrorAsync($"/api/v1/sales/orders/{orderId}/lines/{lineId}/cancel", new { quantity = 100m }, HttpStatusCode.Conflict);
        code.ShouldBe("order.cancel_exceeds_remaining");

        var cancelled = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/cancel", new { reason = "Customer changed their mind" }, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        cancelled.GetProperty("lines")[0].GetProperty("status").GetString().ShouldBe("cancelled");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_drop_ship_line_skips_reservation_and_takes_a_purchase_order_line_once()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        // No stock at all: a drop-ship line never touches the warehouse, so confirmation still succeeds.
        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 5m, dropShip = true } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        order.GetProperty("lines")[0].GetProperty("dropShip").GetBoolean().ShouldBeTrue();

        var confirmed = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        confirmed.GetProperty("status").GetString().ShouldBe("confirmed");
        var line = confirmed.GetProperty("lines")[0];
        line.GetProperty("status").GetString().ShouldBe("open");
        line.GetProperty("qtyReserved").GetDecimal().ShouldBe(0m);

        var purchaseOrderLineId = Guid.CreateVersion7();
        var linked = await owner.PostAsync($"/api/v1/sales/orders/{orderId}/lines/{lineId}/purchase-order", new { purchaseOrderLineId }, HttpStatusCode.OK);
        linked.GetProperty("lines")[0].GetProperty("purchaseOrderLineId").GetGuid().ShouldBe(purchaseOrderLineId);

        var (code, _) = await owner.PostErrorAsync($"/api/v1/sales/orders/{orderId}/lines/{lineId}/purchase-order", new { purchaseOrderLineId = Guid.CreateVersion7() }, HttpStatusCode.Conflict);
        code.ShouldBe("order.line_already_linked");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_customer_over_their_credit_limit_is_held_and_released_only_by_an_authorized_overrides_approval()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/customer-accounts/{s.CompanyId}", new { currency = "USD", creditLimit = 150m });

        async Task<Guid> NewOrderAsync()
        {
            var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } });
            return order.GetProperty("id").GetGuid();
        }

        var orderA = await NewOrderAsync();
        (await owner.PostAsync($"/api/v1/sales/orders/{orderA}/confirm", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("confirmed");

        var orderB = await NewOrderAsync();
        var held = await owner.PostAsync($"/api/v1/sales/orders/{orderB}/confirm", new { }, HttpStatusCode.OK);
        held.GetProperty("status").GetString().ShouldBe("on_hold");
        held.GetProperty("blockKind").GetString().ShouldBe("credit_limit");

        var approver = await InviteAsync(owner, s.Ws, "credit_approver", "workflow.request.read", "sales.order.read");
        var definition = await owner.PostAsync("/api/v1/workflow/definitions", new
        {
            entityType = "sales_order",
            trigger = "on_block",
            blockKind = "credit_limit",
            name = Name("Credit limit overrides", "تجاوز حد الائتمان"),
            overrideValidHours = 48,
            rules = new[] { new { name = Name("Any excess", "أي تجاوز"), condition = "exposure > 0", steps = new[] { new { name = Name("Credit approver", "معتمد الائتمان"), approverKind = "users", approvers = new { membershipIds = new[] { approver.MembershipId } }, mode = "any", requireComment = true } } } },
        });
        await owner.PostAsync($"/api/v1/workflow/definitions/{definition.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);

        var pending = await owner.PostAsync($"/api/v1/sales/orders/{orderB}/confirm", new { }, HttpStatusCode.OK);
        pending.GetProperty("status").GetString().ShouldBe("on_hold");
        var blocks = await owner.GetOkAsync($"/api/v1/workflow/blocks?entityType=sales_order&entityId={orderB}");
        var requestId = blocks.EnumerateArray().Single(static b => b.GetProperty("status").GetString() == "pending").GetProperty("requestId").GetGuid();
        await approver.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Known good customer; approved." }, HttpStatusCode.OK);

        var released = await owner.PostAsync($"/api/v1/sales/orders/{orderB}/confirm", new { }, HttpStatusCode.OK);
        released.GetProperty("status").GetString().ShouldBe("confirmed");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Two_orders_of_the_same_customer_confirmed_at_once_never_both_pass_a_limit_only_one_should()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/customer-accounts/{s.CompanyId}", new { currency = "USD", creditLimit = 150m });

        var orderC = (await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } })).GetProperty("id").GetGuid();
        var orderD = (await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } })).GetProperty("id").GetGuid();

        var confirmC = owner.PostAsync($"/api/v1/sales/orders/{orderC}/confirm", new { }, HttpStatusCode.OK);
        var confirmD = owner.PostAsync($"/api/v1/sales/orders/{orderD}/confirm", new { }, HttpStatusCode.OK);
        var results = await Task.WhenAll(confirmC, confirmD);

        var statuses = results.Select(static r => r.GetProperty("status").GetString()).OrderBy(static x => x, StringComparer.Ordinal).ToList();
        statuses.ShouldBe(["confirmed", "on_hold"]);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_member_with_only_order_read_lists_and_reads_but_may_not_create_or_confirm()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 5m);
        await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } });

        var reader = await InviteAsync(owner, s.Ws, "order_reader", "sales.order.read");
        (await reader.Client.GetOkAsync("/api/v1/sales/orders")).GetArrayLength().ShouldBe(1);
        var (code, _) = await reader.Client.PostErrorAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } }, HttpStatusCode.Forbidden);
        code.ShouldNotBeNull();
    }
}

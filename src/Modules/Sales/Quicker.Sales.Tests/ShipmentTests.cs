using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Sales shipments (roadmap 5.5a): a confirmed order's reserved lines ship in full or in part through the stock
/// engine as a sale shipment, which consumes exactly the reservation it ships and lets the costing engine (roadmap
/// 3.3) value and book cost of goods sold -- nothing new there, only this document. A partial shipment leaves the
/// remainder reserved for a later one, a drop-ship line has nothing to pick, and a reversal returns the stock and
/// re-reserves it for the order.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShipmentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Tea, Guid Warehouse, Guid Customer);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "SHP", legalName = Name("Shipping Co", "شركة الشحن"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BAGHDAD-MALL", legalName = Name("Baghdad Mall LLC", "بغداد مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        return new Setup(ws, owner, company, tea, warehouse, customer);
    }

    private static async Task StockAsync(HttpClient owner, Guid companyId, Guid warehouseId, Guid itemId, decimal quantity, decimal unitCost = 50m)
    {
        var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId, kind = "opening", lines = new object[] { new { itemId, quantity, unitCost, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
    }

    private static async Task<decimal> OnHandAsync(Setup s, Guid itemId)
    {
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={itemId}&warehouseId={s.Warehouse}");
        return balances.EnumerateArray().Sum(static b => b.GetProperty("onHand").GetDecimal());
    }

    private static async Task<(Guid OrderId, Guid LineId)> ConfirmedOrderAsync(Setup s, decimal quantity)
    {
        var order = await s.Owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        return (orderId, lineId);
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
    public async Task Posting_a_full_shipment_consumes_the_reservation_moves_the_stock_and_books_cost_of_goods_sold_and_fulfils_the_order()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 10m, unitCost: 40m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, 10m);

        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 10m } } });
        draft.GetProperty("status").GetString().ShouldBe("draft");
        var shipmentId = draft.GetProperty("id").GetGuid();

        var posted = await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        var postedLine = posted.GetProperty("lines")[0];
        postedLine.GetProperty("cogsAmount").GetDecimal().ShouldBe(400m);
        posted.GetProperty("totalCogs").GetDecimal().ShouldBe(400m);

        (await OnHandAsync(s, s.Tea)).ShouldBe(0m);

        var order = await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}");
        order.GetProperty("status").GetString().ShouldBe("shipped");
        var orderLine = order.GetProperty("lines")[0];
        orderLine.GetProperty("status").GetString().ShouldBe("fulfilled");
        orderLine.GetProperty("qtyShipped").GetDecimal().ShouldBe(10m);
        orderLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(0m);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_partial_shipment_leaves_the_remainder_reserved_and_a_second_shipment_completes_the_order()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 10m, unitCost: 40m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, 10m);

        var firstDraft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 6m } } });
        var firstPosted = await owner.PostAsync($"/api/v1/sales/shipments/{firstDraft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        firstPosted.GetProperty("lines")[0].GetProperty("cogsAmount").GetDecimal().ShouldBe(240m);

        var afterFirst = await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}");
        afterFirst.GetProperty("status").GetString().ShouldBe("partially_shipped");
        var afterFirstLine = afterFirst.GetProperty("lines")[0];
        afterFirstLine.GetProperty("status").GetString().ShouldBe("open");
        afterFirstLine.GetProperty("qtyShipped").GetDecimal().ShouldBe(6m);
        afterFirstLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(4m);

        (await OnHandAsync(s, s.Tea)).ShouldBe(4m);

        var (tooMuchCode, _) = await owner.PostErrorAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 5m } } }, HttpStatusCode.Conflict);
        tooMuchCode.ShouldBe("shipment.exceeds_reserved");

        var secondDraft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 4m } } });
        await owner.PostAsync($"/api/v1/sales/shipments/{secondDraft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);

        var afterSecond = await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}");
        afterSecond.GetProperty("status").GetString().ShouldBe("shipped");
        afterSecond.GetProperty("lines")[0].GetProperty("status").GetString().ShouldBe("fulfilled");

        (await OnHandAsync(s, s.Tea)).ShouldBe(0m);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_drop_ship_line_is_refused_and_so_is_an_order_that_is_not_yet_confirmed()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 5m);

        var draftOrder = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 5m } } });
        var draftOrderId = draftOrder.GetProperty("id").GetGuid();
        var draftLineId = draftOrder.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var (notShippableCode, _) = await owner.PostErrorAsync("/api/v1/sales/shipments", new { orderId = draftOrderId, lines = new object[] { new { orderLineId = draftLineId, quantity = 5m } } }, HttpStatusCode.Conflict);
        notShippableCode.ShouldBe("shipment.order_not_shippable");

        var dropShipOrder = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = s.Tea, quantity = 3m, dropShip = true } } });
        var dropShipOrderId = dropShipOrder.GetProperty("id").GetGuid();
        var dropShipLineId = dropShipOrder.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/orders/{dropShipOrderId}/confirm", new { }, HttpStatusCode.OK);
        var (dropShipCode, _) = await owner.PostErrorAsync("/api/v1/sales/shipments", new { orderId = dropShipOrderId, lines = new object[] { new { orderLineId = dropShipLineId, quantity = 3m } } }, HttpStatusCode.Conflict);
        dropShipCode.ShouldBe("shipment.line_is_drop_ship");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Reversing_a_posted_shipment_returns_the_stock_at_its_exact_cost_and_re_reserves_it_for_the_order()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 8m, unitCost: 40m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, 8m);

        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 8m } } });
        var shipmentId = draft.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/post", new { }, HttpStatusCode.OK);
        (await OnHandAsync(s, s.Tea)).ShouldBe(0m);

        (await owner.PostErrorAsync($"/api/v1/sales/shipments/{shipmentId}/reverse", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.reason_required");

        var reversed = await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/reverse", new { reason = "Customer refused delivery" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("reversalReason").GetString().ShouldBe("Customer refused delivery");

        (await OnHandAsync(s, s.Tea)).ShouldBe(8m);

        var order = await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}");
        order.GetProperty("status").GetString().ShouldBe("confirmed");
        var orderLine = order.GetProperty("lines")[0];
        orderLine.GetProperty("qtyShipped").GetDecimal().ShouldBe(0m);
        orderLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(8m);
        orderLine.GetProperty("status").GetString().ShouldBe("open");

        (await owner.PostErrorAsync($"/api/v1/sales/shipments/{shipmentId}/reverse", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.not_posted");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_member_with_only_shipment_read_lists_and_reads_but_may_not_create_or_post()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 5m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, 5m);
        await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 5m } } });

        var reader = await InviteAsync(owner, s.Ws, "shipment_reader", "sales.shipment.read");
        (await reader.Client.GetOkAsync("/api/v1/sales/shipments")).GetArrayLength().ShouldBe(1);
        var (code, _) = await reader.Client.PostErrorAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 5m } } }, HttpStatusCode.Forbidden);
        code.ShouldNotBeNull();
    }

    /// <summary>Roadmap 5.5's own acceptance criterion (hard scenario 4 on the sales path, 200 parallel rounds):
    /// the product brief's scenario is two users selling the last unit at the same moment, so this repeats that
    /// exact race two hundred times over -- a fresh single unit of stock and two draft shipments contending for its
    /// one reservation, posted at the same moment -- rather than needing two hundred simultaneous contenders in one
    /// round, which would only test the sandbox's connection budget rather than the engine's correctness. Every
    /// round leaves exactly one shipment posted, one refused, and the balance back at zero.</summary>
    [Fact]
    public async Task Two_users_race_the_last_unit_two_hundred_times_over_and_exactly_one_wins_every_round()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var wins = 0;
        var losses = 0;
        for (var round = 0; round < 200; round++)
        {
            await StockAsync(owner, s.CompanyId, s.Warehouse, s.Tea, 1m);
            var (orderId, lineId) = await ConfirmedOrderAsync(s, 1m);
            var firstShipment = (await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 1m } } })).GetProperty("id").GetGuid();
            var secondShipment = (await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 1m } } })).GetProperty("id").GetGuid();

            var posts = new[] { firstShipment, secondShipment }.Select(async id =>
            {
                using var client = Api.ClientFor(s.Ws.AccessToken);
                var response = await client.PostAsJsonAsync($"/api/v1/sales/shipments/{id}/post", new { }, ApiFixture.Json);
                return response.StatusCode;
            });
            var outcomes = await Task.WhenAll(posts);
            var roundWins = outcomes.Count(static c => c == HttpStatusCode.OK);
            roundWins.ShouldBe(1, $"round {round}: exactly one of the two racing shipments posts the last unit");
            wins += roundWins;
            losses += outcomes.Length - roundWins;

            (await OnHandAsync(s, s.Tea)).ShouldBe(0m, $"round {round}: the one unit ships and nothing is left over");
        }

        wins.ShouldBe(200);
        losses.ShouldBe(200);
        await owner.AssertInvariantsAsync();
    }

    /// <summary>Roadmap 5.5b: a lot-tracked item that uses FEFO ships from the earliest-expiring lot first, even one
    /// received after a later-expiring lot (splitting across lots, and lot-and-serial items: <see cref="PickingTests"/>).</summary>
    [Fact]
    public async Task A_lot_tracked_item_ships_from_the_earliest_expiring_lot_that_covers_the_quantity()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var milk = (await owner.PostAsync("/api/v1/items", new { code = "MILK", name = Name("Milk 1L", "حليب ١ لتر"), baseUom = "PCS", tracking = "lot", expiryRequired = true, fefo = true, listPrice = 5m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();

        async Task<Guid> AdjustAsync(string lotNumber, DateOnly expiresOn, decimal quantity)
        {
            var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = milk, quantity, unitCost = 10m, reasonCode = "FOUND", lotNumber, expiresOn = expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } } });
            await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
            return adjustment.GetProperty("id").GetGuid();
        }

        // The newer lot has plenty of stock but expires later; the older one, expiring sooner, has just enough.
        await AdjustAsync("LOT-NEW", new DateOnly(2026, 12, 1), 20m);
        await AdjustAsync("LOT-OLD", new DateOnly(2026, 10, 1), 5m);

        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = milk, quantity = 5m } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);

        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 5m } } });
        draft.GetProperty("lines")[0].GetProperty("lotNumber").GetString().ShouldBe("LOT-OLD");
        var posted = await owner.PostAsync($"/api/v1/sales/shipments/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("lines")[0].GetProperty("lotNumber").GetString().ShouldBe("LOT-OLD");
        posted.GetProperty("lines")[0].GetProperty("cogsAmount").GetDecimal().ShouldBe(50m);

        var lots = await owner.GetOkAsync($"/api/v1/inventory/lots?itemId={milk}");
        var oldLot = lots.EnumerateArray().Single(l => l.GetProperty("lotNumber").GetString() == "LOT-OLD");
        // LOT-OLD's own 5 units are gone; LOT-NEW's 20 are untouched.
        (await owner.GetOkAsync($"/api/v1/inventory/lots/{oldLot.GetProperty("id").GetGuid()}/trace")).GetProperty("onHand").GetArrayLength().ShouldBe(0);

        await owner.AssertInvariantsAsync();
    }

    /// <summary>Roadmap 5.5b (part 2): a serial-tracked item ships the item's own on-hand serials in the line's
    /// warehouse, oldest received first, refuses a quantity with too few serials on hand, and a reversal returns
    /// the exact serials shipped back to stock.</summary>
    [Fact]
    public async Task A_serial_tracked_item_ships_its_oldest_on_hand_serials_and_a_reversal_returns_them()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var phone = (await owner.PostAsync("/api/v1/items", new { code = "PHONE", name = Name("Phone", "هاتف"), baseUom = "PCS", tracking = "serial", listPrice = 500m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();

        async Task<Guid> AdjustAsync(IReadOnlyList<string> serialNumbers)
        {
            var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = phone, quantity = (decimal)serialNumbers.Count, unitCost = 200m, reasonCode = "FOUND", serialNumbers } } });
            var id = adjustment.GetProperty("id").GetGuid();
            await owner.PostAsync($"/api/v1/inventory/adjustments/{id}/submit", new { }, HttpStatusCode.OK);
            return id;
        }

        // Two separate receipts, so the earlier one's serials are the ones a FIFO pick should choose.
        await AdjustAsync(["SN-OLD-1", "SN-OLD-2"]);
        await AdjustAsync(["SN-NEW-1", "SN-NEW-2", "SN-NEW-3"]);

        var order = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = phone, quantity = 2m } } });
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);

        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 2m } } });
        var draftSerials = draft.GetProperty("lines")[0].GetProperty("serialNumbers").EnumerateArray().Select(static e => e.GetString()).ToList();
        draftSerials.ShouldBe(["SN-OLD-1", "SN-OLD-2"]);

        var posted = await owner.PostAsync($"/api/v1/sales/shipments/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var postedLine = posted.GetProperty("lines")[0];
        postedLine.GetProperty("serialNumbers").EnumerateArray().Select(static e => e.GetString()).ToList().ShouldBe(["SN-OLD-1", "SN-OLD-2"]);
        postedLine.GetProperty("cogsAmount").GetDecimal().ShouldBe(400m);

        var soldSerial = await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={phone}&serialNumber=SN-OLD-1");
        soldSerial.GetProperty("status").GetString().ShouldBe("sold");

        // The three remaining serials cover a fresh order of 3 -- reservation is a quantity, blind to which units --
        // but one of them goes for repair before shipping, so only two are actually in stock to pick from.
        var order2 = await owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId = phone, quantity = 3m } } });
        var order2Id = order2.GetProperty("id").GetGuid();
        var line2Id = order2.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/orders/{order2Id}/confirm", new { }, HttpStatusCode.OK);
        var newSerial1Id = (await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={phone}&serialNumber=SN-NEW-1")).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/inventory/serials/{newSerial1Id}/status", new { status = "in_repair" }, HttpStatusCode.OK);
        var (shortCode, _) = await owner.PostErrorAsync("/api/v1/sales/shipments", new { orderId = order2Id, lines = new object[] { new { orderLineId = line2Id, quantity = 3m } } }, HttpStatusCode.Conflict);
        shortCode.ShouldBe("shipment.not_enough_serials_on_hand");
        await owner.PostAsync($"/api/v1/inventory/serials/{newSerial1Id}/status", new { status = "in_stock" }, HttpStatusCode.OK);

        var reversed = await owner.PostAsync($"/api/v1/sales/shipments/{draft.GetProperty("id").GetGuid()}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        var returnedSerial = await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={phone}&serialNumber=SN-OLD-1");
        returnedSerial.GetProperty("status").GetString().ShouldBe("returned");

        await owner.AssertInvariantsAsync();
    }
}

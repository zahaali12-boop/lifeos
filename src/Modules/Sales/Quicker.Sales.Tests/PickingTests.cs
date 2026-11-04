using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Pick, pack, ship (roadmap 5.5b part 3, A-154): a sales order in a warehouse with bins reserves at warehouse level,
/// its shipment is located bin by bin in the bins' pick sequence (lots first expiry first out), released to the
/// warehouse as a pick list a picker works through on a handheld, short picks and all, and posting ships exactly what
/// was picked. Two pick lists never plan the same shelf, packages record what went into which box, and the carrier's
/// details can follow after the goods have left.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PickingTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Tea, Guid Warehouse, Guid Customer);

    private async Task<Setup> SetUpAsync(bool bins)
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "PCK", legalName = Name("Picking Co", "شركة الانتقاء"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "USD", weightKg = 0.5m })).GetProperty("id").GetGuid();
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = company, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي"), binsEnabled = bins })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BAGHDAD-MALL", legalName = Name("Baghdad Mall LLC", "بغداد مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        return new Setup(ws, owner, company, tea, warehouse, customer);
    }

    private static async Task<Guid> BinAsync(Setup s, string code, int pickSequence, string kind = "storage") =>
        (await s.Owner.PostAsync($"/api/v1/inventory/warehouses/{s.Warehouse}/bins", new { code, zone = code[..1], kind, pickSequence })).GetProperty("id").GetGuid();

    private static async Task StockAsync(Setup s, Guid itemId, Guid? binId, decimal quantity, decimal unitCost, string? lotNumber = null, DateOnly? expiresOn = null)
    {
        var adjustment = await s.Owner.PostAsync("/api/v1/inventory/adjustments", new
        {
            companyId = s.CompanyId,
            warehouseId = s.Warehouse,
            kind = "opening",
            lines = new object[] { new { itemId, quantity, unitCost, reasonCode = "FOUND", binId, lotNumber, expiresOn = expiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } },
        });
        await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
    }

    private static async Task<(Guid OrderId, Guid LineId)> ConfirmedOrderAsync(Setup s, Guid itemId, decimal quantity)
    {
        var order = await s.Owner.PostAsync("/api/v1/sales/orders", new { companyId = s.CompanyId, partnerId = s.Customer, warehouseId = s.Warehouse, lines = new object[] { new { itemId, quantity } } });
        var orderId = order.GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/sales/orders/{orderId}/confirm", new { }, HttpStatusCode.OK);
        return (orderId, order.GetProperty("lines")[0].GetProperty("id").GetGuid());
    }

    private static async Task<Dictionary<Guid, decimal>> OnHandByBinAsync(Setup s, Guid itemId)
    {
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&itemId={itemId}&warehouseId={s.Warehouse}&includeZero=true");
        return balances.EnumerateArray().Where(static b => b.TryGetProperty("binId", out var bin) && bin.ValueKind == JsonValueKind.String)
            .GroupBy(static b => b.GetProperty("binId").GetGuid()).ToDictionary(static g => g.Key, static g => g.Sum(static b => b.GetProperty("onHand").GetDecimal()));
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

    private static IReadOnlyList<(string? Bin, string? Lot, decimal Quantity, IReadOnlyList<string?> Serials)> Parts(JsonElement line) =>
        line.GetProperty("allocations").EnumerateArray().Select(static a => (
            a.GetProperty("binCode").ValueKind == JsonValueKind.String ? a.GetProperty("binCode").GetString() : null,
            a.GetProperty("lotNumber").ValueKind == JsonValueKind.String ? a.GetProperty("lotNumber").GetString() : null,
            a.GetProperty("quantity").GetDecimal(),
            (IReadOnlyList<string?>)a.GetProperty("serialNumbers").EnumerateArray().Select(static x => x.GetString()).ToList())).ToList();

    [Fact]
    public async Task An_order_in_a_warehouse_with_bins_is_picked_in_walking_order_short_picked_and_ships_exactly_what_was_picked()
    {
        var s = await SetUpAsync(bins: true);
        var owner = s.Owner;
        var a01 = await BinAsync(s, "A-01", 30);
        var a02 = await BinAsync(s, "A-02", 10);
        var b01 = await BinAsync(s, "B-01", 20);
        var receiving = await BinAsync(s, "R-01", 1, kind: "receiving");
        await StockAsync(s, s.Tea, a01, 4m, 40m);
        await StockAsync(s, s.Tea, a02, 3m, 40m);
        await StockAsync(s, s.Tea, b01, 5m, 40m);
        await StockAsync(s, s.Tea, receiving, 50m, 40m);

        // The order reserves at warehouse level: it does not choose shelves, its pick list does.
        var (orderId, lineId) = await ConfirmedOrderAsync(s, s.Tea, 10m);
        (await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}")).GetProperty("lines")[0].GetProperty("qtyReserved").GetDecimal().ShouldBe(10m);

        // Located in the bins' pick sequence; stock still in the receiving bin is not picked from.
        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 10m } } });
        Parts(draft.GetProperty("lines")[0]).Select(static p => (p.Bin, p.Quantity)).ShouldBe([("A-02", 3m), ("B-01", 5m), ("A-01", 2m)]);
        var shipmentId = draft.GetProperty("id").GetGuid();

        var released = await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/release", new { }, HttpStatusCode.OK);
        released.GetProperty("status").GetString().ShouldBe("picking");
        released.GetProperty("pickListNumber").GetString()!.ShouldStartWith("PCK-");
        var listId = released.GetProperty("pickListId").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/sales/shipments/{shipmentId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.picking_incomplete");
        (await owner.PutErrorAsync($"/api/v1/sales/shipments/{shipmentId}", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 9m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.picking_in_progress");

        // The picker works the list on a handheld: it only holds the picking permission.
        var picker = await InviteAsync(owner, s.Ws, "picker", "inventory.pick.execute");
        var queue = await picker.Client.GetOkAsync($"/api/v1/inventory/pick-lists?warehouseId={s.Warehouse}&status=live");
        queue.EnumerateArray().Select(static p => p.GetProperty("id").GetGuid()).ShouldContain(listId);
        var list = await picker.Client.GetOkAsync($"/api/v1/inventory/pick-lists/{listId}");
        var lines = list.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(static l => (l.GetProperty("binCode").GetString(), l.GetProperty("qtyToPick").GetDecimal())).ShouldBe([("A-02", 3m), ("B-01", 5m), ("A-01", 2m)]);

        var first = await picker.Client.PostAsync($"/api/v1/inventory/pick-lists/{listId}/lines/{lines[0].GetProperty("id").GetGuid()}/pick", new { quantity = 3m }, HttpStatusCode.OK);
        first.GetProperty("status").GetString().ShouldBe("in_progress");
        first.GetProperty("assignedTo").GetGuid().ShouldBe(picker.MembershipId);
        await picker.Client.PostAsync($"/api/v1/inventory/pick-lists/{listId}/lines/{lines[1].GetProperty("id").GetGuid()}/pick", new { quantity = 5m }, HttpStatusCode.OK);
        (await picker.Client.PostErrorAsync($"/api/v1/inventory/pick-lists/{listId}/lines/{lines[2].GetProperty("id").GetGuid()}/pick", new { quantity = 1m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pick.short_reason_required");
        var done = await picker.Client.PostAsync($"/api/v1/inventory/pick-lists/{listId}/lines/{lines[2].GetProperty("id").GetGuid()}/pick", new { quantity = 1m, shortReason = "Carton damaged" }, HttpStatusCode.OK);
        done.GetProperty("status").GetString().ShouldBe("picked");
        done.GetProperty("qtyPicked").GetDecimal().ShouldBe(9m);

        // Posting ships what was picked; the unpicked unit stays reserved on the order for a later shipment.
        var posted = await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        var postedLine = posted.GetProperty("lines")[0];
        postedLine.GetProperty("quantity").GetDecimal().ShouldBe(9m);
        postedLine.GetProperty("cogsAmount").GetDecimal().ShouldBe(360m);
        Parts(postedLine).Select(static p => (p.Bin, p.Quantity)).ShouldBe([("A-02", 3m), ("B-01", 5m), ("A-01", 1m)]);
        posted.GetProperty("pickListStatus").GetString().ShouldBe("closed");

        var orderLine = (await owner.GetOkAsync($"/api/v1/sales/orders/{orderId}")).GetProperty("lines")[0];
        orderLine.GetProperty("qtyShipped").GetDecimal().ShouldBe(9m);
        orderLine.GetProperty("qtyReserved").GetDecimal().ShouldBe(1m);
        orderLine.GetProperty("status").GetString().ShouldBe("open");

        var onHand = await OnHandByBinAsync(s, s.Tea);
        onHand[a02].ShouldBe(0m);
        onHand[b01].ShouldBe(0m);
        onHand[a01].ShouldBe(3m);
        onHand[receiving].ShouldBe(50m);

        // A reversal returns every unit to the bin it left, at its own cost.
        await owner.PostAsync($"/api/v1/sales/shipments/{shipmentId}/reverse", new { reason = "Customer refused delivery" }, HttpStatusCode.OK);
        onHand = await OnHandByBinAsync(s, s.Tea);
        onHand[a02].ShouldBe(3m);
        onHand[b01].ShouldBe(5m);
        onHand[a01].ShouldBe(4m);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Two_pick_lists_never_plan_the_same_shelf_and_a_picker_cannot_take_what_another_list_holds()
    {
        var s = await SetUpAsync(bins: true);
        var owner = s.Owner;
        var near = await BinAsync(s, "A-01", 1);
        var far = await BinAsync(s, "B-01", 2);
        await StockAsync(s, s.Tea, near, 5m, 40m);
        await StockAsync(s, s.Tea, far, 5m, 40m);
        var (order1, line1) = await ConfirmedOrderAsync(s, s.Tea, 5m);
        var (order2, line2) = await ConfirmedOrderAsync(s, s.Tea, 5m);

        // Drafts only suggest (both see the near bin); a release keeps its plan, and the next release plans around it.
        var shipment1 = (await owner.PostAsync("/api/v1/sales/shipments", new { orderId = order1, lines = new object[] { new { orderLineId = line1, quantity = 5m } } })).GetProperty("id").GetGuid();
        var draft2 = await owner.PostAsync("/api/v1/sales/shipments", new { orderId = order2, lines = new object[] { new { orderLineId = line2, quantity = 5m } } });
        Parts(draft2.GetProperty("lines")[0]).Single().Bin.ShouldBe("A-01");
        var shipment2 = draft2.GetProperty("id").GetGuid();

        var list1 = (await owner.PostAsync($"/api/v1/sales/shipments/{shipment1}/release", new { }, HttpStatusCode.OK)).GetProperty("pickListId").GetGuid();
        var list2 = (await owner.PostAsync($"/api/v1/sales/shipments/{shipment2}/release", new { }, HttpStatusCode.OK)).GetProperty("pickListId").GetGuid();
        var lines1 = (await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{list1}")).GetProperty("lines");
        var lines2 = (await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{list2}")).GetProperty("lines");
        lines1[0].GetProperty("binCode").GetString().ShouldBe("A-01");
        lines2[0].GetProperty("binCode").GetString().ShouldBe("B-01");
        (await owner.PostErrorAsync($"/api/v1/sales/shipments/{shipment1}/release", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.not_draft");

        // Picking list 1 from the far bin would take list 2's stock.
        var (code, _) = await owner.PostErrorAsync($"/api/v1/inventory/pick-lists/{list1}/lines/{lines1[0].GetProperty("id").GetGuid()}/pick", new { quantity = 5m, binId = far }, HttpStatusCode.Conflict);
        code.ShouldBe("pick.not_enough_here");

        // A list taken by one picker is not worked by another, unless a pick manager steps in.
        var alice = await InviteAsync(owner, s.Ws, "alice", "inventory.pick.execute");
        var bob = await InviteAsync(owner, s.Ws, "bob", "inventory.pick.execute");
        (await alice.Client.PostAsync($"/api/v1/inventory/pick-lists/{list2}/claim", new { }, HttpStatusCode.OK)).GetProperty("assignedTo").GetGuid().ShouldBe(alice.MembershipId);
        (await bob.Client.PostErrorAsync($"/api/v1/inventory/pick-lists/{list2}/claim", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("pick.assigned_to_other");
        (await bob.Client.PostErrorAsync($"/api/v1/inventory/pick-lists/{list2}/lines/{lines2[0].GetProperty("id").GetGuid()}/pick", new { quantity = 5m }, HttpStatusCode.Forbidden)).Code.ShouldBe("pick.assigned_to_other");
        (await bob.Client.PostErrorAsync($"/api/v1/inventory/pick-lists/{list2}/assign", new { membershipId = bob.MembershipId }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNullOrEmpty();
        (await owner.PostAsync($"/api/v1/inventory/pick-lists/{list2}/assign", new { membershipId = bob.MembershipId }, HttpStatusCode.OK)).GetProperty("assignedTo").GetGuid().ShouldBe(bob.MembershipId);
        await bob.Client.PostAsync($"/api/v1/inventory/pick-lists/{list2}/lines/{lines2[0].GetProperty("id").GetGuid()}/pick", new { quantity = 5m }, HttpStatusCode.OK);

        // Taking shipment 1 back cancels its list; released again, it plans the near bin, which is free once more.
        var back = await owner.PostAsync($"/api/v1/sales/shipments/{shipment1}/cancel-picking", new { reason = "Customer asked to wait" }, HttpStatusCode.OK);
        back.GetProperty("status").GetString().ShouldBe("draft");
        (await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{list1}")).GetProperty("status").GetString().ShouldBe("cancelled");
        var again = await owner.PostAsync($"/api/v1/sales/shipments/{shipment1}/release", new { }, HttpStatusCode.OK);
        again.GetProperty("pickListId").GetGuid().ShouldNotBe(list1);
        (await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{again.GetProperty("pickListId").GetGuid()}")).GetProperty("lines")[0].GetProperty("binCode").GetString().ShouldBe("A-01");

        // Deleting a shipment being picked cancels its list too.
        (await owner.DeleteAsync(new Uri($"/api/v1/sales/shipments/{shipment1}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{again.GetProperty("pickListId").GetGuid()}")).GetProperty("status").GetString().ShouldBe("cancelled");

        await owner.PostAsync($"/api/v1/sales/shipments/{shipment2}/post", new { }, HttpStatusCode.OK);
        (await OnHandByBinAsync(s, s.Tea))[far].ShouldBe(0m);
        await owner.AssertInvariantsAsync();
    }

    /// <summary>Two releases racing for the same shelf: each takes the per-item lock in turn, so the second always sees the first's plan.</summary>
    [Fact]
    public async Task Two_releases_racing_for_the_same_shelf_never_plan_it_twice()
    {
        var s = await SetUpAsync(bins: true);
        var owner = s.Owner;
        await StockAsync(s, s.Tea, await BinAsync(s, "A-01", 1), 5m, 40m);
        await StockAsync(s, s.Tea, await BinAsync(s, "B-01", 2), 5m, 40m);
        var shipments = new List<Guid>();
        foreach (var _ in new[] { 1, 2 })
        {
            var (orderId, lineId) = await ConfirmedOrderAsync(s, s.Tea, 5m);
            shipments.Add((await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 5m } } })).GetProperty("id").GetGuid());
        }

        for (var round = 1; round <= 25; round++)
        {
            var responses = await Task.WhenAll(shipments.Select(id => owner.PostAsJsonAsync($"/api/v1/sales/shipments/{id}/release", new { }, ApiFixture.Json)));
            var bins = new List<string?>();
            foreach (var response in responses)
            {
                var released = await response.ReadJsonAsync();
                response.StatusCode.ShouldBe(HttpStatusCode.OK, released.ToString());
                var list = await owner.GetOkAsync($"/api/v1/inventory/pick-lists/{released.GetProperty("pickListId").GetGuid()}");
                bins.Add(list.GetProperty("lines").EnumerateArray().Single().GetProperty("binCode").GetString());
            }

            bins.Order(StringComparer.Ordinal).ToList().ShouldBe(["A-01", "B-01"], customMessage: $"round {round}");
            foreach (var id in shipments)
            {
                await owner.PostAsync($"/api/v1/sales/shipments/{id}/cancel-picking", new { reason = $"round {round}" }, HttpStatusCode.OK);
            }
        }

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Packages_hold_no_more_than_the_shipment_carries_and_carrier_details_follow_after_posting()
    {
        var s = await SetUpAsync(bins: false);
        var owner = s.Owner;
        await StockAsync(s, s.Tea, null, 10m, 40m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, s.Tea, 10m);
        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 10m } } });
        var id = draft.GetProperty("id").GetGuid();
        var number = draft.GetProperty("number").GetString();

        var packed = await owner.PutAsync($"/api/v1/sales/shipments/{id}/packages", new
        {
            packages = new object[]
            {
                new { packageType = "carton", weightKg = 3.2m, lengthCm = 40m, widthCm = 30m, heightCm = 30m, trackingNumber = "TRK-1", contents = new object[] { new { orderLineId = lineId, quantity = 6m } } },
                new { packageType = "carton", weightKg = 2.1m, contents = new object[] { new { orderLineId = lineId, quantity = 4m } } },
            },
        });
        var packages = packed.GetProperty("packages").EnumerateArray().ToList();
        packages.Select(static p => p.GetProperty("packageNumber").GetString()).ShouldBe([$"{number}-01", $"{number}-02"]);
        packages[0].GetProperty("contentsWeightKg").GetDecimal().ShouldBe(3m);
        packed.GetProperty("totalWeightKg").GetDecimal().ShouldBe(5.3m);
        packed.GetProperty("lines")[0].GetProperty("qtyPacked").GetDecimal().ShouldBe(10m);

        (await owner.PutErrorAsync($"/api/v1/sales/shipments/{id}/packages", new { packages = new object[] { new { contents = new object[] { new { orderLineId = lineId, quantity = 11m } } } } }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.packed_exceeds_shipped");
        (await owner.PutErrorAsync($"/api/v1/sales/shipments/{id}/packages", new { packages = new object[] { new { packageType = "suitcase", contents = new object[] { new { orderLineId = lineId, quantity = 1m } } } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("shipment.package_type_invalid");
        (await owner.PutErrorAsync($"/api/v1/sales/shipments/{id}", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 8m } } }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.packed_exceeds_shipped");

        await owner.PostAsync($"/api/v1/sales/shipments/{id}/post", new { }, HttpStatusCode.OK);
        var carrier = await owner.PutAsync($"/api/v1/sales/shipments/{id}/carrier", new { carrier = "DHL Express", trackingNumber = "JD014600003" });
        carrier.GetProperty("carrier").GetString().ShouldBe("DHL Express");
        carrier.GetProperty("trackingNumber").GetString().ShouldBe("JD014600003");
        await owner.PutAsync($"/api/v1/sales/shipments/{id}/packages", new { packages = new object[] { new { packageType = "pallet", weightKg = 5.3m, trackingNumber = "JD014600003-1", contents = new object[] { new { orderLineId = lineId, quantity = 10m } } } } });

        await owner.PostAsync($"/api/v1/sales/shipments/{id}/reverse", new { reason = "Wrong address" }, HttpStatusCode.OK);
        (await owner.PutErrorAsync($"/api/v1/sales/shipments/{id}/packages", new { packages = Array.Empty<object>() }, HttpStatusCode.Conflict)).Code.ShouldBe("shipment.reversed");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_lot_and_serial_item_ships_a_lot_first_then_serials_from_within_it_and_returns_each_serial_on_reversal()
    {
        var s = await SetUpAsync(bins: false);
        var owner = s.Owner;
        var serum = (await owner.PostAsync("/api/v1/items", new { code = "SERUM", name = Name("Vaccine", "لقاح"), baseUom = "PCS", tracking = "lot_and_serial", expiryRequired = true, fefo = true, listPrice = 500m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();

        async Task ReceiveAsync(string lotNumber, DateOnly expiresOn, IReadOnlyList<string> serialNumbers, decimal unitCost)
        {
            var adjustment = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Warehouse, kind = "opening", lines = new object[] { new { itemId = serum, quantity = (decimal)serialNumbers.Count, unitCost, reasonCode = "FOUND", lotNumber, expiresOn = expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), serialNumbers } } });
            await owner.PostAsync($"/api/v1/inventory/adjustments/{adjustment.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        }

        // Received first but expiring last: first expiry first out still takes the other lot first.
        await ReceiveAsync("LOT-V2", new DateOnly(2027, 3, 1), ["SN-3", "SN-4"], 100m);
        await ReceiveAsync("LOT-V1", new DateOnly(2026, 12, 1), ["SN-1", "SN-2"], 100m);
        var (orderId, lineId) = await ConfirmedOrderAsync(s, serum, 3m);

        var draft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId, lines = new object[] { new { orderLineId = lineId, quantity = 3m } } });
        var parts = Parts(draft.GetProperty("lines")[0]);
        parts.Select(static p => (p.Lot, p.Quantity)).ShouldBe([("LOT-V1", 2m), ("LOT-V2", 1m)]);
        parts[0].Serials.ShouldBe(["SN-1", "SN-2"]);
        parts[1].Serials.ShouldBe(["SN-3"]);

        var posted = await owner.PostAsync($"/api/v1/sales/shipments/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("lines")[0].GetProperty("cogsAmount").GetDecimal().ShouldBe(300m);
        foreach (var sn in new[] { "SN-1", "SN-2", "SN-3" })
        {
            (await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={serum}&serialNumber={sn}")).GetProperty("status").GetString().ShouldBe("sold");
        }

        (await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={serum}&serialNumber=SN-4")).GetProperty("status").GetString().ShouldBe("in_stock");

        await owner.PostAsync($"/api/v1/sales/shipments/{draft.GetProperty("id").GetGuid()}/reverse", new { reason = "Cold chain broken" }, HttpStatusCode.OK);
        foreach (var sn in new[] { "SN-1", "SN-2", "SN-3" })
        {
            (await owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={serum}&serialNumber={sn}")).GetProperty("status").GetString().ShouldBe("returned");
        }

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_line_no_single_lot_covers_splits_across_lots_first_expiry_first_or_first_received_first_when_the_item_does_not_use_FEFO()
    {
        var s = await SetUpAsync(bins: false);
        var owner = s.Owner;
        var milk = (await owner.PostAsync("/api/v1/items", new { code = "MILK", name = Name("Milk 1L", "حليب ١ لتر"), baseUom = "PCS", tracking = "lot", expiryRequired = true, fefo = true, listPrice = 5m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var rice = (await owner.PostAsync("/api/v1/items", new { code = "RICE", name = Name("Rice 5kg", "رز ٥ كغ"), baseUom = "PCS", tracking = "lot", expiryRequired = true, listPrice = 9m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();

        // Each item receives the later-expiring lot first.
        await StockAsync(s, milk, null, 5m, 10m, "MILK-B", new DateOnly(2026, 12, 1));
        await StockAsync(s, milk, null, 5m, 10m, "MILK-A", new DateOnly(2026, 11, 1));
        await StockAsync(s, rice, null, 5m, 7m, "RICE-B", new DateOnly(2027, 6, 1));
        await StockAsync(s, rice, null, 5m, 6m, "RICE-A", new DateOnly(2027, 1, 1));

        var (milkOrder, milkLine) = await ConfirmedOrderAsync(s, milk, 8m);
        var milkDraft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId = milkOrder, lines = new object[] { new { orderLineId = milkLine, quantity = 8m } } });
        Parts(milkDraft.GetProperty("lines")[0]).Select(static p => (p.Lot, p.Quantity)).ShouldBe([("MILK-A", 5m), ("MILK-B", 3m)]);
        milkDraft.GetProperty("lines")[0].GetProperty("lotNumber").ValueKind.ShouldBe(JsonValueKind.Null);
        var milkPosted = await owner.PostAsync($"/api/v1/sales/shipments/{milkDraft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        milkPosted.GetProperty("lines")[0].GetProperty("cogsAmount").GetDecimal().ShouldBe(80m);

        var (riceOrder, riceLine) = await ConfirmedOrderAsync(s, rice, 8m);
        var riceDraft = await owner.PostAsync("/api/v1/sales/shipments", new { orderId = riceOrder, lines = new object[] { new { orderLineId = riceLine, quantity = 8m } } });
        Parts(riceDraft.GetProperty("lines")[0]).Select(static p => (p.Lot, p.Quantity)).ShouldBe([("RICE-B", 5m), ("RICE-A", 3m)]);

        // Reversing the split shipment puts each lot's units back in that lot.
        await owner.PostAsync($"/api/v1/sales/shipments/{milkDraft.GetProperty("id").GetGuid()}/reverse", new { reason = "Returned unopened" }, HttpStatusCode.OK);
        var lots = await owner.GetOkAsync($"/api/v1/inventory/lots?itemId={milk}");
        foreach (var lot in lots.EnumerateArray())
        {
            var trace = await owner.GetOkAsync($"/api/v1/inventory/lots/{lot.GetProperty("id").GetGuid()}/trace");
            trace.GetProperty("onHand").EnumerateArray().Sum(static b => b.GetProperty("onHand").GetDecimal()).ShouldBe(5m);
        }

        await owner.AssertInvariantsAsync();
    }
}

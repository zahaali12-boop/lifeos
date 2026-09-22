using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>Adjustments with reason codes and approval, revaluations and NRV write-downs, one-step transfers and shortages, assembly builds (roadmap 3.4).</summary>
[Collection(ApiCollection.Name)]
public sealed class StockDocumentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Main, Guid Branch, Guid Transit);

    private async Task<Setup> SetUpAsync(string costingMethod = "fifo")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var branch = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra branch", "فرع البصرة") });
        var transit = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "TRANSIT", name = Name("In transit", "في الطريق"), kind = "in_transit" });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), branch.GetProperty("id").GetGuid(), transit.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> ItemAsync(Setup s, string code, string type = "stock") =>
        (await s.Owner.PostAsync("/api/v1/items", new { code, name = Name(code, code), baseUom = "PCS", type })).GetProperty("id").GetGuid();

    private static async Task<Guid> ReasonAsync(Setup s, string code, string appliesTo, string? role = null, bool requiresNote = false) =>
        (await s.Owner.PostAsync("/api/v1/inventory/reason-codes", new { code, name = Name(code, code), appliesTo, accountRoleOverride = role, requiresNote })).GetProperty("id").GetGuid();

    private Task<StockPostingResult> ReceiveAsync(Setup s, Guid itemId, decimal quantity, DateOnly date, decimal unitCost, Guid? warehouse = null) =>
        host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, date, "test_document", Guid.CreateVersion7(), [new StockLine(itemId, StockEntryTypes.PurchaseReceipt, quantity, warehouse ?? s.Main, UnitCost: unitCost)]));

    private static DateOnly D(int day) => new(2026, 9, day);

    private async Task<decimal> BookedAsync(Setup s, string role, DateOnly? asOf = null, Guid? warehouseKeyItem = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role AND (@asOf::date IS NULL OR l.posting_date <= @asOf) AND (@item::uuid IS NULL OR l.subledger_ref = @item)",
            new { t = s.Ws.TenantId, c = s.CompanyId, role, asOf, item = warehouseKeyItem });
    }

    /// <summary>Σ value entries on the in-transit warehouse at a date (the transit balance the acceptance criterion names).</summary>
    private async Task<decimal> TransitValuedAsync(Setup s, DateOnly asOf)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(cost_amount_actual + cost_amount_expected), 0) FROM app.inv_stock_value_entries WHERE tenant_id = @t AND company_id = @c AND warehouse_id = @w AND posting_date <= @asOf AND account_role = 'InventoryInTransit'",
            new { t = s.Ws.TenantId, c = s.CompanyId, w = s.Transit, asOf });
    }

    [Fact]
    public async Task Adjustments_carry_reason_codes_go_through_approval_when_the_company_asks_and_post_to_the_reasons_account()
    {
        var s = await SetUpAsync();
        var item = await ItemAsync(s, "GLASS");
        var damaged = await ReasonAsync(s, "DAMAGED", "scrap", role: "Scrap", requiresNote: true);
        var found = await ReasonAsync(s, "FOUND", "adjustment");
        var writeOff = await ReasonAsync(s, "WRITEOFF", "adjustment", role: "InventoryWriteDown");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/reason-codes", new { code = "BAD", name = Name("Bad", "سيء"), appliesTo = "adjustment", accountRoleOverride = "Bank" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("reason_code.account_role_invalid");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Again", "مرة") }, HttpStatusCode.Conflict)).Code.ShouldBe("reason_code.code_taken");

        // An opening adjustment brings 100 at 5; a positive line without a cost takes the current cost.
        var opening = await s.Owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "opening", postingDate = "2026-09-01", lines = new object[] { new { itemCode = "GLASS", quantity = 100, unitCost = 5, reasonCode = "FOUND" } } });
        opening.GetProperty("status").GetString().ShouldBe("draft");
        opening.GetProperty("number").GetString().ShouldStartWith("DRAFT-");
        var openingId = opening.GetProperty("id").GetGuid();
        var posted = await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{openingId}/submit", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("number").GetString().ShouldStartWith("ADJ-2026-");
        posted.GetProperty("journalEntryId").GetGuid().ShouldNotBe(Guid.Empty);
        posted.GetProperty("lines")[0].GetProperty("costAmount").GetDecimal().ShouldBe(500m);
        (await BookedAsync(s, "OpeningBalanceEquity")).ShouldBe(-500m);
        var more = await s.Owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "positive", postingDate = "2026-09-02", lines = new object[] { new { itemId = item, quantity = 10, reasonCodeId = found } } });
        (await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{more.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK)).GetProperty("lines")[0].GetProperty("costAmount").GetDecimal().ShouldBe(50m, "no cost given: the current cost of 5 applies");
        (await BookedAsync(s, "InventoryAdjustment")).ShouldBe(-50m);

        // Validation: the reason must apply to the kind, a note when the reason demands it, no cost on stock taken out.
        (await s.Owner.PostErrorAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "scrap", lines = new object[] { new { itemId = item, quantity = 1, reasonCodeId = found } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("adjustment.reason_not_applicable");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "scrap", lines = new object[] { new { itemId = item, quantity = 1, reasonCodeId = damaged } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("adjustment.note_required");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "negative", lines = new object[] { new { itemId = item, quantity = 1, unitCost = 4, reasonCodeId = found } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("adjustment.unit_cost_not_allowed");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "negative", lines = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("adjustment.lines_required");

        // The company requires approval: the submitter cannot approve; the approver posts it; the reason's account override takes the scrap.
        await s.Owner.PutAsync($"/api/v1/organization/companies/{s.CompanyId}/settings/inventory.adjustments.approval", new { value = "required", valueType = "string" });
        var scrap = await s.Owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "scrap", postingDate = "2026-09-03", lines = new object[] { new { itemId = item, quantity = 4, reasonCodeId = damaged, note = "Dropped pallet" } } });
        var scrapId = scrap.GetProperty("id").GetGuid();
        var pending = await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/submit", new { }, HttpStatusCode.OK);
        pending.GetProperty("status").GetString().ShouldBe("pending_approval");
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/adjustments/{scrapId}/approve", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("adjustment.self_approval");
        (await s.Owner.PutErrorAsync($"/api/v1/inventory/adjustments/{scrapId}", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "scrap", lines = new object[] { new { itemId = item, quantity = 4, reasonCodeId = damaged, note = "x" } } }, HttpStatusCode.Conflict)).Code.ShouldBe("adjustment.not_draft");

        var roles = await s.Owner.GetOkAsync("/api/v1/roles");
        var managerRole = roles.EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "inventory_manager").GetProperty("id").GetGuid();
        var email = $"approver-{s.Ws.Slug}@example.test";
        await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Approver", roleIds = new[] { managerRole } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "approver-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        var approver = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        var rejected = await approver.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/reject", new { reason = "Photograph it first" }, HttpStatusCode.OK);
        rejected.GetProperty("status").GetString().ShouldBe("rejected");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Photograph it first");
        await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/submit", new { }, HttpStatusCode.OK);
        var approved = await approver.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/approve", new { }, HttpStatusCode.OK);
        approved.GetProperty("status").GetString().ShouldBe("posted");
        approved.GetProperty("approvedBy").GetGuid().ShouldNotBe(Guid.Empty);
        approved.GetProperty("lines")[0].GetProperty("costAmount").GetDecimal().ShouldBe(-20m);
        (await BookedAsync(s, "Scrap")).ShouldBe(20m);

        // A negative adjustment with a write-off reason goes to InventoryWriteDown instead of the adjustment account.
        await s.Owner.PutAsync($"/api/v1/organization/companies/{s.CompanyId}/settings/inventory.adjustments.approval", new { value = "none", valueType = "string" });
        var lost = await s.Owner.PostAsync("/api/v1/inventory/adjustments", new { companyId = s.CompanyId, warehouseId = s.Main, kind = "negative", postingDate = "2026-09-04", lines = new object[] { new { itemId = item, quantity = 6, reasonCodeId = writeOff } } });
        await s.Owner.PostAsync($"/api/v1/inventory/adjustments/{lost.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        (await BookedAsync(s, "InventoryWriteDown")).ShouldBe(30m);
        (await BookedAsync(s, "InventoryAdjustment")).ShouldBe(-50m);
        (await BookedAsync(s, "Inventory")).ShouldBe(500m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-30")).GetProperty("totalValue").GetDecimal().ShouldBe(500m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/adjustments?companyId={s.CompanyId}&status=posted")).GetArrayLength().ShouldBe(4);
        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task NRV_write_downs_are_capped_on_recovery_one_step_transfers_land_directly_and_shortages_are_written_off_from_transit()
    {
        var s = await SetUpAsync();
        var item = await ItemAsync(s, "OIL");
        await ReceiveAsync(s, item, 10m, D(1), 20m);
        await ReceiveAsync(s, item, 10m, D(2), 30m);

        // Net realisable value drops to 15: 20 on hand worth 500 are written down to 300; the layers carry it, so the next shipment costs 15 a unit.
        var writeDown = await s.Owner.PostAsync("/api/v1/inventory/revaluations", new { companyId = s.CompanyId, kind = "nrv_writedown", postingDate = "2026-09-05", lines = new object[] { new { itemId = item, newUnitCost = 15 } } });
        var revaluationId = writeDown.GetProperty("id").GetGuid();
        var postedWriteDown = await s.Owner.PostAsync($"/api/v1/inventory/revaluations/{revaluationId}/post", new { }, HttpStatusCode.OK);
        postedWriteDown.GetProperty("status").GetString().ShouldBe("posted");
        postedWriteDown.GetProperty("number").GetString().ShouldStartWith("RVL-2026-");
        postedWriteDown.GetProperty("lines")[0].GetProperty("quantity").GetDecimal().ShouldBe(20m);
        postedWriteDown.GetProperty("lines")[0].GetProperty("currentUnitCost").GetDecimal().ShouldBe(25m);
        postedWriteDown.GetProperty("totalAmount").GetDecimal().ShouldBe(-200m);
        (await BookedAsync(s, "InventoryWriteDown")).ShouldBe(200m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-05")).GetProperty("totalValue").GetDecimal().ShouldBe(300m);
        var shipped = await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, D(6), "test_document", Guid.CreateVersion7(), [new StockLine(item, StockEntryTypes.SaleShipment, 12m, s.Main)]));
        shipped.Entries[0].CostAmount.ShouldBe(-180m, "10 from the first layer at 15 and 2 from the second at 15");

        // The market recovers to 40: IAS 2 caps the reversal at the original cost (the remaining 8 came in at 30).
        var recovery = await s.Owner.PostAsync("/api/v1/inventory/revaluations", new { companyId = s.CompanyId, kind = "nrv_writedown", postingDate = "2026-09-07", lines = new object[] { new { itemId = item, newUnitCost = 40 } } });
        var postedRecovery = await s.Owner.PostAsync($"/api/v1/inventory/revaluations/{recovery.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        postedRecovery.GetProperty("lines")[0].GetProperty("amount").GetDecimal().ShouldBe(120m, "8 × (30 − 15): back to cost, never above it");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-07")).GetProperty("totalValue").GetDecimal().ShouldBe(240m);
        (await BookedAsync(s, "InventoryWriteDown")).ShouldBe(80m);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/revaluations/{revaluationId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("revaluation.not_draft");

        // A one-step transfer lands in the branch straight away at the carried cost; nothing touches transit.
        var oneStep = await s.Owner.PostAsync("/api/v1/inventory/transfers", new { companyId = s.CompanyId, fromWarehouseId = s.Main, toWarehouseId = s.Branch, kind = "one_step", lines = new object[] { new { itemId = item, quantity = 3 } } });
        var landed = await s.Owner.PostAsync($"/api/v1/inventory/transfers/{oneStep.GetProperty("id").GetGuid()}/ship", new { shipDate = "2026-09-08" }, HttpStatusCode.OK);
        landed.GetProperty("status").GetString().ShouldBe("received");
        landed.GetProperty("kind").GetString().ShouldBe("one_step");
        landed.GetProperty("transitWarehouseId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={item}&warehouseId={s.Branch}")).Dec("onHand").ShouldBe(3m);
        (await BookedAsync(s, "InventoryInTransit")).ShouldBe(0m);

        // A two-step transfer of 5 loses 2 in transit: 3 arrive, 2 are written off from transit with a shortage reason; the transfer completes.
        var lostInTransit = await ReasonAsync(s, "LOST", "shortage", role: "InventoryWriteDown");
        var twoStep = await s.Owner.PostAsync("/api/v1/inventory/transfers", new { companyId = s.CompanyId, fromWarehouseId = s.Main, toWarehouseId = s.Branch, lines = new object[] { new { itemId = item, quantity = 5 } } });
        var transferId = twoStep.GetProperty("id").GetGuid();
        var shippedTransfer = await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transferId}/ship", new { shipDate = "2026-09-09" }, HttpStatusCode.OK);
        (await TransitValuedAsync(s, D(9))).ShouldBe(150m);
        (await BookedAsync(s, "InventoryInTransit", D(9))).ShouldBe(150m, "in-transit balance equals the in-transit account at the ship date");
        var lineId = shippedTransfer.GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{transferId}/receive", new { receiveDate = "2026-09-10", lines = new object[] { new { lineId, quantity = 3, shortage = 2 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.shortage_reason_required");
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{transferId}/receive", new { receiveDate = "2026-09-10", lines = new object[] { new { lineId, quantity = 4, shortage = 2, shortageReasonCode = "LOST" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.receive_quantity_invalid");
        var received = await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transferId}/receive", new { receiveDate = "2026-09-10", lines = new object[] { new { lineId, quantity = 3, shortage = 2, shortageReasonCode = "LOST", shortageNote = "Two cans missing from the pallet" } } }, HttpStatusCode.OK);
        received.GetProperty("status").GetString().ShouldBe("received");
        received.GetProperty("lines")[0].GetProperty("qtyReceived").GetDecimal().ShouldBe(3m);
        received.GetProperty("lines")[0].GetProperty("qtyShortage").GetDecimal().ShouldBe(2m);
        received.GetProperty("shortagePostingIds").GetArrayLength().ShouldBe(1);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={item}&warehouseId={s.Transit}")).Dec("onHand").ShouldBe(0m);
        (await TransitValuedAsync(s, D(10))).ShouldBe(0m);
        (await BookedAsync(s, "InventoryInTransit", D(10))).ShouldBe(0m, "in-transit balance equals the in-transit account after the receipt and the write-off");
        (await BookedAsync(s, "InventoryWriteDown")).ShouldBe(140m, "80 of net write-down plus the 2 lost cans at 30");
        var byWarehouse = (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-30")).GetProperty("lines").EnumerateArray().Select(static l => (l.GetProperty("warehouseCode").GetString(), l.GetProperty("quantity").GetDecimal(), l.GetProperty("value").GetDecimal())).ToList();
        byWarehouse.ShouldBe([("BASRA", 6m, 180m)]);
        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Assemblies_consume_the_bill_of_material_and_the_output_costs_what_the_components_cost_even_after_a_backdated_component_receipt()
    {
        var s = await SetUpAsync("average");
        var box = await ItemAsync(s, "BOX", type: "assembly");
        var bottle = await ItemAsync(s, "BOTTLE");
        var cap = await ItemAsync(s, "CAP");
        await s.Owner.PostAsync($"/api/v1/items/{box}/boms", new { kind = "assembly", outputQty = 1, lines = new object[] { new { componentItemCode = "BOTTLE", quantity = 6, uom = "PCS" }, new { componentItemCode = "CAP", quantity = 6, uom = "PCS", scrapPct = 50 } } });
        await ReceiveAsync(s, bottle, 100m, D(1), 2m);
        await ReceiveAsync(s, cap, 100m, D(1), 0.5m);

        // Ten boxes: 60 bottles at 2 and 90 caps (50 % scrap allowance) at 0.5 = 165, so 16.5 a box.
        var draft = await s.Owner.PostAsync("/api/v1/inventory/assemblies", new { companyId = s.CompanyId, warehouseId = s.Main, outputItemCode = "BOX", outputQuantity = 10, postingDate = "2026-09-03" });
        draft.GetProperty("lines").EnumerateArray().Select(static l => (l.GetProperty("itemCode").GetString(), l.GetProperty("quantity").GetDecimal())).ShouldBe([("BOTTLE", 60m), ("CAP", 90m)]);
        draft.GetProperty("bomId").GetGuid().ShouldNotBe(Guid.Empty);
        var assemblyId = draft.GetProperty("id").GetGuid();
        (await s.Owner.PostErrorAsync("/api/v1/inventory/assemblies", new { companyId = s.CompanyId, warehouseId = s.Main, outputItemCode = "BOTTLE", outputQuantity = 1 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("assembly.item_not_assembly");
        var built = await s.Owner.PostAsync($"/api/v1/inventory/assemblies/{assemblyId}/post", new { }, HttpStatusCode.OK);
        built.GetProperty("status").GetString().ShouldBe("posted");
        built.GetProperty("number").GetString().ShouldStartWith("ASM-2026-");
        built.GetProperty("outputCost").GetDecimal().ShouldBe(165m);
        built.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("costAmount").GetDecimal()).ShouldBe([-120m, -45m]);
        (await BookedAsync(s, "Inventory", null, box)).ShouldBe(165m);
        (await BookedAsync(s, "Inventory", null, bottle)).ShouldBe(80m);
        (await BookedAsync(s, "Inventory", null, cap)).ShouldBe(5m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/item-cost?companyId={s.CompanyId}&itemId={box}&asOf=2026-09-03")).GetProperty("averageUnitCost").GetDecimal().ShouldBe(16.5m);

        // Bottles received on the 2nd at 4 (backdated) lift the day-3 bottle average to 3: the consumption re-costs to 180, and so does the box.
        await ReceiveAsync(s, bottle, 100m, D(2), 4m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/assemblies/{assemblyId}")).GetProperty("outputCost").GetDecimal().ShouldBe(225m);
        (await BookedAsync(s, "Inventory", null, box)).ShouldBe(225m);
        (await BookedAsync(s, "Inventory", null, bottle)).ShouldBe(420m);
        var sale = await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, D(4), "test_document", Guid.CreateVersion7(), [new StockLine(box, StockEntryTypes.SaleShipment, 4m, s.Main)]));
        sale.Entries[0].CostAmount.ShouldBe(-90m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-30&itemId={box}")).GetProperty("totalValue").GetDecimal().ShouldBe(135m);
        await s.Owner.AssertInvariantsAsync();
    }
}

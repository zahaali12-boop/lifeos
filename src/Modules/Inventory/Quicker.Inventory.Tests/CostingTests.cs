using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>The costing engine (ADR-0008, roadmap 3.3): FIFO, daily average and standard cost, backdating, late invoices and landed costs, the books.</summary>
[Collection(ApiCollection.Name)]
public sealed class CostingTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Main, Guid Branch, Guid Transit);

    private async Task<Setup> SetUpAsync(string costingMethod, string costingScope = "company", string negativeStock = "block")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod, costingScope, negativeStockPolicy = negativeStock });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var branch = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra branch", "فرع البصرة") });
        var transit = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "TRANSIT", name = Name("In transit", "في الطريق"), kind = "in_transit" });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), branch.GetProperty("id").GetGuid(), transit.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> ItemAsync(Setup s, string code) =>
        (await s.Owner.PostAsync("/api/v1/items", new { code, name = Name(code, code), baseUom = "PCS" })).GetProperty("id").GetGuid();

    private Task<StockPostingResult> PostAsync(Setup s, Guid itemId, string type, decimal quantity, DateOnly date, decimal? unitCost = null, bool expected = false, Guid? warehouse = null, string docType = "test_document", Guid? docId = null) =>
        host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, date, docType, docId ?? Guid.CreateVersion7(), [new StockLine(itemId, type, quantity, warehouse ?? s.Main, UnitCost: unitCost, CostIsExpected: expected)]));

    private static DateOnly D(int day) => new(2026, 9, day);

    private static async Task<JsonElement> ExplainAsync(Setup s, Guid sleId) => await s.Owner.GetOkAsync($"/api/v1/inventory/costing/entries/{sleId}");

    private static async Task<JsonElement> ValuationAsync(Setup s, DateOnly asOf, Guid? itemId = null) =>
        await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf={asOf:yyyy-MM-dd}{(itemId is null ? string.Empty : "&itemId=" + itemId)}");

    /// <summary>Σ (debit − credit) of the INV subledger lines of the company at a date, straight from the journal lines.</summary>
    private async Task<decimal> InventoryBookedAsync(Setup s, DateOnly asOf, Guid? itemId = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("""
            SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0)
            FROM app.gl_journal_lines l
            WHERE l.tenant_id = @t AND l.company_id = @c AND l.subledger_type = 'INV' AND l.posting_date <= @asOf AND (@item::uuid IS NULL OR l.subledger_ref = @item)
            """, new { t = s.Ws.TenantId, c = s.CompanyId, asOf, item = itemId });
    }

    private async Task<decimal> AccountBookedAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    [Fact]
    public async Task Fifo_applies_the_oldest_layers_and_a_late_invoice_or_landed_cost_adjusts_what_was_shipped_with_the_trigger_named()
    {
        var s = await SetUpAsync("fifo");
        var item = await ItemAsync(s, "TEA");
        var receipt1 = await PostAsync(s, item, StockEntryTypes.PurchaseReceipt, 10m, D(1), unitCost: 10m, expected: true);
        var receipt2 = await PostAsync(s, item, StockEntryTypes.PurchaseReceipt, 10m, D(3), unitCost: 12m);
        receipt1.Entries[0].CostAmount.ShouldBe(100m);
        receipt1.JournalEntryId.ShouldNotBeNull();
        receipt2.Entries[0].UnitCost.ShouldBe(12m);

        // 15 shipped on the 5th: 10 from the first layer at 10, 5 from the second at 12.
        var shipment = await PostAsync(s, item, StockEntryTypes.SaleShipment, 15m, D(5));
        shipment.Entries[0].CostAmount.ShouldBe(-160m);
        shipment.Entries[0].CostedAtExpected.ShouldBeFalse();
        var explained = await ExplainAsync(s, shipment.Entries[0].Id);
        explained.GetProperty("applications").EnumerateArray().Select(static a => (a.GetProperty("quantity").GetDecimal(), a.GetProperty("costAmount").GetDecimal())).ShouldBe([(10m, 100m), (5m, 60m)]);
        var valuation = await ValuationAsync(s, D(5));
        valuation.GetProperty("totalValue").GetDecimal().ShouldBe(60m);
        valuation.GetProperty("totalExpected").GetDecimal().ShouldBe(100m, "the first receipt is still uninvoiced: its cost stays expected until the invoice settles it");
        (await InventoryBookedAsync(s, D(5))).ShouldBe(60m);
        (await AccountBookedAsync(s, "Cogs")).ShouldBe(160m);
        (await AccountBookedAsync(s, "GRNI")).ShouldBe(-220m);

        // The supplier invoices the first receipt at 11: expected cost reversed, actual cost posted, the shipment's COGS moves by 10.
        var invoice = await s.Owner.PostAsync("/api/v1/inventory/costing/inbound-adjustments", new { sleId = receipt1.Entries[0].Id, kind = "invoice", actualUnitCost = 11m, triggerDocumentType = "purchase_invoice", triggerDocumentId = Guid.CreateVersion7(), reason = "Invoice PI-77 at 11" }, HttpStatusCode.OK);
        invoice.GetProperty("valueEntries").EnumerateArray().Select(static v => v.GetProperty("valueType").GetString()).ShouldBe(["expected_cost_reversal", "direct_cost"]);
        var run = invoice.GetProperty("run");
        run.GetProperty("status").GetString().ShouldBe("completed");
        run.GetProperty("triggerDocumentType").GetString().ShouldBe("purchase_invoice");
        run.GetProperty("entriesReapplied").GetInt32().ShouldBe(1);
        explained = await ExplainAsync(s, shipment.Entries[0].Id);
        explained.GetProperty("entry").GetProperty("costAmount").GetDecimal().ShouldBe(-170m);
        var adjustment = explained.GetProperty("valueEntries").EnumerateArray().Single(static v => v.GetProperty("valueType").GetString() == "cost_adjustment");
        adjustment.GetProperty("costAmountActual").GetDecimal().ShouldBe(-10m);
        adjustment.GetProperty("reason").GetProperty("trigger").GetProperty("documentType").GetString().ShouldBe("purchase_invoice");
        adjustment.GetProperty("reason").GetProperty("note").GetString().ShouldBe("Invoice PI-77 at 11");
        adjustment.GetProperty("journalEntryId").GetGuid().ShouldNotBe(Guid.Empty);
        (await AccountBookedAsync(s, "Cogs")).ShouldBe(170m);
        (await AccountBookedAsync(s, "GRNI")).ShouldBe(-230m);
        (await InventoryBookedAsync(s, D(30))).ShouldBe(60m);
        (await ValuationAsync(s, D(30))).GetProperty("totalExpected").GetDecimal().ShouldBe(0m);

        // A landed cost of 24 on the second receipt: its layer is now 144 for 10, 14.4 a unit; the 5 shipped from it cost 12 more, the 5 on hand are worth 72.
        var landed = await s.Owner.PostAsync("/api/v1/inventory/costing/inbound-adjustments", new { sleId = receipt2.Entries[0].Id, kind = "landed_cost", amount = 24m, triggerDocumentType = "landed_cost_document", triggerDocumentId = Guid.CreateVersion7() }, HttpStatusCode.OK);
        landed.GetProperty("valueEntries")[0].GetProperty("valueType").GetString().ShouldBe("indirect_cost");
        (await ValuationAsync(s, D(30))).GetProperty("totalValue").GetDecimal().ShouldBe(72m);
        (await AccountBookedAsync(s, "Cogs")).ShouldBe(182m);
        (await AccountBookedAsync(s, "LandedCostClearing")).ShouldBe(-24m);
        (await InventoryBookedAsync(s, D(30))).ShouldBe(72m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/item-cost?companyId={s.CompanyId}&itemId={item}&asOf=2026-09-30")).GetProperty("value").GetDecimal().ShouldBe(72m);

        // A purchase return of 2 against the second receipt relieves stock at that layer's exact cost.
        var back = await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, D(8), "purchase_return", Guid.CreateVersion7(), [new StockLine(item, StockEntryTypes.PurchaseReturn, 2m, s.Main, AppliesToSleId: receipt2.Entries[0].Id)]));
        back.Entries[0].CostAmount.ShouldBe(-28.8m);

        // A two-step transfer carries the cost: 3 units at 14.4 into transit, then into the branch.
        var transfer = await s.Owner.PostAsync("/api/v1/inventory/transfers", new { companyId = s.CompanyId, fromWarehouseId = s.Main, toWarehouseId = s.Branch, lines = new object[] { new { itemId = item, quantity = 3 } } });
        await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transfer.GetProperty("id").GetGuid()}/ship", new { }, HttpStatusCode.OK);
        (await AccountBookedAsync(s, "InventoryInTransit")).ShouldBe(43.2m);
        await s.Owner.PostAsync($"/api/v1/inventory/transfers/{transfer.GetProperty("id").GetGuid()}/receive", new { }, HttpStatusCode.OK);
        (await AccountBookedAsync(s, "InventoryInTransit")).ShouldBe(0m);
        var byWarehouse = (await ValuationAsync(s, D(30))).GetProperty("lines").EnumerateArray().Select(static l => (l.GetProperty("warehouseCode").GetString(), l.GetProperty("quantity").GetDecimal(), l.GetProperty("value").GetDecimal())).ToList();
        byWarehouse.ShouldBe([("BASRA", 3m, 43.2m)], "MAIN and TRANSIT hold nothing and are left out unless zero rows are asked for");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/valuation?companyId={s.CompanyId}&asOf=2026-09-30&includeZero=true")).GetProperty("lines").GetArrayLength().ShouldBe(3);
        (await InventoryBookedAsync(s, D(30))).ShouldBe(43.2m);

        // Every run explains itself and the paging works.
        var runs = await s.Owner.GetOkAsync($"/api/v1/inventory/costing/runs?companyId={s.CompanyId}&itemId={item}");
        runs.GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
        runs.GetProperty("items").EnumerateArray().ShouldAllBe(static r => r.GetProperty("status").GetString() == "completed");
        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Average_costs_by_day_and_standard_cost_posts_variances_and_revalues_on_a_new_standard()
    {
        var s = await SetUpAsync("average");
        var avg = await ItemAsync(s, "RICE");
        await PostAsync(s, avg, StockEntryTypes.PurchaseReceipt, 10m, D(1), unitCost: 10m);
        await PostAsync(s, avg, StockEntryTypes.PurchaseReceipt, 10m, D(2), unitCost: 20m);
        // The day's average is (100 + 200) / 20 = 15, whatever the order of the day's entries.
        var ship1 = await PostAsync(s, avg, StockEntryTypes.SaleShipment, 5m, D(2));
        ship1.Entries[0].CostAmount.ShouldBe(-75m);
        var ship2 = await PostAsync(s, avg, StockEntryTypes.SaleShipment, 15m, D(3));
        ship2.Entries[0].CostAmount.ShouldBe(-225m, "the last unit takes the whole remaining value, so zero stock is zero value");
        (await ValuationAsync(s, D(3), avg)).GetProperty("totalValue").GetDecimal().ShouldBe(0m);

        // A backdated receipt on the 1st at 40 changes the average of every day after it: day 2 pools (500 + 200) / 30 = 23.333…,
        // so the first shipment re-costs to 116.667 and the second, at the same average, to 350.000; 10 remain worth 233.333.
        var late = await PostAsync(s, avg, StockEntryTypes.PurchaseReceipt, 10m, D(1), unitCost: 40m);
        late.Entries[0].CostAmount.ShouldBe(400m);
        (await ExplainAsync(s, ship1.Entries[0].Id)).GetProperty("entry").GetProperty("costAmount").GetDecimal().ShouldBe(-116.667m);
        (await ExplainAsync(s, ship2.Entries[0].Id)).GetProperty("entry").GetProperty("costAmount").GetDecimal().ShouldBe(-350m);
        var cost = await s.Owner.GetOkAsync($"/api/v1/inventory/costing/item-cost?companyId={s.CompanyId}&itemId={avg}&asOf=2026-09-03");
        cost.GetProperty("quantity").GetDecimal().ShouldBe(10m);
        cost.GetProperty("value").GetDecimal().ShouldBe(233.333m);
        cost.GetProperty("averageUnitCost").GetDecimal().ShouldBe(23.3333m, 0.0001m);
        (await InventoryBookedAsync(s, D(3), avg)).ShouldBe(233.333m);

        // Standard cost: inventory at the standard, the difference to the invoice a purchase price variance; a new standard revalues the stock on hand.
        var std = await ItemAsync(s, "SUGAR");
        await s.Owner.PutAsync($"/api/v1/items/{std}/company-settings/{s.CompanyId}", new { costingMethodOverride = "standard" });
        await s.Owner.PostAsync("/api/v1/inventory/costing/standard-costs", new { companyId = s.CompanyId, itemId = std, standardCost = 8m, effectiveFrom = "2026-01-01" });
        var receipt = await PostAsync(s, std, StockEntryTypes.PurchaseReceipt, 10m, D(1), unitCost: 10m);
        receipt.Entries[0].CostAmount.ShouldBe(80m);
        (await AccountBookedAsync(s, "PurchasePriceVariance")).ShouldBe(20m);
        (await PostAsync(s, std, StockEntryTypes.SaleShipment, 4m, D(2))).Entries[0].CostAmount.ShouldBe(-32m);
        var version = await s.Owner.PostAsync("/api/v1/inventory/costing/standard-costs", new { companyId = s.CompanyId, itemId = std, standardCost = 9m, effectiveFrom = "2026-09-10", reason = "Annual review" });
        version.GetProperty("standardCost").GetDecimal().ShouldBe(9m);
        (await s.Owner.PostErrorAsync("/api/v1/inventory/costing/standard-costs", new { companyId = s.CompanyId, itemId = std, standardCost = 9m, effectiveFrom = "2026-09-10" }, HttpStatusCode.Conflict)).Code.ShouldBe("costing.standard_cost.date_taken");
        (await ValuationAsync(s, D(9), std)).GetProperty("totalValue").GetDecimal().ShouldBe(48m);
        (await ValuationAsync(s, D(10), std)).GetProperty("totalValue").GetDecimal().ShouldBe(54m);
        (await AccountBookedAsync(s, "InventoryWriteDown")).ShouldBe(-6m);
        (await PostAsync(s, std, StockEntryTypes.SaleShipment, 6m, D(12))).Entries[0].CostAmount.ShouldBe(-54m);
        (await ValuationAsync(s, D(12), std)).GetProperty("totalValue").GetDecimal().ShouldBe(0m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/standard-costs?companyId={s.CompanyId}&itemId={std}")).GetArrayLength().ShouldBe(2);
        (await InventoryBookedAsync(s, D(30))).ShouldBe(233.333m);
        await s.Owner.AssertInvariantsAsync();
    }

    [Theory]
    [InlineData("fifo", 11)]
    [InlineData("average", 23)]
    [InlineData("fifo", 37)]
    public async Task Backdated_entries_in_any_order_yield_the_same_costs_as_date_order_and_inventory_equals_the_books_at_every_sampled_date(string method, int seed)
    {
        var s = await SetUpAsync(method);
        var ordered = await ItemAsync(s, "ORDERED");
        var shuffled = await ItemAsync(s, "SHUFFLED");
        await s.Owner.PutAsync($"/api/v1/items/{shuffled}/company-settings/{s.CompanyId}", new { allowNegativeStock = true });

        // A month of movements that never go negative in date order: receipts at random costs, shipments within what is on hand.
        var random = new Random(seed);
        var script = new List<(DateOnly Date, string Type, decimal Quantity, decimal? UnitCost, Guid DocId)>();
        var onHand = 0m;
        for (var day = 1; day <= 24; day++)
        {
            var receipt = day % 3 != 0 || onHand == 0m;
            if (receipt)
            {
                var quantity = random.Next(1, 12);
                onHand += quantity;
                script.Add((D(day), StockEntryTypes.PurchaseReceipt, quantity, random.Next(5, 40) + random.Next(0, 100) / 100m, Guid.CreateVersion7()));
            }
            else
            {
                var quantity = random.Next(1, (int)Math.Min(onHand, 10m) + 1);
                onHand -= quantity;
                script.Add((D(day), StockEntryTypes.SaleShipment, quantity, null, Guid.CreateVersion7()));
            }
        }

        foreach (var step in script)
        {
            await PostAsync(s, ordered, step.Type, step.Quantity, step.Date, step.UnitCost, docId: step.DocId);
        }

        var permutation = script.OrderBy(_ => random.Next()).ToList();
        var shuffledDocs = new Dictionary<Guid, Guid>();
        foreach (var step in permutation)
        {
            var doc = Guid.CreateVersion7();
            shuffledDocs[step.DocId] = doc;
            await PostAsync(s, shuffled, step.Type, step.Quantity, step.Date, step.UnitCost, docId: doc);
        }

        // Same final cost per movement, however the movements arrived.
        var orderedLedger = await LedgerCostsAsync(s, ordered);
        var shuffledLedger = await LedgerCostsAsync(s, shuffled);
        foreach (var step in script)
        {
            shuffledLedger[shuffledDocs[step.DocId]].ShouldBe(orderedLedger[step.DocId], $"{step.Type} of {step.Quantity} on {step.Date:yyyy-MM-dd} ({method}, seed {seed})");
        }

        var orderedValue = (await ValuationAsync(s, D(30), ordered)).GetProperty("totalValue").GetDecimal();
        (await ValuationAsync(s, D(30), shuffled)).GetProperty("totalValue").GetDecimal().ShouldBe(orderedValue);
        orderedValue.ShouldBeGreaterThan(0m);

        // Valuation equals the inventory accounts at every sampled date, for both items, whatever the posting order.
        foreach (var day in new[] { 1, 5, 9, 14, 19, 24, 30 })
        {
            foreach (var item in new[] { ordered, shuffled })
            {
                var valued = (await ValuationAsync(s, D(day), item)).GetProperty("totalValue").GetDecimal();
                (await InventoryBookedAsync(s, D(day), item)).ShouldBe(valued, $"item {(item == ordered ? "ordered" : "shuffled")} on day {day}");
            }
        }

        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_re_application_beyond_the_threshold_continues_in_a_background_job_and_the_item_shows_valuation_pending_meanwhile()
    {
        var s = await SetUpAsync("fifo");
        var item = await ItemAsync(s, "BULK");
        await PostAsync(s, item, StockEntryTypes.PurchaseReceipt, 1000m, D(2), unitCost: 3m);
        for (var i = 0; i < ApiHostFixture.RecostThreshold + 2; i++)
        {
            await PostAsync(s, item, StockEntryTypes.SaleShipment, 1m, D(3 + (i / 4)));
        }

        // A receipt backdated to the 1st at 2 would feed every shipment after it: too many to re-apply in the request, so the run is queued.
        var backdated = await PostAsync(s, item, StockEntryTypes.PurchaseReceipt, 5m, D(1), unitCost: 2m);
        backdated.Entries[0].CostAmount.ShouldBe(0m, "nothing is valued until the queued run completes");
        var cost = await s.Owner.GetOkAsync($"/api/v1/inventory/costing/item-cost?companyId={s.CompanyId}&itemId={item}");
        cost.GetProperty("valuationPending").GetBoolean().ShouldBeTrue();
        var queued = (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/runs?companyId={s.CompanyId}&itemId={item}&status=queued")).GetProperty("items");
        queued.GetArrayLength().ShouldBe(1);
        queued[0].GetProperty("jobId").GetGuid().ShouldNotBe(Guid.Empty);

        await host.RunWorkerAsync();
        var run = await s.Owner.GetOkAsync($"/api/v1/inventory/costing/runs/{queued[0].GetProperty("id").GetGuid()}");
        run.GetProperty("status").GetString().ShouldBe("completed");
        run.GetProperty("entriesWalked").GetInt32().ShouldBe(ApiHostFixture.RecostThreshold + 4);
        run.GetProperty("entriesReapplied").GetInt32().ShouldBe(ApiHostFixture.RecostThreshold + 2, "every shipment after the backdated receipt was re-applied");
        run.GetProperty("valueEntriesCreated").GetInt32().ShouldBe(5, "only the five earliest shipments changed cost: they now come from the cheaper backdated layer");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/costing/item-cost?companyId={s.CompanyId}&itemId={item}")).GetProperty("valuationPending").GetBoolean().ShouldBeFalse();
        var expectedValue = 3000m + 10m - (3m * (ApiHostFixture.RecostThreshold + 2 - 5)) - 10m;
        (await ValuationAsync(s, D(30), item)).GetProperty("totalValue").GetDecimal().ShouldBe(expectedValue);
        (await InventoryBookedAsync(s, D(30), item)).ShouldBe(expectedValue);
        await s.Owner.AssertInvariantsAsync();
    }

    private static async Task<Dictionary<Guid, decimal>> LedgerCostsAsync(Setup s, Guid itemId)
    {
        var costs = new Dictionary<Guid, decimal>();
        string? cursor = null;
        do
        {
            var page = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/ledger?companyId={s.CompanyId}&itemId={itemId}&limit=50{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}");
            foreach (var entry in page.GetProperty("items").EnumerateArray())
            {
                var explained = await ExplainAsync(s, entry.GetProperty("id").GetGuid());
                costs[entry.GetProperty("sourceDocumentId").GetGuid()] = explained.GetProperty("entry").GetProperty("costAmount").GetDecimal();
            }

            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);
        return costs;
    }

    [Fact]
    public async Task A_document_with_more_distinct_items_than_the_old_walk_guard_is_valued_in_one_posting()
    {
        var s = await SetUpAsync("average");
        var lines = new List<StockLine>();
        for (var i = 0; i < 210; i++)
        {
            lines.Add(new StockLine(await ItemAsync(s, $"BULK-{i:000}"), StockEntryTypes.PurchaseReceipt, 10m, s.Main, UnitCost: 1m + i));
        }

        var posted = await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, D(3), "bulk_receipt", Guid.CreateVersion7(), lines));
        posted.Entries.Count.ShouldBe(210);
        posted.JournalEntryId.ShouldNotBeNull("every scope of a large document is valued and booked in the same posting");
        var total = Enumerable.Range(0, 210).Sum(static i => (1m + i) * 10m);
        (await ValuationAsync(s, D(3))).GetProperty("totalValue").GetDecimal().ShouldBe(total);
        (await InventoryBookedAsync(s, D(3))).ShouldBe(total);
        await s.Owner.AssertInvariantsAsync();
    }
}

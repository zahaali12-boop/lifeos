using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>Stock counts (roadmap 3.6, hard scenario 11): the warehouse keeps working during the count and the variances are still right.</summary>
[Collection(ApiCollection.Name)]
public sealed class CountTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Main, Guid Branch);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي"), binsEnabled = true });
        var branch = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra branch", "فرع البصرة") });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), branch.GetProperty("id").GetGuid());
    }

    private static DateOnly D(int day) => new(2026, 9, day);

    private Task<StockPostingResult> PostAsync(Setup s, StockLine line, DateOnly date) =>
        host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, date, "test_document", Guid.CreateVersion7(), [line]));

    [Fact]
    public async Task A_count_runs_while_the_warehouse_keeps_working_and_the_variances_are_taken_against_the_snapshot_plus_the_movements_since()
    {
        var s = await SetUpAsync();
        var binA = (await s.Owner.PostAsync($"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "A-01" })).GetProperty("id").GetGuid();
        var binB = (await s.Owner.PostAsync($"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "B-01" })).GetProperty("id").GetGuid();
        var tea = (await s.Owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var jam = (await s.Owner.PostAsync("/api/v1/items", new { code = "JAM", name = Name("Jam", "مربى"), baseUom = "PCS", tracking = "lot" })).GetProperty("id").GetGuid();
        var lamp = (await s.Owner.PostAsync("/api/v1/items", new { code = "LAMP", name = Name("Lamp", "مصباح"), baseUom = "PCS", tracking = "serial" })).GetProperty("id").GetGuid();
        await PostAsync(s, new StockLine(tea, StockEntryTypes.PurchaseReceipt, 100m, s.Main, UnitCost: 2m, BinId: binA), D(1));
        await PostAsync(s, new StockLine(tea, StockEntryTypes.PurchaseReceipt, 40m, s.Main, UnitCost: 2m, BinId: binB), D(1));
        await PostAsync(s, new StockLine(jam, StockEntryTypes.PurchaseReceipt, 30m, s.Main, UnitCost: 5m, BinId: binA, LotNumber: "J-1"), D(1));
        await PostAsync(s, new StockLine(lamp, StockEntryTypes.PurchaseReceipt, 2m, s.Main, UnitCost: 50m, BinId: binB, SerialNumbers: ["L1", "L2"]), D(1));
        var shortage = (await s.Owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "CNT-SHORT", name = Name("Count shortage", "نقص جرد"), appliesTo = "count" })).GetProperty("id").GetGuid();
        var surplus = (await s.Owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "CNT-FOUND", name = Name("Count surplus", "فائض جرد"), appliesTo = "count", requiresNote = true })).GetProperty("id").GetGuid();

        // Plan and freeze a full count of MAIN on the 10th: the sheet has one line per item, bin, lot and serial.
        var planned = await s.Owner.PostAsync("/api/v1/inventory/counts", new { companyId = s.CompanyId, warehouseId = s.Main, scope = "full", postingDate = "2026-09-10", blind = true });
        planned.GetProperty("status").GetString().ShouldBe("planned");
        var countId = planned.GetProperty("id").GetGuid();
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/review", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("count.not_counting");
        var frozen = await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/freeze", new { }, HttpStatusCode.OK);
        frozen.GetProperty("status").GetString().ShouldBe("frozen");
        frozen.GetProperty("number").GetString().ShouldStartWith("CNT-2026-");
        frozen.GetProperty("lineCount").GetInt32().ShouldBe(5);
        frozen.GetProperty("lastSequence").GetInt64().ShouldBeGreaterThan(0);
        var sheet = await s.Owner.GetOkAsync($"/api/v1/inventory/counts/{countId}/sheet");
        sheet.GetProperty("lines").EnumerateArray().ShouldAllBe(static l => l.GetProperty("expectedQty").ValueKind == JsonValueKind.Null, "a blind count hides the expected quantities while it is open");
        var lines = sheet.GetProperty("lines").EnumerateArray().ToDictionary(static l => $"{l.GetProperty("itemCode").GetString()}/{l.GetProperty("binCode").GetString()}/{l.GetProperty("lotNumber").GetString()}/{l.GetProperty("serialNumber").GetString()}", static l => l.GetProperty("id").GetGuid(), StringComparer.Ordinal);
        lines.Keys.ShouldBe(["JAM/A-01/J-1/", "LAMP/B-01//L1", "LAMP/B-01//L2", "TEA/A-01//", "TEA/B-01//"], ignoreOrder: true);

        // The warehouse keeps working: 10 tea leave bin A and 5 arrive in bin B after the freeze, and the counters see the shelves as they are now.
        await PostAsync(s, new StockLine(tea, StockEntryTypes.SaleShipment, 10m, s.Main, BinId: binA), D(8));
        await PostAsync(s, new StockLine(tea, StockEntryTypes.PurchaseReceipt, 5m, s.Main, UnitCost: 2m, BinId: binB), D(9));
        await PostAsync(s, new StockLine(tea, StockEntryTypes.PurchaseReceipt, 7m, s.Main, UnitCost: 2m, BinId: binB), D(12));
        var entries = new object[]
        {
            new { lineId = lines["TEA/A-01//"], countedQty = 88 },     // 100 − 10 moved = 90 expected now; 2 missing
            new { lineId = lines["TEA/B-01//"], countedQty = 45 },     // 40 + 5 moved = 45; the receipt of the 12th is after the count date and does not count
            new { lineId = lines["JAM/A-01/J-1/"], countedQty = 30 },
            new { lineId = lines["LAMP/B-01//L1"], countedQty = 1 },
            new { lineId = lines["LAMP/B-01//L2"], countedQty = 0 },  // missing
            new { itemCode = "JAM", binId = binB, lotNumber = "J-1", countedQty = 3 },  // found in the wrong bin: nothing expected there
        };
        var counted = await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/entries", new { entries }, HttpStatusCode.OK);
        counted.GetProperty("count").GetProperty("status").GetString().ShouldBe("counting");
        counted.GetProperty("count").GetProperty("countedLines").GetInt32().ShouldBe(6);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/entries", new { entries = new object[] { new { lineId = lines["LAMP/B-01//L1"], countedQty = 2 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("count.serial_quantity");

        // A recount of bin A tea: the review waits for it; the second count replaces the first and keeps it for the record.
        await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/lines/{lines["TEA/A-01//"]}/recount", new { }, HttpStatusCode.OK);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/review", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("count.lines_uncounted");
        await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/entries", new { entries = new object[] { new { lineId = lines["TEA/A-01//"], countedQty = 89 } } }, HttpStatusCode.OK);

        // Review: variances against expected + movements since the freeze up to the count date.
        var review = await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/review", new { }, HttpStatusCode.OK);
        review.GetProperty("count").GetProperty("status").GetString().ShouldBe("review");
        var reviewed = review.GetProperty("lines").EnumerateArray().ToDictionary(static l => $"{l.GetProperty("itemCode").GetString()}/{l.GetProperty("binCode").GetString()}/{l.GetProperty("lotNumber").GetString()}/{l.GetProperty("serialNumber").GetString()}", static l => l, StringComparer.Ordinal);
        reviewed["TEA/A-01//"].GetProperty("expectedQty").GetDecimal().ShouldBe(100m);
        reviewed["TEA/A-01//"].GetProperty("movementSinceFreeze").GetDecimal().ShouldBe(-10m);
        reviewed["TEA/A-01//"].GetProperty("previousCountedQty").GetDecimal().ShouldBe(88m);
        reviewed["TEA/A-01//"].GetProperty("varianceQty").GetDecimal().ShouldBe(-1m);
        reviewed["TEA/A-01//"].GetProperty("varianceValue").GetDecimal().ShouldBe(-2m);
        reviewed["TEA/B-01//"].GetProperty("movementSinceFreeze").GetDecimal().ShouldBe(5m, "the receipt after the count date is not a movement of this count");
        reviewed["TEA/B-01//"].GetProperty("varianceQty").GetDecimal().ShouldBe(0m);
        reviewed["JAM/A-01/J-1/"].GetProperty("varianceQty").GetDecimal().ShouldBe(0m);
        reviewed["JAM/B-01/J-1/"].GetProperty("expectedQty").GetDecimal().ShouldBe(0m);
        reviewed["JAM/B-01/J-1/"].GetProperty("varianceQty").GetDecimal().ShouldBe(3m);
        reviewed["LAMP/B-01//L2"].GetProperty("varianceQty").GetDecimal().ShouldBe(-1m);
        review.GetProperty("count").GetProperty("varianceLines").GetInt32().ShouldBe(3);

        // Reasons on every variance (a note where the reason demands it), approval, posting.
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/counts/{countId}/approve", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("count.reason_required");
        (await s.Owner.PutErrorAsync($"/api/v1/inventory/counts/{countId}/lines/{lines["TEA/A-01//"]}/reason", new { reasonCode = "CNT-FOUND" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("count.note_required");
        await s.Owner.PutAsync($"/api/v1/inventory/counts/{countId}/lines/{lines["TEA/A-01//"]}/reason", new { reasonCodeId = shortage });
        await s.Owner.PutAsync($"/api/v1/inventory/counts/{countId}/lines/{lines["LAMP/B-01//L2"]}/reason", new { reasonCodeId = shortage, note = "Not on the shelf" });
        var jamB = reviewed["JAM/B-01/J-1/"].GetProperty("id").GetGuid();
        await s.Owner.PutAsync($"/api/v1/inventory/counts/{countId}/lines/{jamB}/reason", new { reasonCodeId = surplus, note = "Misplaced during picking" });
        var approved = await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/approve", new { }, HttpStatusCode.OK);
        approved.GetProperty("status").GetString().ShouldBe("approved");
        var posted = await s.Owner.PostAsync($"/api/v1/inventory/counts/{countId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("journalEntryId").GetGuid().ShouldNotBe(Guid.Empty);

        // The books and the balances: tea in A is 89 (100 − 10 − 1), jam sits 30 in A and 3 in B, lamp L2 is scrapped.
        var balances = (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&warehouseId={s.Main}")).EnumerateArray()
            .Select(static b => ($"{b.GetProperty("itemCode").GetString()}/{b.GetProperty("binCode").GetString()}/{b.GetProperty("serialNumber").GetString()}", b.GetProperty("onHand").GetDecimal())).ToList();
        balances.ShouldBe([("JAM/A-01/", 30m), ("JAM/B-01/", 3m), ("LAMP/B-01/L1", 1m), ("TEA/A-01/", 89m), ("TEA/B-01/", 52m)]);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/serials/by-number?itemId={lamp}&serialNumber=L2")).GetProperty("status").GetString().ShouldBe("scrapped");
        var ledger = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/ledger?companyId={s.CompanyId}&sourceDocumentType=stock_count&sourceDocumentId={countId}");
        ledger.GetProperty("items").EnumerateArray().Select(static e => (e.GetProperty("entryType").GetString(), e.GetProperty("quantity").GetDecimal())).ShouldBe([("count_variance", -1m), ("count_variance", 3m), ("count_variance", -1m)], ignoreOrder: true);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/counts?companyId={s.CompanyId}&status=posted")).GetArrayLength().ShouldBe(1);
        await s.Owner.AssertInvariantsAsync();

        // A count that blocks movements: a bins-scoped count of B-01 stops movements in B-01 and nowhere else, until it is cancelled.
        var blocking = await s.Owner.PostAsync("/api/v1/inventory/counts", new { companyId = s.CompanyId, warehouseId = s.Main, scope = "bins", binIds = new[] { binB }, postingDate = "2026-09-15", blockMovements = true });
        var blockingId = blocking.GetProperty("id").GetGuid();
        (await s.Owner.PostAsync($"/api/v1/inventory/counts/{blockingId}/freeze", new { }, HttpStatusCode.OK)).GetProperty("lineCount").GetInt32().ShouldBe(3, "tea, jam and lamp L1 in bin B; the scrapped L2 has nothing on hand");
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(s, new StockLine(tea, StockEntryTypes.SaleShipment, 1m, s.Main, BinId: binB), D(15)));
        refused.Message.ShouldContain("stock.warehouse_counting");
        await PostAsync(s, new StockLine(tea, StockEntryTypes.SaleShipment, 1m, s.Main, BinId: binA), D(15));
        await s.Owner.PostAsync($"/api/v1/inventory/counts/{blockingId}/cancel", new { }, HttpStatusCode.OK);
        await PostAsync(s, new StockLine(tea, StockEntryTypes.SaleShipment, 1m, s.Main, BinId: binB), D(15));

        // A cycle count by class: only the items whose warehouse settings put them in class A.
        await s.Owner.PutAsync($"/api/v1/items/{jam}/warehouse-settings/{s.Main}", new { cycleCountClass = "A" });
        var cycle = await s.Owner.PostAsync("/api/v1/inventory/counts", new { companyId = s.CompanyId, warehouseId = s.Main, scope = "cycle", cycleCountClasses = new[] { "a" }, postingDate = "2026-09-16" });
        (await s.Owner.PostAsync($"/api/v1/inventory/counts/{cycle.GetProperty("id").GetGuid()}/freeze", new { }, HttpStatusCode.OK)).GetProperty("lineCount").GetInt32().ShouldBe(2, "jam in two bins");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/counts", new { companyId = s.CompanyId, warehouseId = s.Branch, scope = "bins", binIds = new[] { binA } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("count.bins_required");
        await s.Owner.AssertInvariantsAsync();
    }
}

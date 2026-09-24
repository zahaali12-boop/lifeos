using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;

namespace Quicker.Inventory.Tests;

/// <summary>Slow-moving stock: on-hand stock not sold or consumed for a number of days at a date, valued for those who may see costs.</summary>
[Collection(ApiCollection.Name)]
public sealed class SlowMovingTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static DateOnly D(int day) => new(2026, 9, day);

    [Fact]
    public async Task Stock_not_sold_or_consumed_for_the_days_asked_is_listed_longest_idle_first_with_its_value()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "SLW", legalName = Name("Slow Co", "شركة البطء"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var main = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        var branch = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra", "البصرة") })).GetProperty("id").GetGuid();
        async Task<Guid> ItemAsync(string code) => (await owner.PostAsync("/api/v1/items", new { code, name = Name(code, code), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var tea = await ItemAsync("TEA");
        var jam = await ItemAsync("JAM");
        var rice = await ItemAsync("RICE");
        var salt = await ItemAsync("SALT");
        async Task PostAsync(Guid item, string type, decimal quantity, DateOnly date, Guid warehouse, decimal? cost = null) =>
            (await host.PostStockAsync(ws.TenantId, new StockPostingRequest(companyId, date, "test_document", Guid.CreateVersion7(), [new StockLine(item, type, quantity, warehouse, UnitCost: cost)]))).ShouldNotBeNull();

        // Tea sold on the 10th; jam never used, part of it moved to Basra on the 15th (a move is not a use); rice sold on
        // the 18th; salt sold out.
        await PostAsync(tea, StockEntryTypes.PurchaseReceipt, 100m, D(1), main, 2m);
        await PostAsync(tea, StockEntryTypes.SaleShipment, 10m, D(10), main);
        await PostAsync(jam, StockEntryTypes.PurchaseReceipt, 50m, D(2), main, 5m);
        await PostAsync(jam, StockEntryTypes.TransferOut, 20m, D(15), main);
        await PostAsync(jam, StockEntryTypes.TransferIn, 20m, D(15), branch);
        await PostAsync(rice, StockEntryTypes.PurchaseReceipt, 20m, D(3), main, 3m);
        await PostAsync(rice, StockEntryTypes.SaleShipment, 5m, D(18), main);
        await PostAsync(salt, StockEntryTypes.PurchaseReceipt, 5m, D(3), main, 1m);
        await PostAsync(salt, StockEntryTypes.SaleShipment, 5m, D(4), main);

        // Idle ten days or more on the 20th: jam in Main (18 days since it came in), then tea (10 days since last sold).
        var report = await owner.GetOkAsync($"/api/v1/inventory/stock/slow-moving?companyId={companyId}&asOf=2026-09-20&idleDays=10");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        rows.Select(static r => (r.GetProperty("itemCode").GetString(), r.GetProperty("warehouseCode").GetString(), r.GetProperty("idleDays").GetInt32(), r.GetProperty("onHand").GetDecimal()))
            .ShouldBe([("JAM", "MAIN", 18, 30m), ("TEA", "MAIN", 10, 90m)]);
        rows[0].GetProperty("lastUsed").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[0].GetProperty("firstReceived").GetString().ShouldBe("2026-09-02");
        rows[1].GetProperty("lastUsed").GetString().ShouldBe("2026-09-10");
        (rows[0].GetProperty("value").GetDecimal(), rows[1].GetProperty("value").GetDecimal()).ShouldBe((150m, 180m));
        report.GetProperty("totalValue").GetDecimal().ShouldBe(330m);

        // With no minimum every item with stock shows; salt, sold out, never does. At an earlier date, as things stood then.
        var all = (await owner.GetOkAsync($"/api/v1/inventory/stock/slow-moving?companyId={companyId}&asOf=2026-09-20&idleDays=0")).GetProperty("rows").EnumerateArray()
            .Select(static r => $"{r.GetProperty("itemCode").GetString()}@{r.GetProperty("warehouseCode").GetString()}").ToList();
        all.ShouldBe(["JAM@MAIN", "TEA@MAIN", "JAM@BASRA", "RICE@MAIN"]);
        var early = (await owner.GetOkAsync($"/api/v1/inventory/stock/slow-moving?companyId={companyId}&asOf=2026-09-05&idleDays=0")).GetProperty("rows").EnumerateArray().ToList();
        early.Single(static r => r.GetProperty("itemCode").GetString() == "TEA").GetProperty("onHand").GetDecimal().ShouldBe(100m);
        (await owner.GetAsync($"/api/v1/inventory/stock/slow-moving?companyId={companyId}&idleDays=-1")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Someone who may read stock but not costs sees the same rows without values.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "storekeeper", name = Name("Storekeeper", "أمين المخزن"), description = "", grants = new[] { "inventory.stock.read" } });
        var email = $"store-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Storekeeper", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        using var storekeeper = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        var seen = await storekeeper.GetOkAsync($"/api/v1/inventory/stock/slow-moving?companyId={companyId}&asOf=2026-09-20&idleDays=10");
        seen.GetProperty("rows").GetArrayLength().ShouldBe(2);
        seen.GetProperty("rows")[0].GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        seen.GetProperty("totalValue").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}

using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// The trader's lists (ADR-0035 §18): requests for quotation and shipments newest first, and bounded by the days they
/// were made in the company's time zone when days are asked for.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ListFiltersTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task Requests_and_shipments_list_newest_first_and_within_the_days_asked_for()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RFL", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 30 });

        // Two comparisons made one after the other: the later one first. The fixture's day is 22 September 2026 in Baghdad.
        await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "First", partnerIds = new[] { supplier }, lines = new[] { new { itemId = pads, quantity = 10m, uom = "PCS" } } });
        await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Second", partnerIds = new[] { supplier }, lines = new[] { new { itemId = pads, quantity = 20m, uom = "PCS" } } });
        var rfqs = await owner.GetOkAsync($"/api/v1/purchasing/rfqs?companyId={companyId}");
        rfqs.EnumerateArray().Select(static r => r.GetProperty("title").GetString()).ShouldBe(["Second", "First"]);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs?companyId={companyId}&from=2026-09-22&to=2026-09-22")).GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs?companyId={companyId}&from=2026-09-23")).GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs?companyId={companyId}&to=2026-09-21")).GetArrayLength().ShouldBe(0);

        // Two shipments the same way.
        var firstOrder = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "IQD", orderDate = "2026-09-22", lines = new[] { new { itemId = pads, quantity = 10m, uom = "PCS", unitPrice = 1000m } } });
        var firstShipment = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = firstOrder.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        var secondOrder = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId, partnerId = supplier, warehouseId, currency = "IQD", orderDate = "2026-09-22", lines = new[] { new { itemId = pads, quantity = 20m, uom = "PCS", unitPrice = 1000m } } });
        var secondShipment = (await owner.PostAsync("/api/v1/purchasing/shipments", new { orderId = secondOrder.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        var shipments = await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={companyId}");
        shipments.EnumerateArray().Select(static s => s.GetProperty("id").GetGuid()).ShouldBe([secondShipment, firstShipment]);
        (await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={companyId}&from=2026-09-22&to=2026-09-22")).GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={companyId}&from=2026-09-23")).GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/purchasing/shipments?companyId={companyId}&to=2026-09-21")).GetArrayLength().ShouldBe(0);
    }
}

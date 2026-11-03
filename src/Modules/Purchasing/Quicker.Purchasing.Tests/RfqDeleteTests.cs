using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// A comparison the trader is done with goes away for good (ADR-0035 §13): a request with suppliers invited and a quote
/// recorded is deleted with all of it, while one that became an order is kept and says so.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RfqDeleteTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task A_request_that_led_to_no_order_is_deleted_with_its_quotes_and_an_awarded_one_is_kept()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RFD", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 30 });

        // A comparison with one offer in it: deleted, and gone.
        var rfq = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Throwaway", partnerIds = new[] { supplier }, lines = new[] { new { itemId = pads, quantity = 10m, uom = "PCS" } } });
        var rfqId = rfq.GetProperty("id").GetGuid();
        var lineId = rfq.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = supplier, currency = "IQD", leadTimeDays = 30, lines = new[] { new { rfqLineId = lineId, unitPrice = 14000m } } }, HttpStatusCode.OK);
        await owner.DeleteOkAsync($"/api/v1/purchasing/rfqs/{rfqId}");
        (await owner.GetAsync($"/api/v1/purchasing/rfqs/{rfqId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs?companyId={companyId}")).GetArrayLength().ShouldBe(0);

        // One that became an order is kept.
        var kept = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Ordered", partnerIds = new[] { supplier }, lines = new[] { new { itemId = pads, quantity = 10m, uom = "PCS" } } });
        var keptId = kept.GetProperty("id").GetGuid();
        var quoted = await owner.PostAsync($"/api/v1/purchasing/rfqs/{keptId}/quotes", new { partnerId = supplier, currency = "IQD", leadTimeDays = 30, lines = new[] { new { rfqLineId = kept.GetProperty("lines")[0].GetProperty("id").GetGuid(), unitPrice = 14000m } } }, HttpStatusCode.OK);
        var quoteId = quoted.GetProperty("quotes")[0].GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/rfqs/{keptId}/award", new { quoteId, warehouseId }, HttpStatusCode.OK);
        var response = await owner.DeleteAsync(new Uri($"/api/v1/purchasing/rfqs/{keptId}", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs/{keptId}")).GetProperty("status").GetString().ShouldBe("awarded");
    }
}

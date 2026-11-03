using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// The choice of offer is the buyer's (ADR-0035 §17): a supplier that priced only some lines is ordered from for those
/// lines, the rest being left out and named; a quote that prices nothing is the one refusal left.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RfqPartialAwardTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task A_supplier_that_priced_some_lines_is_ordered_from_for_those_lines()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RFP", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main store", "المخزن الرئيسي") })).GetProperty("id").GetGuid();
        var pads = (await owner.PostAsync("/api/v1/items", new { code = "PADS", name = Name("Brake pads", "فحمات فرامل"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var filter = (await owner.PostAsync("/api/v1/items", new { code = "FILTER", name = Name("Oil filter", "فلتر زيت"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SHENZ", legalName = Name("Shenzhen Auto Parts", "شنجن لقطع السيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 30 });

        // Pads and filters asked for; the supplier prices the pads only.
        var rfq = await owner.PostAsync("/api/v1/purchasing/rfqs", new { companyId, title = "Autumn", partnerIds = new[] { supplier }, lines = new[] { new { itemId = pads, quantity = 100m, uom = "PCS" }, new { itemId = filter, quantity = 50m, uom = "PCS" } } });
        var rfqId = rfq.GetProperty("id").GetGuid();
        var padsLine = rfq.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var quoted = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/quotes", new { partnerId = supplier, currency = "IQD", leadTimeDays = 30, lines = new[] { new { rfqLineId = padsLine, unitPrice = 14000m } } }, HttpStatusCode.OK);
        var quoteId = quoted.GetProperty("quotes")[0].GetProperty("id").GetGuid();

        // Ordered from anyway, for the pads; the filters stay unordered and the request says it was awarded.
        var order = await owner.PostAsync($"/api/v1/purchasing/rfqs/{rfqId}/award", new { quoteId, warehouseId }, HttpStatusCode.OK);
        var lines = (await owner.GetOkAsync($"/api/v1/purchasing/orders/{order.GetProperty("id").GetGuid()}")).GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(1);
        lines[0].GetProperty("itemId").GetGuid().ShouldBe(pads);
        lines[0].GetProperty("quantity").GetDecimal().ShouldBe(100m);
        (await owner.GetOkAsync($"/api/v1/purchasing/rfqs/{rfqId}")).GetProperty("status").GetString().ShouldBe("awarded");
        // (A quote with no line at all cannot be recorded: quote.lines_required guards it before any award.)
    }
}

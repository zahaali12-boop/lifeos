using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>The sales lines read model (founder programme item 1f): every line with what it can be grouped by, credit notes negative, posted only by default, the company's reader only.</summary>
[Collection(ApiCollection.Name)]
public sealed class SalesLinesTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Lines_carry_their_document_customer_item_and_period_and_credit_notes_count_against()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "SLN", legalName = Name("Lines Co", "شركة البنود"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var group = await owner.PostAsync("/api/v1/partners/customer-groups", new { code = "RETAIL", name = Name("Retail", "تجزئة") });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "IQD", customerGroupId = group.GetProperty("id").GetGuid() });
        await owner.PostAsync("/api/v1/items/categories", new { code = "DRINKS", name = Name("Drinks", "مشروبات") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea service", "خدمة شاي"), type = "service", baseUom = "PCS", categoryCode = "DRINKS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();

        var on = new DateOnly(2026, 9, 22);
        var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = customer, documentDate = Iso(on), lines = new object[] { new { kind = "item", itemId = tea, quantity = 3m, unitPrice = 1_000m }, new { kind = "service", quantity = 1m, unitPrice = 500m, description = "Delivery" } } });
        await owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var credit = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = customer, kind = "credit_note", documentDate = Iso(on.AddDays(1)), lines = new[] { new { kind = "item", itemId = tea, quantity = 1m, unitPrice = 1_000m } } });
        await owner.PostAsync($"/api/v1/sales/invoices/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        // A draft stays out unless asked for.
        await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = customer, documentDate = Iso(on), lines = new[] { new { kind = "service", quantity = 1m, unitPrice = 9m, description = "Draft" } } });

        var report = await owner.GetOkAsync($"/api/v1/reporting/sales-lines?companyId={companyId}&from=2026-09-01&to=2026-09-30");
        report.GetProperty("functionalCurrency").GetString().ShouldBe("IQD");
        report.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(3);
        var teaLine = rows.Single(r => r.GetProperty("kind").GetString() == "invoice" && r.GetProperty("itemCode").GetString() == "TEA");
        teaLine.GetProperty("customerCode").GetString().ShouldBe("CUS-A");
        teaLine.GetProperty("customerName").GetProperty("ar").GetString().ShouldBe("ألفا");
        teaLine.GetProperty("customerGroup").GetString().ShouldBe("RETAIL");
        teaLine.GetProperty("category").GetString().ShouldBe("DRINKS");
        teaLine.GetProperty("uom").GetString().ShouldBe("PCS");
        teaLine.GetProperty("month").GetString().ShouldBe("2026-09");
        teaLine.GetProperty("quarter").GetString().ShouldBe("2026-Q3");
        teaLine.GetProperty("year").GetString().ShouldBe("2026");
        teaLine.GetProperty("quantity").GetDecimal().ShouldBe(3m);
        teaLine.GetProperty("netAmount").GetDecimal().ShouldBe(3_000m);
        teaLine.GetProperty("grossAmount").GetDecimal().ShouldBe(3_000m, "no tax regime, so gross equals net");
        var service = rows.Single(r => r.GetProperty("lineKind").GetString() == "service");
        service.GetProperty("description").GetString().ShouldBe("Delivery");
        service.GetProperty("itemCode").ValueKind.ShouldBe(JsonValueKind.Null);
        var creditLine = rows.Single(r => r.GetProperty("kind").GetString() == "credit_note");
        creditLine.GetProperty("quantity").GetDecimal().ShouldBe(-1m);
        creditLine.GetProperty("netAmount").GetDecimal().ShouldBe(-1_000m);
        rows.Sum(static r => r.GetProperty("netAmount").GetDecimal()).ShouldBe(2_500m, "the credit note nets off");

        // Drafts on request; the range is checked; the year to date when no range is given.
        (await owner.GetOkAsync($"/api/v1/reporting/sales-lines?companyId={companyId}&from=2026-09-01&to=2026-09-30&status=posted,draft")).GetProperty("rows").GetArrayLength().ShouldBe(4);
        var bad = await owner.GetAsync($"/api/v1/reporting/sales-lines?companyId={companyId}&from=2026-10-01&to=2026-09-01");
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await bad.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("report.range_invalid");

        // A member without the sales invoice permission is refused.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "stores", name = Name("Stores", "مخازن"), description = "", grants = new[] { "inventory.item.read" } });
        var email = $"stores-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Stores", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var stores = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await stores.GetAsync($"/api/v1/reporting/sales-lines?companyId={companyId}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

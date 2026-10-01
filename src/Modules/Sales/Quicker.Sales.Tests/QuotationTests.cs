using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Sales quotations (roadmap 5.4a): a line priced by the item's own list price (no price list needed), a customer
/// with no tax registration yet (every line honestly says "not registered"), sent, accepted and rejected, permission
/// scoping, and a quotation whose validity has passed refusing acceptance.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuotationTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Tea, Guid Cup, Guid Customer);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRD", legalName = Name("Trading Co", "شركة التجارة"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 100m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var cup = (await owner.PostAsync("/api/v1/items", new { code = "CUP", name = Name("Tea glass", "استكان"), baseUom = "PCS", listPrice = 5m, listPriceCurrency = "USD" })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BAGHDAD-MALL", legalName = Name("Baghdad Mall LLC", "بغداد مول"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" });
        return new Setup(ws, owner, company, tea, cup, customer);
    }

    [Fact]
    public async Task A_quotation_prices_its_lines_from_the_items_own_list_price_is_sent_accepted_and_the_harness_holds()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        var quotation = await owner.PostAsync("/api/v1/sales/quotations", new
        {
            companyId = s.CompanyId,
            partnerId = s.Customer,
            lines = new object[]
            {
                new { itemId = s.Tea, quantity = 2m },
                new { itemId = s.Cup, quantity = 3m },
            },
        });

        quotation.GetProperty("status").GetString().ShouldBe("draft");
        quotation.GetProperty("number").GetString().ShouldStartWith("QT-");
        var lines = quotation.GetProperty("lines");
        lines.GetArrayLength().ShouldBe(2);
        lines[0].GetProperty("unitPrice").GetDecimal().ShouldBe(100m);
        lines[0].GetProperty("netAmount").GetDecimal().ShouldBe(200m);
        lines[1].GetProperty("netAmount").GetDecimal().ShouldBe(15m);
        // No regime is registered yet, so tax honestly carries none rather than guessing (A-146).
        lines[0].GetProperty("taxAmount").GetDecimal().ShouldBe(0m);
        lines[0].GetProperty("taxReason").GetString().ShouldBe("not_registered");
        quotation.GetProperty("totalNet").GetDecimal().ShouldBe(215m);
        quotation.GetProperty("totalTax").GetDecimal().ShouldBe(0m);
        quotation.GetProperty("totalGross").GetDecimal().ShouldBe(215m);
        var id = quotation.GetProperty("id").GetGuid();

        var sent = await owner.PostAsync($"/api/v1/sales/quotations/{id}/send", new { }, HttpStatusCode.OK);
        sent.GetProperty("status").GetString().ShouldBe("sent");

        var accepted = await owner.PostAsync($"/api/v1/sales/quotations/{id}/accept", new { }, HttpStatusCode.OK);
        accepted.GetProperty("status").GetString().ShouldBe("accepted");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_sent_quotation_is_rejected_with_a_reason()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var quotation = await owner.PostAsync("/api/v1/sales/quotations", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } });
        var id = quotation.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/quotations/{id}/send", new { }, HttpStatusCode.OK);

        var (code, _) = await owner.PostErrorAsync($"/api/v1/sales/quotations/{id}/reject", new { reason = "" }, HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("quotation.reason_required");

        var rejected = await owner.PostAsync($"/api/v1/sales/quotations/{id}/reject", new { reason = "Found it cheaper elsewhere" }, HttpStatusCode.OK);
        rejected.GetProperty("status").GetString().ShouldBe("rejected");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Found it cheaper elsewhere");
    }

    [Fact]
    public async Task Accepting_a_quotation_past_its_validity_is_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var quotation = await owner.PostAsync("/api/v1/sales/quotations", new
        {
            companyId = s.CompanyId,
            partnerId = s.Customer,
            quoteDate = "2026-01-05",
            validUntil = "2026-01-31",
            lines = new object[] { new { itemId = s.Tea, quantity = 1m } },
        });
        var id = quotation.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/quotations/{id}/send", new { }, HttpStatusCode.OK);

        var (code, _) = await owner.PostErrorAsync($"/api/v1/sales/quotations/{id}/accept", new { }, HttpStatusCode.Conflict);
        code.ShouldBe("quotation.expired");
    }

    [Fact]
    public async Task A_member_with_only_quote_read_lists_and_reads_but_may_not_create()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PostAsync("/api/v1/sales/quotations", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } });

        var role = (await owner.PostAsync("/api/v1/roles", new { code = "sales_quote_reader", name = Name("Quote viewer", "مشاهد عروض الأسعار"), description = "", grants = new[] { SalesPermissions.QuoteRead } })).GetProperty("id").GetGuid();
        var email = $"quote-viewer-{s.Ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Quote viewer", roleIds = Array.Empty<Guid>() }, HttpStatusCode.Created);
        (await owner.PostAsJsonAsync($"/api/v1/users/{invited.GetProperty("membershipId").GetGuid()}/assignments", new { roleId = role, scopes = new[] { new { scopeType = "company", scopeId = s.CompanyId } } }, ApiFixture.Json)).EnsureSuccessStatusCode();
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        var viewer = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        (await viewer.GetOkAsync("/api/v1/sales/quotations")).GetArrayLength().ShouldBe(1);
        var (code, _) = await viewer.PostErrorAsync("/api/v1/sales/quotations", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new object[] { new { itemId = s.Tea, quantity = 1m } } }, HttpStatusCode.Forbidden);
        code.ShouldNotBeNull();
    }
}

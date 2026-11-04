using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>The home page's numbers (founder programme item 1) against the real modules: sales, receivables, cash and the pipeline add up to what was posted, and a member sees only the sections their grant covers.</summary>
[Collection(ApiCollection.Name)]
public sealed class HomeDashboardTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Customer, Guid Cash, Guid Bank);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "HOME", legalName = Name("Home Co", "شركة البداية"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "IQD" });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main account", "الحساب الرئيسي"), kind = "bank", currency = "IQD" })).GetProperty("id").GetGuid();
        return new Setup(ws, owner, companyId, customer, cash, bank);
    }

    /// <summary>A service invoice to the customer, posted on the date; its open item.</summary>
    private static async Task<Guid> InvoiceAsync(Setup s, DateOnly date, decimal amount)
    {
        var invoice = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, documentDate = Iso(date), lines = new[] { new { kind = "service", quantity = 1m, unitPrice = amount, description = "Consulting" } } });
        var posted = await s.Owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<JsonElement> HomeAsync(HttpClient client, Guid companyId, DateOnly asOf) =>
        client.GetOkAsync($"/api/v1/reporting/home?companyId={companyId}&asOf={Iso(asOf)}");

    [Fact]
    public async Task The_home_page_adds_up_sales_receivables_cash_and_the_pipeline_from_what_was_posted()
    {
        var s = await SetUpAsync();
        var today = new DateOnly(2026, 9, 22);

        // Nothing yet: every section present for the owner, all zero, thirty days of series.
        var empty = await HomeAsync(s.Owner, s.CompanyId, today);
        empty.GetProperty("functionalCurrency").GetString().ShouldBe("IQD");
        empty.GetProperty("sales").GetProperty("today").GetDecimal().ShouldBe(0m);
        empty.GetProperty("sales").GetProperty("days").GetArrayLength().ShouldBe(30);
        empty.GetProperty("receivables").GetProperty("total").GetDecimal().ShouldBe(0m);
        empty.GetProperty("cash").GetProperty("accounts").GetArrayLength().ShouldBe(2);
        empty.GetProperty("pipeline").GetProperty("openQuotations").GetInt32().ShouldBe(0);

        // Three invoices: today, earlier this month, and last month (past due at 60 days); a credit note today counts against.
        var overdueItem = await InvoiceAsync(s, today.AddDays(-60), 300_000m);
        await InvoiceAsync(s, today.AddDays(-10), 200_000m);
        var todayItem = await InvoiceAsync(s, today, 500_000m);
        var credit = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "credit_note", documentDate = Iso(today), lines = new[] { new { kind = "service", quantity = 1m, unitPrice = 50_000m, description = "Goodwill" } } });
        await s.Owner.PostAsync($"/api/v1/sales/invoices/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        // A draft stays out of the numbers but is counted as waiting.
        await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, documentDate = Iso(today), lines = new[] { new { kind = "service", quantity = 1m, unitPrice = 1m, description = "Draft" } } });
        // Money in: the overdue invoice paid into the cash box today, the month's first receipt.
        var receipt = await s.Owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Cash, receiptDate = Iso(today), lines = new[] { new { openItemId = overdueItem, amount = 300_000m } } });
        await s.Owner.PostAsync($"/api/v1/banking/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var tea = (await s.Owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Black tea", "شاي أسود"), baseUom = "PCS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();
        await s.Owner.PostAsync("/api/v1/sales/quotations", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new[] { new { itemId = tea, quantity = 1m } } });

        var home = await HomeAsync(s.Owner, s.CompanyId, today);
        var sales = home.GetProperty("sales");
        sales.GetProperty("today").GetDecimal().ShouldBe(450_000m);
        sales.GetProperty("monthToDate").GetDecimal().ShouldBe(650_000m);
        sales.GetProperty("previousMonthToDate").GetDecimal().ShouldBe(0m, "the 60-day-old invoice is before last month's same day");
        sales.GetProperty("invoicesToday").GetInt32().ShouldBe(1);
        sales.GetProperty("draftInvoices").GetInt32().ShouldBe(1);
        var days = sales.GetProperty("days").EnumerateArray().ToList();
        days.Count.ShouldBe(30);
        days[^1].GetProperty("day").GetString().ShouldBe(Iso(today));
        days[^1].GetProperty("amount").GetDecimal().ShouldBe(450_000m);
        days.Sum(static d => d.GetProperty("amount").GetDecimal()).ShouldBe(650_000m);

        var receivables = home.GetProperty("receivables");
        // Without payment terms an invoice is due on its date: the 60-day one was just paid, the 10-day one is past due, today's is due within the week.
        receivables.GetProperty("overdue").GetDecimal().ShouldBe(200_000m);
        receivables.GetProperty("overdueCustomers").GetInt32().ShouldBe(1);
        receivables.GetProperty("dueSoon").GetDecimal().ShouldBe(500_000m);
        receivables.GetProperty("total").GetDecimal().ShouldBe(650_000m, "this month's invoices less the credit note");
        receivables.GetProperty("openItems").GetInt32().ShouldBe(3);
        todayItem.ShouldNotBe(Guid.Empty);

        var cash = home.GetProperty("cash");
        cash.GetProperty("total").GetDecimal().ShouldBe(300_000m);
        cash.GetProperty("receivedMonthToDate").GetDecimal().ShouldBe(300_000m);
        cash.GetProperty("paidMonthToDate").GetDecimal().ShouldBe(0m);
        var cashBox = cash.GetProperty("accounts").EnumerateArray().First();
        cashBox.GetProperty("code").GetString().ShouldBe("CASH", "cash boxes come before bank accounts");
        cashBox.GetProperty("balanceFc").GetDecimal().ShouldBe(300_000m);
        cashBox.GetProperty("name").GetProperty("ar").GetString().ShouldBe("الصندوق");

        home.GetProperty("pipeline").GetProperty("openQuotations").GetInt32().ShouldBe(1);
        home.GetProperty("pipeline").GetProperty("ordersToShip").GetInt32().ShouldBe(0);

        // Before the receipt, the oldest invoice was past due; the day before it was issued, nothing was.
        var yesterday = await HomeAsync(s.Owner, s.CompanyId, today.AddDays(-1));
        // Open items are read as they stand now (the paid invoice is settled); only "past due" is measured at the date.
        yesterday.GetProperty("receivables").GetProperty("overdue").GetDecimal().ShouldBe(200_000m);
        yesterday.GetProperty("receivables").GetProperty("overdueCustomers").GetInt32().ShouldBe(1);
        yesterday.GetProperty("sales").GetProperty("today").GetDecimal().ShouldBe(0m);
        yesterday.GetProperty("cash").GetProperty("total").GetDecimal().ShouldBe(0m);
        yesterday.GetProperty("payables").GetProperty("total").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task A_member_sees_only_the_sections_their_grant_covers_and_an_unknown_company_is_not_found()
    {
        var s = await SetUpAsync();
        var role = await s.Owner.PostAsync("/api/v1/roles", new { code = "seller", name = Name("Seller", "بائع"), description = "", grants = new[] { "sales.invoice.read", "sales.quote.read" } });
        var email = $"seller-{s.Ws.Slug}@example.test";
        await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Seller", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var seller = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        var home = await HomeAsync(seller, s.CompanyId, new DateOnly(2026, 9, 22));
        home.GetProperty("sales").ValueKind.ShouldBe(JsonValueKind.Object);
        home.GetProperty("receivables").ValueKind.ShouldBe(JsonValueKind.Null);
        home.GetProperty("payables").ValueKind.ShouldBe(JsonValueKind.Null);
        home.GetProperty("cash").ValueKind.ShouldBe(JsonValueKind.Null);
        var pipeline = home.GetProperty("pipeline");
        pipeline.GetProperty("openQuotations").GetInt32().ShouldBe(0);
        pipeline.GetProperty("ordersToShip").ValueKind.ShouldBe(JsonValueKind.Null);

        (await s.Owner.GetAsync($"/api/v1/reporting/home?companyId={Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

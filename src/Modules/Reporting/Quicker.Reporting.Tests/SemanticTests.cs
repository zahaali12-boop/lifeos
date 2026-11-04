using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>
/// The semantic layer (founder programme item 9, ADR-0034): the facts projected from the books, the catalogue of
/// models and fields, a query by customer with labels in both languages, by month with a filter, with a prior-period
/// comparison and a calculated field, top-N, a text filter, the explanation with the SQL, the drill to the lines and
/// their documents, the journal model balanced, the open items as they stand, a rebuild equal to the increments, and
/// a reader without the module's permission refused.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SemanticTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement Row(JsonElement result, string field, string key) => result.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty(field + "_key").GetString() == key);

    [Fact]
    public async Task Facts_are_projected_and_queried_with_labels_filters_comparisons_drill_explain_and_permissions()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "RPT", legalName = Name("Reports Co", "شركة التقارير"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var group = await owner.PostAsync("/api/v1/partners/customer-groups", new { code = "RETAIL", name = Name("Retail", "تجزئة") });
        var alpha = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{alpha}/customer-accounts/{companyId}", new { currency = "IQD", customerGroupId = group.GetProperty("id").GetGuid() });
        var beta = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-B", legalName = Name("Beta Stores", "بيتا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{beta}/customer-accounts/{companyId}", new { currency = "IQD" });
        await owner.PostAsync("/api/v1/items/categories", new { code = "DRINKS", name = Name("Drinks", "مشروبات") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea service", "خدمة شاي"), type = "service", baseUom = "PCS", categoryCode = "DRINKS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();

        async Task PostInvoiceAsync(Guid partner, DateOnly on, string kind, params object[] lines)
        {
            var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = partner, kind, documentDate = Iso(on), lines });
            await owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        }

        await PostInvoiceAsync(alpha, new DateOnly(2026, 8, 15), "invoice", new { kind = "item", itemId = tea, quantity = 5m, unitPrice = 1_000m });
        await PostInvoiceAsync(alpha, new DateOnly(2026, 9, 22), "invoice", new { kind = "item", itemId = tea, quantity = 3m, unitPrice = 1_000m }, new { kind = "service", quantity = 1m, unitPrice = 500m, description = "Delivery" });
        await PostInvoiceAsync(alpha, new DateOnly(2026, 9, 23), "credit_note", new { kind = "item", itemId = tea, quantity = 1m, unitPrice = 1_000m });
        await PostInvoiceAsync(beta, new DateOnly(2026, 9, 10), "invoice", new { kind = "item", itemId = tea, quantity = 2m, unitPrice = 1_000m });

        // The catalogue: the models the owner may read, each with its fields in both languages.
        var models = await owner.GetOkAsync("/api/v1/reporting/models");
        var sales = models.EnumerateArray().Single(static m => m.GetProperty("code").GetString() == "sales_lines");
        sales.GetProperty("name").GetProperty("ar").GetString().ShouldBe("بنود المبيعات");
        sales.GetProperty("canDrill").GetBoolean().ShouldBeTrue();
        var fields = sales.GetProperty("fields").EnumerateArray().ToDictionary(static f => f.GetProperty("key").GetString()!);
        fields["customer"].GetProperty("role").GetString().ShouldBe("dimension");
        fields["net_amount"].GetProperty("role").GetString().ShouldBe("measure");
        fields["net_amount"].GetProperty("type").GetString().ShouldBe("money");
        models.EnumerateArray().Select(static m => m.GetProperty("code").GetString()).ShouldContain("journal_lines");
        models.EnumerateArray().Select(static m => m.GetProperty("code").GetString()).ShouldContain("open_items");

        // The first projection copies everything posted so far.
        var projected = await owner.PostAsync("/api/v1/reporting/projections/refresh", new { }, HttpStatusCode.OK);
        projected.GetProperty("rowsProjected").GetInt64().ShouldBeGreaterThan(0);
        var status = await owner.GetOkAsync("/api/v1/reporting/projections");
        status.EnumerateArray().Single(static s => s.GetProperty("fact").GetString() == "sales_lines").GetProperty("rowCount").GetInt64().ShouldBe(5);

        // Net sales by customer in September: labels in both languages, the credit note netted, the invoices counted.
        var byCustomer = await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new object[] { new { field = "net_amount" }, new { field = "invoice_count" } }, from = "2026-09-01", to = "2026-09-30", sort = new[] { new { field = "net_amount", descending = true } } }, HttpStatusCode.OK);
        byCustomer.GetProperty("currency").GetString().ShouldBe("IQD");
        byCustomer.GetProperty("rowCount").GetInt32().ShouldBe(2);
        byCustomer.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        byCustomer.GetProperty("projectedUpTo").ValueKind.ShouldBe(JsonValueKind.String);
        var first = byCustomer.GetProperty("rows")[0];
        first.GetProperty("customer_key").GetString().ShouldBe(alpha.ToString());
        first.GetProperty("customer").GetProperty("ar").GetString().ShouldBe("ألفا");
        first.GetProperty("net_amount").GetDecimal().ShouldBe(2_500m, "3,000 + 500 − 1,000");
        first.GetProperty("invoice_count").GetDecimal().ShouldBe(2m);
        Row(byCustomer, "customer", beta.ToString()).GetProperty("net_amount").GetDecimal().ShouldBe(2_000m);
        byCustomer.GetProperty("fields").EnumerateArray().Select(static f => f.GetProperty("key").GetString()).ShouldBe(["customer", "net_amount", "invoice_count"]);

        // By month across two months, one customer only; the customer group and the item category come from their joins.
        var byMonth = await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "period_month", "customer_group", "item_category" }, values = new[] { new { field = "net_amount" } }, filters = new[] { new { field = "customer", op = "in", value = new[] { alpha } } }, from = "2026-08-01", to = "2026-09-30" }, HttpStatusCode.OK);
        var august = Row(byMonth, "period_month", "2026-08");
        august.GetProperty("net_amount").GetDecimal().ShouldBe(5_000m);
        august.GetProperty("customer_group").GetProperty("en").GetString().ShouldBe("Retail");
        august.GetProperty("item_category").GetProperty("en").GetString().ShouldBe("Drinks");
        var september = byMonth.GetProperty("rows").EnumerateArray().Where(static r => r.GetProperty("period_month_key").GetString() == "2026-09").ToList();
        september.Count.ShouldBe(2, "tea in Drinks and the service line without a category");
        september.Sum(static r => r.GetProperty("net_amount").GetDecimal()).ShouldBe(2_500m);
        september.Single(static r => r.GetProperty("item_category").ValueKind == JsonValueKind.Null).GetProperty("net_amount").GetDecimal().ShouldBe(500m);
        byMonth.GetProperty("rows").GetArrayLength().ShouldBe(3);

        // Prior-period comparison and a calculated field per row.
        var compared = await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new object[] { new { field = "net_amount" }, new { field = "invoice_count" } }, from = "2026-09-01", to = "2026-09-30", comparison = "prior_period", calculated = new[] { new { key = "per_invoice", label = Name("Per invoice", "لكل فاتورة"), expression = "net_amount / invoice_count" } } }, HttpStatusCode.OK);
        var alphaRow = Row(compared, "customer", alpha.ToString());
        alphaRow.GetProperty("net_amount_prior").GetDecimal().ShouldBe(5_000m, "August, the thirty days before September");
        alphaRow.GetProperty("net_amount_change").GetDecimal().ShouldBe(-2_500m);
        alphaRow.GetProperty("per_invoice").GetDecimal().ShouldBe(1_250m);
        Row(compared, "customer", beta.ToString()).GetProperty("net_amount_prior").ValueKind.ShouldBe(JsonValueKind.Null);
        compared.GetProperty("fields").EnumerateArray().Single(static f => f.GetProperty("key").GetString() == "net_amount_prior").GetProperty("group").GetString().ShouldBe("prior");

        // Top-N and a text filter on the label.
        var top = await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, from = "2026-09-01", to = "2026-09-30", topN = 1 }, HttpStatusCode.OK);
        top.GetProperty("rowCount").GetInt32().ShouldBe(1);
        top.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        top.GetProperty("rows")[0].GetProperty("customer_key").GetString().ShouldBe(alpha.ToString());
        var contains = await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, filters = new[] { new { field = "customer", op = "contains", value = "beta" } }, from = "2026-01-01", to = "2026-12-31" }, HttpStatusCode.OK);
        contains.GetProperty("rows").EnumerateArray().Single().GetProperty("customer_key").GetString().ShouldBe(beta.ToString());

        // The explanation: the statement, the parameters, the scope; a wrong field is refused with the model named.
        var explained = await owner.PostAsync("/api/v1/reporting/query/explain", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK);
        explained.GetProperty("sql").GetString()!.ShouldContain("GROUP BY");
        explained.GetProperty("sql").GetString()!.ShouldContain("f.company_id = @company");
        explained.GetProperty("sqlWithValues").GetString()!.ShouldContain(companyId.ToString());
        explained.GetProperty("joins").EnumerateArray().Select(static j => j.GetString()).ShouldContain("p");
        (await owner.PostErrorAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "colour" }, values = new[] { new { field = "net_amount" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("report.field_unknown");
        (await owner.PostErrorAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, filters = new[] { new { field = "net_amount", op = "gt", value = 1 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("report.filter_on_measure");

        // The drill: the lines behind Alpha's September cell, each naming its invoice.
        var drill = await owner.PostAsync("/api/v1/reporting/query/drill", new { definition = new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, from = "2026-09-01", to = "2026-09-30" }, keys = new Dictionary<string, string> { ["customer"] = alpha.ToString() } }, HttpStatusCode.OK);
        drill.GetProperty("rowCount").GetInt32().ShouldBe(3);
        drill.GetProperty("rows").EnumerateArray().Sum(static r => r.GetProperty("net_amount").GetDecimal()).ShouldBe(2_500m);
        drill.GetProperty("rows").EnumerateArray().All(static r => r.GetProperty("document_type").GetString() == "sales_invoice" && r.GetProperty("document_number").GetString()!.Length > 0).ShouldBeTrue();
        drill.GetProperty("columns").EnumerateArray().Select(static c => c.GetProperty("key").GetString()).ShouldContain("document_number");

        // The journal model reads the same books: debits equal credits, revenue by role nets the credit note.
        var journal = await owner.PostAsync("/api/v1/reporting/query", new { model = "journal_lines", companyId, rows = new[] { "account_role" }, values = new object[] { new { field = "debit_fc" }, new { field = "credit_fc" } }, from = "2026-08-01", to = "2026-09-30" }, HttpStatusCode.OK);
        var journalRows = journal.GetProperty("rows").EnumerateArray().ToList();
        journalRows.Sum(static r => r.GetProperty("debit_fc").GetDecimal()).ShouldBe(journalRows.Sum(static r => r.GetProperty("credit_fc").GetDecimal()));
        var revenue = journalRows.Single(static r => r.GetProperty("account_role_key").GetString() == "Revenue");
        (revenue.GetProperty("credit_fc").GetDecimal() - revenue.GetProperty("debit_fc").GetDecimal()).ShouldBe(9_500m, "5,000 + 3,500 + 2,000 − 1,000");

        // Open items as they stand: a live view, no projection in between.
        var open = await owner.PostAsync("/api/v1/reporting/query", new { model = "open_items", companyId, rows = new[] { "partner" }, values = new object[] { new { field = "remaining_fc" }, new { field = "item_count" } }, filters = new[] { new { field = "side", op = "eq", value = "AR" } }, from = "2026-01-01", to = "2026-12-31" }, HttpStatusCode.OK);
        open.GetProperty("projectedUpTo").ValueKind.ShouldBe(JsonValueKind.Null);
        Row(open, "partner", alpha.ToString()).GetProperty("remaining_fc").GetDecimal().ShouldBe(7_500m, "5,000 + 3,500 − 1,000 still open");
        Row(open, "partner", beta.ToString()).GetProperty("remaining_fc").GetDecimal().ShouldBe(2_000m);

        // A rebuild from the books equals what the increments built.
        var rebuilt = await owner.PostAsync("/api/v1/reporting/projections/rebuild", new { }, HttpStatusCode.OK);
        rebuilt.GetProperty("facts").EnumerateArray().Single(static s => s.GetProperty("fact").GetString() == "sales_lines").GetProperty("rowCount").GetInt64().ShouldBe(5);
        (await owner.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } }, from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK)).GetProperty("rows").EnumerateArray().Sum(static r => r.GetProperty("net_amount").GetDecimal()).ShouldBe(4_500m);

        // A member who may run reports but not read sales invoices sees no sales model and is refused the query.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "analyst", name = Name("Analyst", "محلل"), description = "", grants = new[] { "reporting.report.run", "inventory.stock.read" } });
        var email = $"analyst-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Analyst", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var analyst = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await analyst.GetOkAsync("/api/v1/reporting/models")).EnumerateArray().Select(static m => m.GetProperty("code").GetString()).ShouldBe(["stock_movements", "stock_on_hand"]);
        (await analyst.PostErrorAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("report.model_forbidden");
        (await analyst.PostAsync("/api/v1/reporting/query", new { model = "stock_on_hand", companyId, rows = new[] { "warehouse" }, values = new[] { new { field = "on_hand" } } }, HttpStatusCode.OK)).GetProperty("rowCount").GetInt32().ShouldBe(0);

        // Sales reading granted for another company only: the first company is out of scope, the second answers (with nothing yet).
        var other = (await owner.PostAsync("/api/v1/organization/companies", new { code = "RPT2", legalName = Name("Second Co", "الشركة الثانية"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        var salesReader = await owner.PostAsync("/api/v1/roles", new { code = "sales_reader", name = Name("Sales reader", "قارئ المبيعات"), description = "", grants = new[] { "sales.invoice.read" } });
        var membershipId = (await owner.GetOkAsync("/api/v1/users")).EnumerateArray().Single(m => m.GetProperty("email").GetString() == email).GetProperty("membershipId").GetGuid();
        await owner.PostAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = salesReader.GetProperty("id").GetGuid(), scopes = new[] { new { scopeType = "company", scopeId = other } } });
        (await analyst.PostErrorAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("report.company_not_in_scope");
        var scoped = await analyst.PostAsync("/api/v1/reporting/query", new { model = "sales_lines", companyId = other, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } } }, HttpStatusCode.OK);
        scoped.GetProperty("rowCount").GetInt32().ShouldBe(0);
        scoped.GetProperty("scope").GetProperty("companies").EnumerateArray().Single().GetGuid().ShouldBe(other);
        (await analyst.PostAsync("/api/v1/reporting/query/explain", new { model = "sales_lines", companyId = other, rows = new[] { "customer" }, values = new[] { new { field = "net_amount" } } }, HttpStatusCode.OK)).GetProperty("sql").GetString()!.ShouldContain("= ANY(@scopeCompanies)");
    }
}

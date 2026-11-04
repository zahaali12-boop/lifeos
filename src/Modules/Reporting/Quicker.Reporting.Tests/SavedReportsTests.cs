using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>
/// Saved reports (founder programme item 9, slice 9b): a report saved under a bilingual name with a chart, listed and
/// run by its owner; a broken definition refused at save time; a share to the tenant that lets an analyst see and run
/// it under their own permissions, with the analyst unable to change it or its audience; an edit share that lets them
/// change the query; the owner's delete.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SavedReportsTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Reports_are_saved_shared_run_as_the_reader_and_deleted_by_their_owner()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "SVD", legalName = Name("Saved Co", "شركة المحفوظات"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var alpha = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{alpha}/customer-accounts/{companyId}", new { currency = "IQD" });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea service", "خدمة شاي"), type = "service", baseUom = "PCS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();
        var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = alpha, kind = "invoice", documentDate = Iso(new DateOnly(2026, 9, 10)), lines = new[] { new { kind = "item", itemId = tea, quantity = 4m, unitPrice = 1_000m } } });
        await owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);

        // Nothing saved yet; a broken definition is refused with the field at fault before anything is stored.
        (await owner.GetOkAsync("/api/v1/reporting/reports")).GetArrayLength().ShouldBe(0);
        var definition = new { model = "sales_lines", companyId, rows = new[] { "customer" }, columns = new[] { "period_month" }, values = new object[] { new { field = "net_amount" }, new { field = "invoice_count" } }, from = "2026-01-01", to = "2026-12-31" };
        var broken = await owner.PostErrorAsync("/api/v1/reporting/reports", new { name = Name("Broken", "معطل"), definition = new { model = "sales_lines", companyId, rows = new[] { "no_such_field" }, values = new[] { new { field = "net_amount" } } } }, HttpStatusCode.UnprocessableEntity);
        broken.Code.ShouldBe("report.field_unknown");
        (await owner.PostErrorAsync("/api/v1/reporting/reports", new { name = Name("Nameless", ""), kind = "sketch", definition }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("report.kind_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/reports", new { name = new { }, definition }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("report.name_required");

        // Saved as a chart with its options; the summary carries the definition back and the owner's rights.
        var saved = await owner.PostAsync("/api/v1/reporting/reports", new { name = Name("Sales by customer", "المبيعات حسب العميل"), description = Name("Net sales per customer and month", "صافي المبيعات لكل عميل وشهر"), kind = "chart", chart = new { type = "bar", stacked = true, categoryField = "customer", seriesField = "period_month", measures = new[] { "net_amount" } }, definition });
        var reportId = saved.GetProperty("id").GetGuid();
        saved.GetProperty("model").GetString().ShouldBe("sales_lines");
        saved.GetProperty("companyId").GetGuid().ShouldBe(companyId);
        saved.GetProperty("kind").GetString().ShouldBe("chart");
        saved.GetProperty("chart").GetProperty("stacked").GetBoolean().ShouldBeTrue();
        saved.GetProperty("chart").GetProperty("measures")[0].GetString().ShouldBe("net_amount");
        saved.GetProperty("name").GetProperty("ar").GetString().ShouldBe("المبيعات حسب العميل");
        saved.GetProperty("definition").GetProperty("rows")[0].GetString().ShouldBe("customer");
        saved.GetProperty("isOwner").GetBoolean().ShouldBeTrue();
        saved.GetProperty("canEdit").GetBoolean().ShouldBeTrue();
        saved.GetProperty("isShared").GetBoolean().ShouldBeFalse();

        // Running the saved report projects the facts first and answers with the saved definition; a range override narrows it.
        var run = await owner.PostAsync($"/api/v1/reporting/reports/{reportId}/run", new { }, HttpStatusCode.OK);
        run.GetProperty("rowCount").GetInt32().ShouldBe(1);
        run.GetProperty("rows")[0].GetProperty("net_amount").GetDecimal().ShouldBe(4_000m);
        run.GetProperty("rows")[0].GetProperty("period_month_key").GetString().ShouldBe("2026-09");
        var narrowed = await owner.PostAsync($"/api/v1/reporting/reports/{reportId}/run", new { from = "2026-10-01", to = "2026-10-31" }, HttpStatusCode.OK);
        narrowed.GetProperty("rowCount").GetInt32().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/reporting/reports?companyId={companyId}")).GetArrayLength().ShouldBe(1);

        // An analyst with reporting and sales rights sees nothing until the owner shares the report with the tenant.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "analyst", name = Name("Analyst", "محلل"), description = "", grants = new[] { "reporting.report.run", "reporting.report.manage", "sales.invoice.read" } });
        var email = $"analyst-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Analyst", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var analyst = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await analyst.GetOkAsync("/api/v1/reporting/reports")).GetArrayLength().ShouldBe(0);
        (await analyst.GetStatusAsync($"/api/v1/reporting/reports/{reportId}")).ShouldBe(HttpStatusCode.NotFound);
        (await analyst.PostErrorAsync($"/api/v1/reporting/reports/{reportId}/run", new { }, HttpStatusCode.NotFound)).Code.ShouldBe("saved_report.not_found");

        var shared = await owner.PutAsync($"/api/v1/reporting/reports/{reportId}/shares", new[] { new { shareWith = "tenant", access = "view" } });
        shared.GetProperty("isShared").GetBoolean().ShouldBeTrue();
        shared.GetProperty("shares").GetArrayLength().ShouldBe(1);
        (await owner.PutErrorAsync($"/api/v1/reporting/reports/{reportId}/shares", new[] { new { shareWith = "role", access = "view" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("report.share_target_required");

        // Shared to view: the analyst lists and runs it as themselves, but cannot change it, re-share it or delete it.
        var visible = await analyst.GetOkAsync("/api/v1/reporting/reports");
        visible.GetArrayLength().ShouldBe(1);
        visible[0].GetProperty("isOwner").GetBoolean().ShouldBeFalse();
        visible[0].GetProperty("canEdit").GetBoolean().ShouldBeFalse();
        (await analyst.PostAsync($"/api/v1/reporting/reports/{reportId}/run", new { }, HttpStatusCode.OK)).GetProperty("rows")[0].GetProperty("net_amount").GetDecimal().ShouldBe(4_000m);
        (await analyst.PutErrorAsync($"/api/v1/reporting/reports/{reportId}", new { name = Name("Hijacked", "مختطف"), definition }, HttpStatusCode.Forbidden)).Code.ShouldBe("report.not_editable");
        (await analyst.PutErrorAsync($"/api/v1/reporting/reports/{reportId}/shares", new[] { new { shareWith = "tenant", access = "edit" } }, HttpStatusCode.Forbidden)).Code.ShouldBe("report.not_owner");
        await analyst.DeleteAsync($"/api/v1/reporting/reports/{reportId}", HttpStatusCode.Forbidden);

        // The analyst saves a report of their own; the owner does not see it until it is shared with them by membership.
        var mine = await analyst.PostAsync("/api/v1/reporting/reports", new { name = Name("My invoices", "فواتيري"), kind = "table", definition = new { model = "sales_lines", companyId, rows = new[] { "invoice_number" }, values = new[] { new { field = "net_amount" } } } });
        (await owner.GetOkAsync("/api/v1/reporting/reports")).GetArrayLength().ShouldBe(1);
        await analyst.PutAsync($"/api/v1/reporting/reports/{mine.GetProperty("id").GetGuid()}/shares", new[] { new { shareWith = "membership", targetId = ws.MembershipId, access = "view" } });
        (await owner.GetOkAsync("/api/v1/reporting/reports")).GetArrayLength().ShouldBe(2);

        // Shared to edit by role: the analyst changes the query (a table of the same model) but still cannot change the audience.
        await owner.PutAsync($"/api/v1/reporting/reports/{reportId}/shares", new[] { new { shareWith = "role", targetId = role.GetProperty("id").GetGuid(), access = "edit" } });
        var edited = await analyst.PutAsync($"/api/v1/reporting/reports/{reportId}", new { name = Name("Sales by item", "المبيعات حسب الصنف"), kind = "table", definition = new { model = "sales_lines", companyId, rows = new[] { "item" }, values = new[] { new { field = "quantity" } }, from = "2026-01-01", to = "2026-12-31" }, shares = new[] { new { shareWith = "tenant", access = "edit" } } });
        edited.GetProperty("kind").GetString().ShouldBe("table");
        edited.GetProperty("canEdit").GetBoolean().ShouldBeTrue();
        edited.GetProperty("shares").GetArrayLength().ShouldBe(1);
        edited.GetProperty("shares")[0].GetProperty("shareWith").GetString().ShouldBe("role");
        (await owner.PostAsync($"/api/v1/reporting/reports/{reportId}/run", new { }, HttpStatusCode.OK)).GetProperty("rows")[0].GetProperty("quantity").GetDecimal().ShouldBe(4m);

        // The audit trail keeps the report's changes; the owner deletes it and it is gone for everyone.
        var history = await owner.GetOkAsync($"/api/v1/audit/records/saved_report/{reportId}");
        history.EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("created");
        history.EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("updated");
        await owner.DeleteAsync($"/api/v1/reporting/reports/{reportId}", HttpStatusCode.NoContent);
        (await owner.GetStatusAsync($"/api/v1/reporting/reports/{reportId}")).ShouldBe(HttpStatusCode.NotFound);
        (await analyst.GetOkAsync("/api/v1/reporting/reports")).GetArrayLength().ShouldBe(1);
    }
}

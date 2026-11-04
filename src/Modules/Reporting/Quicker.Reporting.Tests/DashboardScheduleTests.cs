using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>
/// Dashboards and schedules (founder programme item 9, slice 9d; ADR-0034 §4, A-185): a dashboard of KPI, chart,
/// table and statement widgets over a saved report and the shipped profit and loss, run for its period and for
/// others, private until shared, run by an analyst under their own rights; a schedule of the report as XLSX to a
/// member and an address run now through the worker, filed, emailed and kept in the run history; a statement
/// schedule as PDF; an empty period skipped; the platform entry that fires a schedule following its enabled state and
/// its deletion. The clock of the test host stands at 2026-09-22.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DashboardScheduleTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement Widget(JsonElement run, string code) => run.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("code").GetString() == code);

    [Fact]
    public async Task A_dashboard_is_built_run_and_shared_and_a_schedule_runs_through_the_worker_files_emails_and_keeps_its_history()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "DSH", legalName = Name("Dashboards Co", "شركة اللوحات"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var alpha = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{alpha}/customer-accounts/{companyId}", new { currency = "IQD" });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea service", "خدمة شاي"), type = "service", baseUom = "PCS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();

        async Task PostInvoiceAsync(DateOnly on, decimal quantity)
        {
            var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = alpha, kind = "invoice", documentDate = Iso(on), lines = new[] { new { kind = "item", itemId = tea, quantity, unitPrice = 1_000m } } });
            await owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        }

        async Task PostJournalAsync(string date, string description, object[] lines)
        {
            var draft = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { kind = "manual", postingDate = date, currency = "IQD", description = Name(description, description), lines });
            (await owner.PostAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
        }

        // The books: capital in August, 5,000 of sales in August and 5,000 in September, September's rent accrued.
        await PostJournalAsync("2026-08-01", "Capital", [new { accountCode = "1250", debit = 10_000m, credit = 0m }, new { accountCode = "3100", debit = 0m, credit = 10_000m }]);
        await PostInvoiceAsync(new DateOnly(2026, 8, 15), 5m);
        await PostInvoiceAsync(new DateOnly(2026, 9, 10), 2m);
        await PostInvoiceAsync(new DateOnly(2026, 9, 22), 3m);
        await PostJournalAsync("2026-09-05", "Rent", [new { accountCode = "6110", debit = 1_200m, credit = 0m }, new { accountCode = "2170", debit = 0m, credit = 1_200m }]);

        // A saved report of net sales by customer, comparing with the period before.
        var report = await owner.PostAsync("/api/v1/reporting/reports", new
        {
            name = Name("Sales by customer", "المبيعات حسب العميل"),
            kind = "chart",
            chart = new { type = "bar", categoryField = "customer", measures = new[] { "net_amount" } },
            definition = new { model = "sales_lines", companyId, rows = new[] { "customer" }, values = new object[] { new { field = "net_amount" }, new { field = "invoice_count" } }, comparison = "prior_period" },
        });
        var reportId = report.GetProperty("id").GetGuid();

        // The designer's refusals: a name, a statement widget's layout and row, a chart over a statement.
        var widgets = new object[]
        {
            new { code = "SALES", source = "report", viz = "kpi", reportId, width = 1, options = new { measure = "net_amount" } },
            new { code = "CHART", source = "report", viz = "bar", reportId, width = 2, options = new { chart = new { type = "bar", categoryField = "customer", measures = new[] { "net_amount" } } } },
            new { code = "TABLE", source = "report", viz = "table", reportId, width = 1, period = "year_to_date" },
            new { code = "PL", source = "statement", viz = "table", layoutCode = "PL", width = 2 },
            new { code = "NET", source = "statement", viz = "kpi", layoutCode = "PL", width = 1, options = new { rowCode = "NET", columnCode = "PERIOD" }, title = Name("Net profit", "صافي الربح") },
        };
        (await owner.PostErrorAsync("/api/v1/reporting/dashboards", new { name = new { }, widgets }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dashboard.name_required");
        (await owner.PostErrorAsync("/api/v1/reporting/dashboards", new { name = Name("X", "س"), widgets = new[] { new { code = "S", source = "statement", viz = "table", layoutCode = "NOPE" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dashboard.widget_layout_required");
        (await owner.PostErrorAsync("/api/v1/reporting/dashboards", new { name = Name("X", "س"), widgets = new[] { new { code = "S", source = "statement", viz = "kpi", layoutCode = "PL" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dashboard.widget_row_required");
        (await owner.PostErrorAsync("/api/v1/reporting/dashboards", new { name = Name("X", "س"), widgets = new[] { new { code = "S", source = "statement", viz = "line", layoutCode = "PL" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dashboard.widget_viz_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/dashboards", new { name = Name("X", "س"), widgets = new[] { new { code = "S", source = "report", viz = "kpi", reportId = Guid.NewGuid() } } }, HttpStatusCode.NotFound)).Code.ShouldBe("saved_report.not_found");

        // Saved with five widgets, private, pinned to the home page.
        var dashboard = await owner.PostAsync("/api/v1/reporting/dashboards", new { name = Name("Sales at a glance", "المبيعات في لمحة"), widgets, defaultPeriod = "this_month", showOnHome = true });
        var dashboardId = dashboard.GetProperty("id").GetGuid();
        dashboard.GetProperty("widgets").GetArrayLength().ShouldBe(5);
        dashboard.GetProperty("widgets")[0].GetProperty("title").GetProperty("en").GetString().ShouldBe("Sales by customer");
        dashboard.GetProperty("widgets")[4].GetProperty("title").GetProperty("ar").GetString().ShouldBe("صافي الربح");
        (await owner.GetOkAsync($"/api/v1/reporting/dashboards?home=true")).GetArrayLength().ShouldBe(1);

        // Run for this month (September): sales 5,000 with 5,000 the month before; the table widget follows its own period, the year to date, so 10,000.
        var run = await owner.PostAsync($"/api/v1/reporting/dashboards/{dashboardId}/run", new { companyId }, HttpStatusCode.OK);
        run.GetProperty("period").GetString().ShouldBe("this_month");
        (run.GetProperty("from").GetString(), run.GetProperty("to").GetString()).ShouldBe(("2026-09-01", "2026-09-30"));
        var sales = Widget(run, "SALES").GetProperty("kpi");
        sales.GetProperty("value").GetDecimal().ShouldBe(5_000m);
        sales.GetProperty("prior").GetDecimal().ShouldBe(5_000m);
        sales.GetProperty("type").GetString().ShouldBe("money");
        Widget(run, "CHART").GetProperty("query").GetProperty("rows").GetArrayLength().ShouldBe(1);
        Widget(run, "CHART").GetProperty("chart").GetProperty("categoryField").GetString().ShouldBe("customer");
        var table = Widget(run, "TABLE");
        (table.GetProperty("from").GetString(), table.GetProperty("to").GetString()).ShouldBe(("2026-01-01", "2026-09-22"));
        table.GetProperty("query").GetProperty("rows")[0].GetProperty("net_amount").GetDecimal().ShouldBe(10_000m);
        var pl = Widget(run, "PL").GetProperty("statement");
        pl.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "NET").GetProperty("values")[0].GetDecimal().ShouldBe(3_800m);
        Widget(run, "NET").GetProperty("kpi").GetProperty("value").GetDecimal().ShouldBe(3_800m);
        Widget(run, "NET").GetProperty("kpi").GetProperty("label").GetProperty("en").GetString().ShouldBe("Net profit");

        // Another period: August by fixed dates; a custom period without dates is refused.
        var august = await owner.PostAsync($"/api/v1/reporting/dashboards/{dashboardId}/run", new { companyId, period = "custom", from = "2026-08-01", to = "2026-08-31" }, HttpStatusCode.OK);
        Widget(august, "SALES").GetProperty("kpi").GetProperty("value").GetDecimal().ShouldBe(5_000m);
        Widget(august, "SALES").GetProperty("kpi").GetProperty("prior").ValueKind.ShouldBe(JsonValueKind.Null);
        Widget(august, "NET").GetProperty("kpi").GetProperty("value").GetDecimal().ShouldBe(5_000m);
        (await owner.PostErrorAsync($"/api/v1/reporting/dashboards/{dashboardId}/run", new { companyId, period = "custom" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dashboard.range_required");

        // An analyst sees nothing until the dashboard is shared with the workspace; shared, they run it under their own rights and cannot change it.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "analyst", name = Name("Analyst", "محلل"), description = "", grants = new[] { "reporting.report.run", "reporting.report.manage", "sales.invoice.read", "accounting.journal.read" } });
        var email = $"analyst-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Analyst", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var analyst = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await analyst.GetOkAsync("/api/v1/reporting/dashboards")).GetArrayLength().ShouldBe(0);
        (await analyst.GetStatusAsync($"/api/v1/reporting/dashboards/{dashboardId}")).ShouldBe(HttpStatusCode.NotFound);
        await owner.PutAsync($"/api/v1/reporting/reports/{reportId}/shares", new[] { new { shareWith = "tenant", access = "view" } });
        var sharedBody = new { name = Name("Sales at a glance", "المبيعات في لمحة"), widgets, defaultPeriod = "this_month", showOnHome = true, sharing = "tenant" };
        (await owner.PutAsync($"/api/v1/reporting/dashboards/{dashboardId}", sharedBody)).GetProperty("sharing").GetString().ShouldBe("tenant");
        var seen = await analyst.GetOkAsync("/api/v1/reporting/dashboards");
        seen.GetArrayLength().ShouldBe(1);
        seen[0].GetProperty("canEdit").GetBoolean().ShouldBeFalse();
        var analystRun = await analyst.PostAsync($"/api/v1/reporting/dashboards/{dashboardId}/run", new { companyId }, HttpStatusCode.OK);
        Widget(analystRun, "SALES").GetProperty("kpi").GetProperty("value").GetDecimal().ShouldBe(5_000m);
        Widget(analystRun, "NET").GetProperty("kpi").GetProperty("value").GetDecimal().ShouldBe(3_800m);
        (await analyst.PutErrorAsync($"/api/v1/reporting/dashboards/{dashboardId}", sharedBody, HttpStatusCode.Forbidden)).Code.ShouldBe("dashboard.not_owner");
        (await owner.GetOkAsync($"/api/v1/audit/records/dashboard/{dashboardId}")).EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("updated");

        // A schedule of the report as XLSX to the owner and an outside address, monthly; the designer's refusals first.
        var recipients = new object[] { new { membershipId = ws.MembershipId }, new { email = "cfo@example.test" } };
        (await owner.PostErrorAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, cron = "0 7 1 * *", format = "xlsx", recipients = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.recipients_required");
        (await owner.PostErrorAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, cron = "0 7 1 * *", format = "docx", recipients }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.format_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, cron = "every monday", format = "xlsx", recipients }, HttpStatusCode.UnprocessableEntity)).Code.ShouldStartWith("schedule.");
        (await owner.PostErrorAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, cron = "0 7 1 * *", format = "xlsx", recipients, timeZone = "Mars/Olympus" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.time_zone_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, cron = "0 7 1 * *", format = "xlsx", recipients = new[] { new { email = "not-an-address" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.recipient_invalid");

        var schedule = await owner.PostAsync("/api/v1/reporting/schedules", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, period = "this_month", cron = "0 7 1 * *", format = "xlsx", recipients, onlyIfRows = true });
        var scheduleId = schedule.GetProperty("id").GetGuid();
        schedule.GetProperty("timeZone").GetString().ShouldBe("Asia/Baghdad");
        schedule.GetProperty("nextRunAt").ValueKind.ShouldBe(JsonValueKind.String);
        schedule.GetProperty("sourceName").GetProperty("en").GetString().ShouldBe("Sales by customer");
        schedule.GetProperty("recipients").GetArrayLength().ShouldBe(2);
        var platform = await owner.GetOkAsync("/api/v1/platform/schedules");
        var entry = platform.EnumerateArray().Single(s => s.GetProperty("code").GetString() == $"reporting.run.{scheduleId:N}");
        (entry.GetProperty("jobType").GetString(), entry.GetProperty("cron").GetString(), entry.GetProperty("enabled").GetBoolean()).ShouldBe(("reporting.run", "0 7 1 * *", true));

        // Run now: the worker runs it as the owner, files the XLSX, emails both recipients and records the run.
        var requested = await owner.PostAsync($"/api/v1/reporting/schedules/{scheduleId}/run", new { }, HttpStatusCode.OK);
        requested.GetProperty("jobId").ValueKind.ShouldBe(JsonValueKind.String);
        await host.RunWorkerAsync();
        var runs = await owner.GetOkAsync($"/api/v1/reporting/schedules/{scheduleId}/runs");
        runs.GetArrayLength().ShouldBe(1);
        var first = runs[0];
        (first.GetProperty("status").GetString(), first.GetProperty("trigger").GetString()).ShouldBe(("sent", "manual"));
        (first.GetProperty("periodFrom").GetString(), first.GetProperty("periodTo").GetString()).ShouldBe(("2026-09-01", "2026-09-30"));
        first.GetProperty("rowCount").GetInt32().ShouldBe(1);
        first.GetProperty("fileName").GetString().ShouldEndWith(".xlsx");
        first.GetProperty("recipients").EnumerateArray().Select(static r => r.GetString()).ShouldBe([ws.OwnerEmail, "cfo@example.test"]);
        var mail = Api.Emails.LastTo("cfo@example.test").ShouldNotBeNull();
        mail.Subject.ShouldNotBeNull().ShouldContain("Monthly sales");
        var attachment = mail.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        attachment.FileName.ShouldEndWith(".xlsx");
        attachment.ContentType.ShouldContain("spreadsheet");
        using var file = await owner.GetAsync($"/api/v1/reporting/schedules/runs/{first.GetProperty("id").GetGuid()}/file");
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldNotBeNull().ShouldContain("spreadsheet");
        (await file.Content.ReadAsByteArrayAsync()).Length.ShouldBe((int)first.GetProperty("sizeBytes").GetInt64());
        (await owner.GetOkAsync($"/api/v1/reporting/schedules/{scheduleId}")).GetProperty("lastStatus").GetString().ShouldBe("sent");

        // A statement schedule as PDF for last month: the print pipeline renders it and the PDF goes out.
        var statementSchedule = await owner.PostAsync("/api/v1/reporting/schedules", new { name = Name("August P&L", "أرباح وخسائر آب"), companyId, source = "statement", layoutCode = "PL", period = "last_month", cron = "0 6 2 * *", format = "pdf", recipients = new[] { new { email = "board@example.test" } } });
        await owner.PostAsync($"/api/v1/reporting/schedules/{statementSchedule.GetProperty("id").GetGuid()}/run", new { }, HttpStatusCode.OK);
        await host.RunWorkerAsync();
        var statementRun = (await owner.GetOkAsync($"/api/v1/reporting/schedules/{statementSchedule.GetProperty("id").GetGuid()}/runs"))[0];
        statementRun.GetProperty("status").GetString().ShouldBe("sent", statementRun.ToString());
        (statementRun.GetProperty("periodFrom").GetString(), statementRun.GetProperty("periodTo").GetString()).ShouldBe(("2026-08-01", "2026-08-31"));
        var board = Api.Emails.LastTo("board@example.test").ShouldNotBeNull();
        var pdf = board.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        pdf.ContentType.ShouldBe("application/pdf");
        pdf.Content.ToArray().Take(5).ToArray().ShouldBe("%PDF-"u8.ToArray());

        // An empty period with "only if rows" is skipped: nothing filed, nothing sent.
        var sentBefore = Api.Emails.Sent.Count;
        var quiet = await owner.PostAsync("/api/v1/reporting/schedules", new { name = Name("Yesterday's sales", "مبيعات الأمس"), companyId, source = "report", reportId, period = "yesterday", cron = "0 7 * * *", format = "csv", recipients, onlyIfRows = true });
        await owner.PostAsync($"/api/v1/reporting/schedules/{quiet.GetProperty("id").GetGuid()}/run", new { }, HttpStatusCode.OK);
        await host.RunWorkerAsync();
        var skipped = (await owner.GetOkAsync($"/api/v1/reporting/schedules/{quiet.GetProperty("id").GetGuid()}/runs"))[0];
        (skipped.GetProperty("status").GetString(), skipped.GetProperty("rowCount").GetInt32(), skipped.GetProperty("hasFile").GetBoolean()).ShouldBe(("skipped", 0, false));
        Api.Emails.Sent.Count.ShouldBe(sentBefore);

        // Disabled, the platform entry stops firing; deleted, the entry and the runs are gone.
        await owner.PutAsync($"/api/v1/reporting/schedules/{scheduleId}", new { name = Name("Monthly sales", "المبيعات الشهرية"), companyId, source = "report", reportId, period = "this_month", cron = "0 7 1 * *", format = "xlsx", recipients, onlyIfRows = true, enabled = false });
        (await owner.GetOkAsync("/api/v1/platform/schedules")).EnumerateArray().Single(s => s.GetProperty("code").GetString() == $"reporting.run.{scheduleId:N}").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await owner.GetOkAsync($"/api/v1/audit/records/report_schedule/{scheduleId}")).EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("updated");
        await owner.DeleteAsync($"/api/v1/reporting/schedules/{scheduleId}", HttpStatusCode.NoContent);
        (await owner.GetOkAsync("/api/v1/platform/schedules")).EnumerateArray().Any(s => s.GetProperty("code").GetString() == $"reporting.run.{scheduleId:N}").ShouldBeFalse();
        (await owner.GetStatusAsync($"/api/v1/reporting/schedules/{scheduleId}/runs")).ShouldBe(HttpStatusCode.NotFound);

        // Deleted, the dashboard is gone for everyone.
        await owner.DeleteAsync($"/api/v1/reporting/dashboards/{dashboardId}", HttpStatusCode.NoContent);
        (await analyst.GetOkAsync("/api/v1/reporting/dashboards")).GetArrayLength().ShouldBe(0);
        await owner.AssertInvariantsAsync();
    }
}

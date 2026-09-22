using System.Net;
using System.Text;
using System.Text.Json;
using Quicker.Accounting.Application;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Amounts;

namespace Quicker.Accounting.Tests;

/// <summary>
/// Roadmap 2.3 through the API: manual journal lifecycle with maker/checker, opening balances, accruals reversed
/// by the daily routine on their date, recurring templates, prepayment amortisation to the cent, CSV import.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class JournalTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static async Task<Guid> CompanyReadyAsync(HttpClient owner, string code, string functional)
    {
        var company = await owner.PostAsync("/api/v1/organization/companies", new
        {
            code,
            legalName = new { en = code + " Trading", ar = "شركة " + code },
            country = "IQ",
            functionalCurrency = functional,
            timeZone = "Asia/Baghdad",
        });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.ChartFromTemplateAsync("IFRS_SME", "CH-" + code, companyId);
        await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/posting-profiles/from-chart", new { });
        return companyId;
    }

    private static object Line(string account, decimal debit = 0m, decimal credit = 0m, string? subledgerType = null, Guid? subledgerRef = null) =>
        new { accountCode = account, debit, credit, subledgerType, subledgerRef };

    [Fact]
    public async Task A_journal_is_drafted_submitted_approved_by_someone_else_and_posted_once()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await CompanyReadyAsync(owner, "MJL", "IQD");
        await owner.PutAsync($"/api/v1/organization/companies/{companyId}/settings/{ManualJournalService.ApprovalSetting}", new { value = "required", valueType = "string" });

        var draft = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new
        {
            postingDate = "2026-09-22",
            currency = "IQD",
            description = new { en = "Office rent September", ar = "إيجار المكتب أيلول" },
            lines = new[] { Line("6110", debit: 1500000m), Line("2170", credit: 1500000m) },
        });
        var journalId = draft.GetProperty("id").GetGuid();
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("number").GetString().ShouldStartWith("DRAFT-");
        draft.GetProperty("totalDebit").GetDecimal().ShouldBe(1500000m);
        draft.GetProperty("lines").EnumerateArray().First().GetProperty("accountName").GetProperty("ar").GetString().ShouldBe("الإيجار");

        // Rules on the way: both sides, unknown account, a control account without its subledger item at posting time.
        (await owner.PostErrorAsync($"/api/v1/accounting/companies/{companyId}/journals", new { postingDate = "2026-09-22", currency = "IQD", lines = new[] { new { accountCode = "6110", debit = 5m, credit = 5m }, Line("2170", credit: 5m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.line_sides");
        (await owner.PostErrorAsync($"/api/v1/accounting/companies/{companyId}/journals", new { postingDate = "2026-09-22", currency = "IQD", lines = new[] { Line("9999", debit: 5m), Line("2170", credit: 5m) } }, HttpStatusCode.NotFound)).Code.ShouldBe("account.not_found");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{journalId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.not_postable");

        // Approval is required and the submitter cannot approve.
        var submitted = await owner.PostAsync($"/api/v1/accounting/journals/{journalId}/submit", new { }, HttpStatusCode.OK);
        submitted.GetProperty("status").GetString().ShouldBe("pending_approval");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{journalId}/approve", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.approve_own");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{journalId}/reject", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.reason_required");
        var rejected = await owner.PostAsync($"/api/v1/accounting/journals/{journalId}/reject", new { reason = "Wrong month" }, HttpStatusCode.OK);
        rejected.GetProperty("status").GetString().ShouldBe("rejected");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Wrong month");

        // Without the approval requirement a balanced draft posts directly; the entry carries the journal's number and the journal keeps the entry.
        await owner.PutAsync($"/api/v1/organization/companies/{companyId}/settings/{ManualJournalService.ApprovalSetting}", new { value = "none", valueType = "string" });
        await owner.PutAsync($"/api/v1/accounting/journals/{journalId}", new { postingDate = "2026-09-22", currency = "IQD", description = new { en = "Office rent September" }, lines = new[] { Line("6110", debit: 1500000m), Line("2170", credit: 1500000m) } });
        var posted = await owner.PostAsync($"/api/v1/accounting/journals/{journalId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("number").GetString().ShouldBe("MJ-2026-00001");
        var entryId = posted.GetProperty("journalEntryId").GetGuid();
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{entryId}");
        entry.GetProperty("sourceDocumentType").GetString().ShouldBe("manual_journal");
        entry.GetProperty("sourceDocumentNumber").GetString().ShouldBe("MJ-2026-00001");
        entry.GetProperty("isManual").GetBoolean().ShouldBeTrue();
        entry.GetProperty("lines").EnumerateArray().First().GetProperty("accountCode").GetString().ShouldBe("6110");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{journalId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.not_postable");
        (await owner.PutErrorAsync($"/api/v1/accounting/journals/{journalId}", new { postingDate = "2026-09-22", currency = "IQD", lines = new[] { Line("6110", debit: 1m), Line("2170", credit: 1m) } }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.not_editable");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{journalId}/cancel", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.not_cancellable");

        // A control account takes a manual line only with its subledger item; the list shows the journal by status.
        var customer = Guid.NewGuid();
        var blocked = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { postingDate = "2026-09-22", currency = "IQD", lines = new[] { Line("1210", debit: 10m), Line("4100", credit: 10m) } });
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{blocked.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.manual_posting_blocked");
        var adjustment = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { postingDate = "2026-09-22", currency = "IQD", lines = new[] { Line("1210", debit: 10m, subledgerType: "AR", subledgerRef: customer), Line("4100", credit: 10m) } });
        (await owner.PostAsync($"/api/v1/accounting/journals/{adjustment.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/journals?status=posted")).GetProperty("items").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Opening_balances_balance_themselves_and_accruals_reverse_on_their_date_through_the_routine()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await CompanyReadyAsync(owner, "OPN", "IQD");

        var opening = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new
        {
            kind = "opening",
            postingDate = "2026-09-01",
            currency = "IQD",
            description = new { en = "Go-live balances", ar = "أرصدة الانطلاق" },
            lines = new[] { Line("1111", debit: 5000000m, subledgerType: "BANK", subledgerRef: Guid.NewGuid()), Line("1510", debit: 20000000m, subledgerType: "FA", subledgerRef: Guid.NewGuid()), Line("2510", credit: 10000000m) },
        });
        var openingPosted = await owner.PostAsync($"/api/v1/accounting/journals/{opening.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var openingEntry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{openingPosted.GetProperty("journalEntryId").GetGuid()}");
        openingEntry.GetProperty("isOpeningEntry").GetBoolean().ShouldBeTrue();
        var equity = openingEntry.GetProperty("lines").EnumerateArray().Single(static l => l.GetProperty("accountCode").GetString() == "3400");
        equity.GetProperty("creditTc").GetDecimal().ShouldBe(15000000m, "the engine balanced the opening to opening balance equity");

        // An accrual reverses automatically on its date, not before.
        var accrual = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new
        {
            kind = "accrual",
            postingDate = "2026-09-30",
            autoReverseOn = "2026-10-01",
            currency = "IQD",
            description = new { en = "Accrued electricity", ar = "كهرباء مستحقة" },
            lines = new[] { Line("6120", debit: 300000m), Line("2170", credit: 300000m) },
        });
        (await owner.PostErrorAsync($"/api/v1/accounting/companies/{companyId}/journals", new { kind = "accrual", postingDate = "2026-09-30", currency = "IQD", lines = new[] { Line("6120", debit: 1m), Line("2170", credit: 1m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.auto_reverse_date");
        var accrualPosted = await owner.PostAsync($"/api/v1/accounting/journals/{accrual.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var accrualEntryId = accrualPosted.GetProperty("journalEntryId").GetGuid();

        var early = await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={companyId}&asOf=2026-09-30", new { }, HttpStatusCode.OK);
        early.GetProperty("autoReversals").GetArrayLength().ShouldBe(0);
        var onDate = await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={companyId}&asOf=2026-10-01", new { }, HttpStatusCode.OK);
        var reversal = onDate.GetProperty("autoReversals").EnumerateArray().Single();
        reversal.GetProperty("targetId").GetGuid().ShouldBe(accrualEntryId);
        reversal.GetProperty("outcome").GetString().ShouldBe("posted");
        var reversalEntry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{reversal.GetProperty("producedId").GetGuid()}");
        reversalEntry.GetProperty("postingDate").GetString().ShouldBe("2026-10-01");
        reversalEntry.GetProperty("isAutoReversal").GetBoolean().ShouldBeTrue();
        reversalEntry.GetProperty("reversesEntryId").GetGuid().ShouldBe(accrualEntryId);
        reversalEntry.GetProperty("links").EnumerateArray().Select(static l => l.GetProperty("relation").GetString()).ShouldBe(["auto_reversal_of", "reverses"], ignoreOrder: true);
        (await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={companyId}&asOf=2026-10-02", new { }, HttpStatusCode.OK)).GetProperty("autoReversals").GetArrayLength().ShouldBe(0, "reversed once");
    }

    [Fact]
    public async Task Recurring_templates_generate_on_schedule_and_prepayments_amortise_to_the_cent()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await CompanyReadyAsync(owner, "REC", "USD");

        var template = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/recurring-templates", new
        {
            code = "RENT",
            name = new { en = "Monthly rent", ar = "الإيجار الشهري" },
            cron = "0 0 1 * *",
            currency = "USD",
            startsOn = "2026-10-01",
            requiresReview = false,
            lines = new object[] { new { accountCode = "6110", debit = 2500m }, new { accountCode = "2170", credit = 2500m } },
        });
        var templateId = template.GetProperty("id").GetGuid();
        template.GetProperty("nextRunOn").GetString().ShouldBe("2026-10-01");
        (await owner.PostErrorAsync($"/api/v1/accounting/companies/{companyId}/recurring-templates", new { code = "BAD", name = new { en = "x" }, cron = "every day", currency = "USD", lines = new object[] { new { accountCode = "6110", debit = 1m }, new { accountCode = "2170", credit = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.cron_invalid");
        (await owner.PostErrorAsync($"/api/v1/accounting/companies/{companyId}/recurring-templates", new { code = "BAD", name = new { en = "x" }, cron = "0 0 1 * *", currency = "USD", lines = new object[] { new { accountCode = "6110", debit = 1m }, new { accountCode = "2170", credit = 2m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("recurring_template.unbalanced");

        var nothing = await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={companyId}&asOf=2026-09-30", new { }, HttpStatusCode.OK);
        nothing.GetProperty("recurringJournals").GetArrayLength().ShouldBe(0);
        var run = await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={companyId}&asOf=2026-11-01", new { }, HttpStatusCode.OK);
        var generated = run.GetProperty("recurringJournals").EnumerateArray().ToList();
        generated.Count.ShouldBe(2, "October and November were due");
        generated.ShouldAllBe(static g => g.GetProperty("outcome").GetString() == "generated");
        var october = await owner.GetOkAsync($"/api/v1/accounting/journals/{generated[0].GetProperty("producedId").GetGuid()}");
        october.GetProperty("kind").GetString().ShouldBe("recurring");
        october.GetProperty("status").GetString().ShouldBe("posted", "no review required: posted at once");
        october.GetProperty("postingDate").GetString().ShouldBe("2026-10-01");
        october.GetProperty("templateId").GetGuid().ShouldBe(templateId);
        (await owner.GetOkAsync($"/api/v1/accounting/recurring-templates/{templateId}")).GetProperty("nextRunOn").GetString().ShouldBe("2026-12-01");

        // A percentage template distributes a base amount and lands the rounding on the last credit line; it waits for review.
        var split = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/recurring-templates", new
        {
            code = "SPLIT",
            name = new { en = "Shared services", ar = "خدمات مشتركة" },
            cron = "0 0 1 * *",
            currency = "USD",
            amountMode = "percentage",
            baseAmount = 100.01m,
            startsOn = "2026-10-01",
            lines = new object[] { new { accountCode = "6160", debit = 100m }, new { accountCode = "2170", credit = 33.33m }, new { accountCode = "2190", credit = 33.33m }, new { accountCode = "2250", credit = 33.34m } },
        });
        var draft = await owner.PostAsync($"/api/v1/accounting/recurring-templates/{split.GetProperty("id").GetGuid()}/generate", new { });
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("totalDebit").GetDecimal().ShouldBe(100.01m);
        draft.GetProperty("totalCredit").GetDecimal().ShouldBe(100.01m);

        // 1000.01 over 12 periods: eleven parts of 83.33 and a last part of 83.38, planned on period ends.
        var preview = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/deferrals/preview", new { kind = "prepayment", balanceAccountCode = "1410", targetAccountCode = "6170", startsOn = "2026-09-01", periods = 12, totalAmount = 1000.01m, currency = "USD" }, HttpStatusCode.OK);
        var planned = preview.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("amount").GetDecimal()).ToList();
        planned.Count.ShouldBe(12);
        planned.Take(11).ShouldAllBe(static a => a == 83.33m);
        planned[11].ShouldBe(83.38m);
        planned.Sum().ShouldBe(1000.01m);
        preview.GetProperty("lines").EnumerateArray().First().GetProperty("postingDate").GetString().ShouldBe("2026-09-30");
        DeferralService.Amortise(1000.01m, [(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), (new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28))], "daily", 2, RoundingPolicy.Default).ShouldBe([525.43m, 474.58m]);

        var schedule = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/deferrals", new { kind = "prepayment", balanceAccountCode = "1410", targetAccountCode = "6170", startsOn = "2026-09-01", periods = 12, totalAmount = 1000.01m, currency = "USD", description = new { en = "Annual insurance", ar = "التأمين السنوي" } });
        var scheduleId = schedule.GetProperty("id").GetGuid();
        var due = await owner.PostAsync($"/api/v1/accounting/deferrals/{scheduleId}/post-due?asOf=2026-12-31", new { }, HttpStatusCode.OK);
        due.EnumerateArray().Count(static o => o.GetProperty("outcome").GetString() == "posted").ShouldBe(4, "September to December");
        var after = await owner.GetOkAsync($"/api/v1/accounting/deferrals/{scheduleId}");
        after.GetProperty("postedAmount").GetDecimal().ShouldBe(333.32m);
        after.GetProperty("remainingAmount").GetDecimal().ShouldBe(666.69m);
        after.GetProperty("status").GetString().ShouldBe("active");
        var balances = (await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/balances")).EnumerateArray().ToList();
        balances.Where(static b => b.GetProperty("accountCode").GetString() == "1410").Sum(static b => b.GetProperty("creditTc").GetDecimal()).ShouldBe(333.32m);
        balances.Where(static b => b.GetProperty("accountCode").GetString() == "6170").Sum(static b => b.GetProperty("debitTc").GetDecimal()).ShouldBe(333.32m);
        (await owner.PostAsync($"/api/v1/accounting/deferrals/{scheduleId}/post-due?asOf=2026-12-31", new { }, HttpStatusCode.OK)).GetArrayLength().ShouldBe(0, "nothing else is due");
        var cancelled = await owner.PostAsync($"/api/v1/accounting/deferrals/{scheduleId}/cancel", new { }, HttpStatusCode.OK);
        cancelled.GetProperty("lines").EnumerateArray().Count(static l => l.GetProperty("status").GetString() == "cancelled").ShouldBe(8);
    }

    [Fact]
    public async Task A_spreadsheet_imports_as_drafts_grouped_by_reference()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await CompanyReadyAsync(owner, "IMP", "IQD");
        var csv = string.Join('\n',
        [
            "journal_ref,posting_date,currency,account_code,debit,credit,description",
            "J1,2026-09-10,IQD,6100,2000000,,Salaries",
            "J1,2026-09-10,IQD,2190,,2000000,Salaries",
            "J2,2026-09-12,IQD,6130,150000,,\"Ads, September\"",
            "J2,2026-09-12,IQD,2170,,150000,\"Ads, September\"",
        ]);
        using var upload = new StringContent(csv, Encoding.UTF8, "text/csv");
        var imported = await owner.PostContentAsync($"/api/v1/accounting/companies/{companyId}/journals/import", upload);
        imported.GetProperty("created").GetInt32().ShouldBe(2);
        var second = await owner.GetOkAsync($"/api/v1/accounting/journals/{imported.GetProperty("journalIds").EnumerateArray().Last().GetGuid()}");
        second.GetProperty("reference").GetString().ShouldBe("J2");
        second.GetProperty("lines").EnumerateArray().First().GetProperty("description").GetProperty("en").GetString().ShouldBe("Ads, September");
        second.GetProperty("totalDebit").GetDecimal().ShouldBe(150000m);

        using var broken = new StringContent(csv.Replace("6130", "6999", StringComparison.Ordinal), Encoding.UTF8, "text/csv");
        var failed = await owner.PostContentAsync($"/api/v1/accounting/companies/{companyId}/journals/import", broken, HttpStatusCode.NotFound);
        failed.GetProperty("why").GetProperty("journal").GetInt32().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/journals")).GetProperty("items").GetArrayLength().ShouldBe(2, "all or nothing");
        var json = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals/import", new { journals = new[] { new { postingDate = "2026-09-15", currency = "IQD", lines = new[] { Line("6150", debit: 40000m), Line("2170", credit: 40000m) } } } }, HttpStatusCode.OK);
        json.GetProperty("created").GetInt32().ShouldBe(1);
        JsonSerializer.Serialize(json).ShouldContain("journalIds");
    }
}

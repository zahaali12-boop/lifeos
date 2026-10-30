using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Sales.Tests;

/// <summary>
/// Invoicing extensions (roadmap 5.6b): a line deferred over periods parks its revenue on deferred revenue and
/// Accounting's schedule releases it month by month; a sales rep's commission accrues with the invoice under their
/// plan's tiers for the month and a credit note gives it back; recurring invoices issue on their cron schedule and the
/// daily run catches up what is due; a posted invoice under a regime with an e-invoicing scheme waits for clearance.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvoicingExtensionsTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Cleaning, Guid Customer, Guid StandardCode);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "INX", legalName = Name("Riyadh Services", "الرياض للخدمات"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId = company });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = company, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service", listPrice = 50m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "SERVICES") })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "DAMMAM-CO", legalName = Name("Dammam Co", "شركة الدمام"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        var standardCode = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        return new Setup(ws, owner, company, cleaning, customer, standardCode);
    }

    private static Dictionary<string, (decimal Debit, decimal Credit)> ByRole(JsonElement entry) =>
        entry.GetProperty("lines").EnumerateArray().GroupBy(static l => l.GetProperty("accountRole").GetString()!, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => (g.Sum(static l => l.GetProperty("debitTc").GetDecimal()), g.Sum(static l => l.GetProperty("creditTc").GetDecimal())), StringComparer.Ordinal);

    private static object ServiceLine(Setup s, string description, decimal amount, int? deferralPeriods = null, string? deferralStartsOn = null) =>
        new { kind = "service", quantity = 1m, unitPrice = amount, description, taxCodeId = s.StandardCode, deferralPeriods, deferralStartsOn };

    private static async Task<JsonElement> PostedInvoiceAsync(Setup s, string documentDate, params object[] lines)
    {
        var draft = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, documentDate, lines });
        return await s.Owner.PostAsync($"/api/v1/sales/invoices/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deferred_line_parks_its_revenue_and_the_schedule_releases_it_monthly_so_the_invoice_is_credited_not_reversed_once_recognition_began()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new[] { ServiceLine(s, "Support", 1200m, deferralPeriods: 0) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.deferral_periods");
        (await owner.PostErrorAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "credit_note", lines = new[] { ServiceLine(s, "Support", 100m, deferralPeriods: 2) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.deferral_on_credit_note");

        // A year of support billed up front: receivables carry the gross, deferred revenue the net, nothing reaches revenue yet.
        var posted = await PostedInvoiceAsync(s, "2026-09-01", ServiceLine(s, "Annual support", 1200m, deferralPeriods: 12, deferralStartsOn: "2026-09-01"), ServiceLine(s, "Onboarding", 300m));
        var byRole = ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{posted.GetProperty("journalEntryId").GetGuid()}"));
        byRole["AR"].ShouldBe((1725m, 0m));
        byRole["DeferredRevenue"].ShouldBe((0m, 1200m));
        byRole["Revenue"].ShouldBe((0m, 300m));
        byRole["OutputTax"].ShouldBe((0m, 225m));
        var line = posted.GetProperty("lines")[0];
        line.GetProperty("deferralPeriods").GetInt32().ShouldBe(12);
        var scheduleId = line.GetProperty("deferralScheduleId").GetGuid();
        var schedule = (await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/deferrals")).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == scheduleId);
        (schedule.GetProperty("kind").GetString(), schedule.GetProperty("status").GetString(), schedule.GetProperty("balanceAccountCode").GetString(), schedule.GetProperty("targetAccountCode").GetString(), schedule.GetProperty("lines").GetArrayLength()).ShouldBe(("deferred_revenue", "active", "2180", "4100", 12));
        schedule.GetProperty("lines").EnumerateArray().Sum(static l => l.GetProperty("amount").GetDecimal()).ShouldBe(1200m);
        await owner.AssertInvariantsAsync();

        // A second deferred invoice reversed before any recognition cancels its schedule.
        var early = await PostedInvoiceAsync(s, "2026-09-02", ServiceLine(s, "Hosting", 600m, deferralPeriods: 6, deferralStartsOn: "2026-09-01"));
        var earlyId = early.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/sales/invoices/{earlyId}/reverse", new { reason = "Duplicate" }, HttpStatusCode.OK);
        var earlySchedule = early.GetProperty("lines")[0].GetProperty("deferralScheduleId").GetGuid();
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/deferrals")).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == earlySchedule).GetProperty("status").GetString().ShouldBe("cancelled");

        // The daily routines release September's hundred to revenue; from then on the invoice is credited, never reversed.
        var run = await owner.PostAsync($"/api/v1/accounting/routines/run?companyId={s.CompanyId}&asOf=2026-09-30", new { }, HttpStatusCode.OK);
        run.GetProperty("deferralPostings").EnumerateArray().ShouldContain(p => p.GetProperty("targetId").GetGuid() == scheduleId && p.GetProperty("outcome").GetString() == "posted");
        var recognised = (await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/deferrals")).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == scheduleId);
        recognised.GetProperty("postedAmount").GetDecimal().ShouldBe(100m);
        var recognition = recognised.GetProperty("lines").EnumerateArray().First(static l => l.GetProperty("status").GetString() == "posted");
        // The schedule posts by account, so its lines are read by code: Dr 2180 deferred revenue / Cr 4100 revenue.
        var recognitionEntry = (await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{recognition.GetProperty("journalEntryId").GetGuid()}")).GetProperty("lines").EnumerateArray()
            .ToDictionary(static l => l.GetProperty("accountCode").GetString()!, static l => (l.GetProperty("debitTc").GetDecimal(), l.GetProperty("creditTc").GetDecimal()), StringComparer.Ordinal);
        recognitionEntry["2180"].ShouldBe((100m, 0m));
        recognitionEntry["4100"].ShouldBe((0m, 100m));
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{posted.GetProperty("id").GetGuid()}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.deferral_recognised");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_sales_reps_commission_accrues_with_the_invoice_across_the_plans_monthly_tiers_and_a_credit_note_gives_it_back()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        // 2% of the first 1,000 SAR a month and 3% above, accrued at invoice.
        var plan = (await owner.PostAsync("/api/v1/partners/commission-plans", new { code = "SRV-2026", name = Name("Services 2026", "الخدمات ٢٠٢٦"), currency = "SAR", basis = "revenue", accrualPoint = "invoice", tierPeriod = "month", rules = new object[] { new { ratePct = 2, fromAmount = 0 }, new { ratePct = 3, fromAmount = 1000 } } })).GetProperty("id").GetGuid();
        var rep = (await owner.PostAsync("/api/v1/partners/sales-reps", new { code = "REP-1", name = Name("Huda Salim", "هدى سالم"), companyId = s.CompanyId, commissionPlanId = plan })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/customer-accounts/{s.CompanyId}", new { currency = "SAR", salesRepId = rep, taxGroupId = (await owner.GetOkAsync("/api/v1/tax/groups")).EnumerateArray().Single(g => g.GetProperty("kind").GetString() == "partner" && g.GetProperty("code").GetString() == "DOMESTIC").GetProperty("id").GetGuid() });

        // 800 in the month: 2% = 16. Then 600 more: 200 at 2% and 400 at 3% = 16 again, because the first invoice filled the band.
        var first = await PostedInvoiceAsync(s, "2026-09-05", ServiceLine(s, "Consulting", 800m));
        var firstCommission = first.GetProperty("commissions").EnumerateArray().Single();
        (firstCommission.GetProperty("basisAmount").GetDecimal(), firstCommission.GetProperty("commissionAmount").GetDecimal(), firstCommission.GetProperty("currency").GetString(), firstCommission.GetProperty("status").GetString()).ShouldBe((800m, 16m, "SAR", "accrued"));
        var accrual = ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{firstCommission.GetProperty("journalEntryId").GetGuid()}"));
        accrual["CommissionExpense"].ShouldBe((16m, 0m));
        accrual["CommissionPayable"].ShouldBe((0m, 16m));
        var second = await PostedInvoiceAsync(s, "2026-09-12", ServiceLine(s, "Consulting", 600m));
        second.GetProperty("commissions").EnumerateArray().Single().GetProperty("commissionAmount").GetDecimal().ShouldBe(16m);

        // A credit of 600 the same month gives back what those 600 earned on their way down the bands: 400 at 3% and 200 at 2%.
        var note = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "credit_note", documentDate = "2026-09-20", lines = new[] { ServiceLine(s, "Consulting not delivered", 600m) } });
        var postedNote = await owner.PostAsync($"/api/v1/sales/invoices/{note.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var giveBack = postedNote.GetProperty("commissions").EnumerateArray().Single();
        (giveBack.GetProperty("basisAmount").GetDecimal(), giveBack.GetProperty("commissionAmount").GetDecimal()).ShouldBe((-600m, -16m));
        var giveBackEntry = ByRole(await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{giveBack.GetProperty("journalEntryId").GetGuid()}"));
        giveBackEntry["CommissionExpense"].ShouldBe((0m, 16m));
        giveBackEntry["CommissionPayable"].ShouldBe((16m, 0m));

        // Reversing the invoice reverses its accrual; a new month starts the tiers afresh.
        var reversed = await owner.PostAsync($"/api/v1/sales/invoices/{second.GetProperty("id").GetGuid()}/reverse", new { reason = "Issued twice" }, HttpStatusCode.OK);
        reversed.GetProperty("commissions").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("reversed");
        var october = await PostedInvoiceAsync(s, "2026-10-03", ServiceLine(s, "Consulting", 500m));
        october.GetProperty("commissions").EnumerateArray().Single().GetProperty("commissionAmount").GetDecimal().ShouldBe(10m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_recurring_invoice_issues_on_its_schedule_the_run_catches_up_what_is_due_and_stops_at_its_end()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var lines = new object[] { new { kind = "item", itemId = s.Cleaning, quantity = 10m, unitPrice = 50m }, new { kind = "service", quantity = 1m, unitPrice = 25m, description = "Supplies", taxCodeId = s.StandardCode } };
        (await owner.PostErrorAsync("/api/v1/sales/recurring-invoices", new { companyId = s.CompanyId, partnerId = s.Customer, code = "CLEAN-M", name = Name("Monthly cleaning", "تنظيف شهري"), cron = "every month", lines }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("schedule.cron_invalid");
        (await owner.PostErrorAsync("/api/v1/sales/recurring-invoices", new { companyId = s.CompanyId, partnerId = s.Customer, code = "CLEAN-M", name = Name("Monthly cleaning", "تنظيف شهري"), cron = "0 9 1 * *", lines = new object[] { new { kind = "shipment", shipmentLineId = Guid.NewGuid(), quantity = 1m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("recurring_invoice.line_kind_invalid");

        var template = await owner.PostAsync("/api/v1/sales/recurring-invoices", new { companyId = s.CompanyId, partnerId = s.Customer, code = "CLEAN-M", name = Name("Monthly cleaning", "تنظيف شهري"), cron = "0 9 1 * *", startsOn = "2026-09-01", endsOn = "2026-11-30", autoPost = true, customerReference = "FM-2026", lines });
        template.GetProperty("nextRunOn").GetString().ShouldBe("2026-09-01");
        var templateId = template.GetProperty("id").GetGuid();

        // One run by hand: the invoice is priced and taxed on the run date, posted at once, and the schedule moves a month on.
        var september = await owner.PostAsync($"/api/v1/sales/recurring-invoices/{templateId}/generate", new { });
        (september.GetProperty("status").GetString(), september.GetProperty("documentDate").GetString(), september.GetProperty("customerReference").GetString(), september.GetProperty("totalNet").GetDecimal()).ShouldBe(("posted", "2026-09-01", "FM-2026", 525m));
        september.GetProperty("recurringInvoiceId").GetGuid().ShouldBe(templateId);
        (await owner.GetOkAsync($"/api/v1/sales/recurring-invoices/{templateId}")).GetProperty("nextRunOn").GetString().ShouldBe("2026-10-01");

        // The daily run catches up October and November, then the schedule ends.
        var runs = await owner.PostAsync($"/api/v1/sales/recurring-invoices/run?companyId={s.CompanyId}&asOf=2026-12-15", new { }, HttpStatusCode.OK);
        runs.EnumerateArray().Select(static r => (r.GetProperty("runDate").GetString(), r.GetProperty("outcome").GetString())).ShouldBe([("2026-10-01", "generated"), ("2026-11-01", "generated")]);
        var ended = await owner.GetOkAsync($"/api/v1/sales/recurring-invoices/{templateId}");
        ended.GetProperty("nextRunOn").ValueKind.ShouldBe(JsonValueKind.Null);
        ended.GetProperty("generated").GetInt32().ShouldBe(3);
        (await owner.GetOkAsync($"/api/v1/sales/invoices?companyId={s.CompanyId}&status=posted")).GetArrayLength().ShouldBe(3);
        (await owner.PostErrorAsync($"/api/v1/sales/recurring-invoices/{templateId}/generate", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("recurring_invoice.ended");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_posted_invoice_under_a_regime_with_an_einvoicing_scheme_waits_for_clearance_and_submission_is_refused_until_an_adapter_exists()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var draft = await owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new[] { ServiceLine(s, "Consulting", 100m) } });
        draft.GetProperty("einvoiceScheme").GetString().ShouldBe("zatca");
        draft.GetProperty("einvoiceStatus").GetString().ShouldBe("not_required", "a draft is nothing to clear yet");
        var invoiceId = draft.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{invoiceId}/einvoice/submit", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.not_posted");

        var posted = await owner.PostAsync($"/api/v1/sales/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("einvoiceStatus").GetString().ShouldBe("pending");
        (await owner.PostErrorAsync($"/api/v1/sales/invoices/{invoiceId}/einvoice/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax.clearance_not_configured");
        (await owner.GetOkAsync($"/api/v1/sales/invoices/{invoiceId}")).GetProperty("einvoiceStatus").GetString().ShouldBe("pending");
    }
}

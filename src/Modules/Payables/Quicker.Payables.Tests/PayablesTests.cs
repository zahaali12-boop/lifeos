using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Payables.Tests;

/// <summary>The payables subledger (roadmap 4.7, hard scenario 15): aging at any date equals the control account at that date; holds keep an item out of proposals and applications; a proposal picks what is due, is edited, approved and cancelled.</summary>
[Collection(ApiCollection.Name)]
public sealed class PayablesTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Supplier, Guid Other);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "AP", legalName = Name("Payables Co", "شركة الذمم"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });
        var other = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-B", legalName = Name("Beta Trading", "بيتا"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{other}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });
        return new Setup(ws, owner, companyId, supplier, other);
    }

    private static async Task<Guid> InvoiceAsync(Setup s, Guid partner, string reference, string date, decimal amount)
    {
        var invoice = await s.Owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = partner, kind = "expense", supplierInvoiceNumber = reference, documentDate = date, lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = amount, description = "Rent" } } });
        var id = invoice.GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/purchasing/invoices/{id}/submit", new { }, HttpStatusCode.OK);
        var posted = await s.Owner.PostAsync($"/api/v1/purchasing/invoices/{id}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<decimal> ControlAsync(Setup s, string asOf)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.credit_fc - l.debit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = 'AP' AND l.posting_date <= @d::date", new { t = s.Ws.TenantId, c = s.CompanyId, d = asOf });
    }

    [Fact]
    public async Task Aging_at_any_date_equals_the_control_account_holds_keep_items_out_and_a_proposal_picks_what_is_due()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var august = await InvoiceAsync(s, s.Supplier, "A-08", "2026-08-01", 300_000m);
        var early = await InvoiceAsync(s, s.Supplier, "A-09", "2026-09-10", 120_000m);
        var late = await InvoiceAsync(s, s.Other, "B-09", "2026-09-20", 80_000m);

        // Aging today (the 22nd): 52 days on the August invoice, 12 and 2 days on the September ones; the totals are the control account.
        var today = await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={s.CompanyId}");
        today.GetProperty("asOf").GetString().ShouldBe("2026-09-22");
        var rows = today.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        var alpha = rows.Single(r => r.GetProperty("partnerCode").GetString() == "SUP-A");
        alpha.GetProperty("days31To60").GetDecimal().ShouldBe(300_000m);
        alpha.GetProperty("days1To30").GetDecimal().ShouldBe(120_000m);
        alpha.GetProperty("totalFc").GetDecimal().ShouldBe(420_000m);
        today.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(500_000m);
        (await ControlAsync(s, "2026-09-22")).ShouldBe(500_000m);

        // Aging in mid-August sees only the August invoice, 14 days old, and again equals the control account then.
        var midAugust = await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={s.CompanyId}&asOf=2026-08-15");
        midAugust.GetProperty("rows").GetArrayLength().ShouldBe(1);
        midAugust.GetProperty("totals").GetProperty("days1To30").GetDecimal().ShouldBe(300_000m);
        midAugust.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(await ControlAsync(s, "2026-08-15"));
        (await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={s.CompanyId}&asOf=2026-07-31")).GetProperty("rows").GetArrayLength().ShouldBe(0);

        // A hold: named reason, visible on the item, refused twice.
        (await owner.PostErrorAsync($"/api/v1/payables/open-items/{late}/hold", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payables.hold_reason_required");
        var held = await owner.PostAsync($"/api/v1/payables/open-items/{late}/hold", new { reason = "Disputed quantity" }, HttpStatusCode.OK);
        held.GetProperty("item").GetProperty("paymentBlocked").GetBoolean().ShouldBeTrue();
        held.GetProperty("item").GetProperty("blockReason").GetString().ShouldBe("Disputed quantity");
        (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&status=live")).GetArrayLength().ShouldBe(3);

        // The proposal for the end of the month: the two unheld invoices, nothing for the held one; edited, approved, then cancelled.
        (await owner.PostErrorAsync("/api/v1/payables/proposals", new { companyId = s.CompanyId, payThrough = "2026-09-30", currency = "USD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("proposal.nothing_due");
        (await owner.PostErrorAsync("/api/v1/payables/proposals", new { companyId = s.CompanyId, payThrough = "2026-09-01", currency = "IQD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("proposal.pay_through_invalid");
        var proposal = await owner.PostAsync("/api/v1/payables/proposals", new { companyId = s.CompanyId, payThrough = "2026-09-30", currency = "IQD" });
        var proposalId = proposal.GetProperty("id").GetGuid();
        proposal.GetProperty("number").GetString().ShouldStartWith("PP-2026-");
        proposal.GetProperty("status").GetString().ShouldBe("draft");
        var lines = proposal.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(2);
        lines.Select(static l => l.GetProperty("openItemId").GetGuid()).ShouldBe([august, early], ignoreOrder: true);
        proposal.GetProperty("totalTc").GetDecimal().ShouldBe(420_000m);
        var earlyLine = lines.Single(l => l.GetProperty("openItemId").GetGuid() == early).GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/payables/proposals/{proposalId}/lines", new { lines = new[] { new { lineId = earlyLine, selected = true, amountTc = 200_000m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("proposal.amount_invalid");
        var edited = await owner.PutAsync($"/api/v1/payables/proposals/{proposalId}/lines", new { lines = new[] { new { lineId = earlyLine, selected = true, amountTc = 50_000m } } });
        edited.GetProperty("totalTc").GetDecimal().ShouldBe(350_000m);
        var approved = await owner.PostAsync($"/api/v1/payables/proposals/{proposalId}/approve", new { }, HttpStatusCode.OK);
        approved.GetProperty("status").GetString().ShouldBe("approved");
        (await owner.PutErrorAsync($"/api/v1/payables/proposals/{proposalId}/lines", new { lines = Array.Empty<object>() }, HttpStatusCode.Conflict)).Code.ShouldBe("proposal.not_draft");
        (await owner.PostAsync($"/api/v1/payables/proposals/{proposalId}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");
        (await owner.GetOkAsync($"/api/v1/payables/proposals?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(1);

        // Released, the held invoice is proposed as well.
        await owner.PostAsync($"/api/v1/payables/open-items/{late}/release", new { }, HttpStatusCode.OK);
        (await owner.PostErrorAsync($"/api/v1/payables/open-items/{late}/release", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("payables.item_not_held");
        var second = await owner.PostAsync("/api/v1/payables/proposals", new { companyId = s.CompanyId, payThrough = "2026-09-30", currency = "IQD", partnerId = s.Other });
        second.GetProperty("lines").GetArrayLength().ShouldBe(1);
        await owner.DeleteOkAsync($"/api/v1/payables/proposals/{second.GetProperty("id").GetGuid()}");
        await owner.AssertInvariantsAsync();
    }
}

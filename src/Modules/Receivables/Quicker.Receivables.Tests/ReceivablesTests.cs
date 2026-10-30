using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Receivables.Tests;

/// <summary>The receivables subledger (roadmap 5.6a, hard scenario 15 on the customer side): a journal on the control opens the customer's item and reversing it follows; a credit applied at another rate books realised FX; aging at any date equals the control at that date; a hold keeps an item out of applications; a statement carries the balance forward.</summary>
[Collection(ApiCollection.Name)]
public sealed class ReceivablesTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Customer, Guid Other);

    private async Task<Setup> SetUpAsync(string functionalCurrency = "IQD")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "AR", legalName = Name("Receivables Co", "شركة الذمم المدينة"), country = "IQ", functionalCurrency, timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = functionalCurrency });
        var other = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-B", legalName = Name("Beta Stores", "بيتا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{other}/customer-accounts/{companyId}", new { currency = functionalCurrency });
        return new Setup(ws, owner, companyId, customer, other);
    }

    private async Task<HttpClient> InviteAsync(Setup s, string roleCode, params string[] grants)
    {
        var role = await s.Owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{s.Ws.Slug}@example.test";
        await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
    }

    private static object Line(string account, decimal debit = 0m, decimal credit = 0m, Guid? customer = null, string? dueDate = null) =>
        new { accountCode = account, debit, credit, subledgerType = customer is null ? null : "AR", subledgerRef = customer, dueDate };

    /// <summary>A sale booked by journal (Dr receivables / Cr revenue), posted, with its entry id.</summary>
    private static async Task<Guid> SaleAsync(Setup s, string date, decimal amount, Guid? customer = null, string? dueDate = null, string currency = "IQD")
    {
        var draft = await s.Owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = date, currency, description = Name("Sale", "بيع"), lines = new[] { Line("1210", debit: amount, customer: customer ?? s.Customer, dueDate: dueDate), Line("4100", credit: amount) } });
        var posted = await s.Owner.PostAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("journalEntryId").GetGuid();
    }

    private static async Task<List<JsonElement>> ItemsAsync(Setup s, Guid? partner = null, string? status = null) =>
        (await s.Owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={s.CompanyId}&partnerId={partner ?? s.Customer}" + (status is null ? string.Empty : $"&status={status}"))).EnumerateArray().Select(static o => o.GetProperty("item")).ToList();

    private async Task<decimal> ControlAsync(Setup s, string asOf)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = 'AR' AND l.posting_date <= @d::date", new { t = s.Ws.TenantId, c = s.CompanyId, d = asOf });
    }

    [Fact]
    public async Task A_journal_on_the_receivables_control_opens_the_customers_item_and_reversing_it_follows()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Only a customer can be named on the receivables control.
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("A supplier", "مورد"), isSupplier = true })).GetProperty("id").GetGuid();
        foreach (var notACustomer in new[] { supplier, Guid.NewGuid() })
        {
            var draft = await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = "2026-09-05", currency = "IQD", lines = new[] { Line("1210", debit: 100m, customer: notACustomer), Line("4100", credit: 100m) } });
            (await owner.PostErrorAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.customer_required");
        }

        // A sale owed by the customer: the debit opens an item owed by them, due when the line says.
        var saleEntry = await SaleAsync(s, "2026-09-05", 1000m, dueDate: "2026-10-15");
        var owed = (await ItemsAsync(s)).ShouldHaveSingleItem();
        (owed.GetProperty("kind").GetString(), owed.GetProperty("documentType").GetString(), owed.GetProperty("originalTc").GetDecimal(), owed.GetProperty("dueDate").GetString()).ShouldBe(("adjustment", "journal_entry", 1000m, "2026-10-15"));
        await owner.AssertInvariantsAsync();

        // A credit to the customer by journal (a credit on the control) is theirs, and applies to the sale like any credit.
        var credit = await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = "2026-09-08", currency = "IQD", lines = new[] { Line("4100", debit: 300m), Line("1210", credit: 300m, customer: s.Customer) } });
        await owner.PostAsync($"/api/v1/accounting/journals/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var creditItem = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == -300m);
        var applied = await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = creditItem.GetProperty("id").GetGuid(), settledItemId = owed.GetProperty("id").GetGuid(), amount = 300m }, HttpStatusCode.OK);
        applied.GetProperty("kind").GetString().ShouldBe("credit_application");
        applied.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(0m);
        applied.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == owed.GetProperty("id").GetGuid()).GetProperty("remainingTc").GetDecimal().ShouldBe(700m);
        await owner.AssertInvariantsAsync();

        // A settled item is not reversed from under its settlement.
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{saleEntry}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.Conflict)).Code.ShouldBe("receivables.item_settled");

        // Reversing the application reopens both, then the sale's reversal follows.
        var settlementId = applied.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/receivables/settlements/{settlementId}/reverse", new { reason = "Applied to the wrong sale" }, HttpStatusCode.OK);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == owed.GetProperty("id").GetGuid()).GetProperty("remainingTc").GetDecimal().ShouldBe(1000m);
        await owner.PostAsync($"/api/v1/accounting/journal-entries/{saleEntry}/reverse", new { reason = "Wrong customer" });
        (await ItemsAsync(s)).ShouldNotContain(i => i.GetProperty("originalTc").GetDecimal() == 1000m);
        (await ItemsAsync(s, status: "reversed")).ShouldHaveSingleItem().GetProperty("originalTc").GetDecimal().ShouldBe(1000m);
        await owner.AssertInvariantsAsync();

        // A deposit by journal (the cash side kept out of the bank control here, which takes documents only) sits on its own control and is applied to a sale as a deposit application through a journal of its own.
        var deposit = await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = "2026-09-09", currency = "IQD", lines = new[] { Line("6110", debit: 500m), Line("2160", credit: 500m, customer: s.Customer) } });
        await owner.PostAsync($"/api/v1/accounting/journals/{deposit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var depositItem = (await ItemsAsync(s)).Single(i => i.GetProperty("kind").GetString() == "deposit");
        depositItem.GetProperty("originalTc").GetDecimal().ShouldBe(-500m);
        await SaleAsync(s, "2026-09-10", 800m);
        var sale2 = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == 800m);
        var depositApplied = await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = depositItem.GetProperty("id").GetGuid(), settledItemId = sale2.GetProperty("id").GetGuid(), amount = 500m }, HttpStatusCode.OK);
        depositApplied.GetProperty("kind").GetString().ShouldBe("deposit_application");
        depositApplied.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == depositItem.GetProperty("id").GetGuid()).GetProperty("status").GetString().ShouldBe("settled");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_credit_booked_at_another_rate_applies_with_realised_fx_and_aging_equals_the_control_at_any_date()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        async Task RateAsync(string date, decimal rate) =>
            await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = date, rate });
        await RateAsync("2026-09-01", 1300m);
        await RateAsync("2026-09-15", 1320m);
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/customer-accounts/{s.CompanyId}", new { currency = "USD" });

        // A USD sale at 1300 and a USD credit at 1320: applying the credit relieves the receivable at its booked value and books the 20/USD as realised FX gain.
        await SaleAsync(s, "2026-09-02", 100m, dueDate: "2026-09-20", currency: "USD");
        var credit = await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = "2026-09-16", currency = "USD", lines = new[] { Line("4100", debit: 40m), Line("1210", credit: 40m, customer: s.Customer) } });
        await owner.PostAsync($"/api/v1/accounting/journals/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var sale = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == 100m);
        sale.GetProperty("originalFc").GetDecimal().ShouldBe(130_000m);
        var creditItem = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == -40m);
        creditItem.GetProperty("originalFc").GetDecimal().ShouldBe(-52_800m);
        var applied = await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = creditItem.GetProperty("id").GetGuid(), settledItemId = sale.GetProperty("id").GetGuid(), amount = 40m, settlementDate = "2026-09-16" }, HttpStatusCode.OK);
        applied.GetProperty("amountFcSettledItem").GetDecimal().ShouldBe(52_000m);
        applied.GetProperty("amountFcSettlingItem").GetDecimal().ShouldBe(52_800m);
        applied.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(800m);
        var fxEntry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{applied.GetProperty("journalEntryId").GetGuid()}");
        fxEntry.GetProperty("lines").EnumerateArray().Single(static l => l.GetProperty("accountRole").GetString() == "FxGainRealized").GetProperty("creditFc").GetDecimal().ShouldBe(800m);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == sale.GetProperty("id").GetGuid()).GetProperty("remainingFc").GetDecimal().ShouldBe(78_000m);
        await owner.AssertInvariantsAsync();

        // More sales, one of them reversed later, and the other customer: aging at two sampled dates equals the control at each.
        await SaleAsync(s, "2026-09-05", 60m, customer: s.Other, dueDate: "2026-09-10", currency: "USD");
        var wrongEntry = await SaleAsync(s, "2026-09-18", 25m, dueDate: "2026-10-18", currency: "USD");
        await owner.PostAsync($"/api/v1/accounting/journal-entries/{wrongEntry}/reverse", new { reason = "Booked twice", reversalDate = "2026-09-25" });
        foreach (var (asOf, expectedRows) in new[] { ("2026-09-14", 2), ("2026-09-20", 2), ("2026-10-31", 2) })
        {
            var aging = await owner.GetOkAsync($"/api/v1/receivables/open-items/aging?companyId={s.CompanyId}&asOf={asOf}");
            aging.GetProperty("rows").GetArrayLength().ShouldBe(expectedRows);
            aging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(await ControlAsync(s, asOf), $"aging at {asOf}");
        }

        var late = await owner.GetOkAsync($"/api/v1/receivables/open-items/aging?companyId={s.CompanyId}&asOf=2026-10-31");
        var alpha = late.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("partnerCode").GetString() == "CUS-A");
        alpha.GetProperty("days31To60").GetDecimal().ShouldBe(78_000m);
        var beta = late.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("partnerCode").GetString() == "CUS-B");
        beta.GetProperty("days31To60").GetDecimal().ShouldBe(78_000m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_held_item_takes_no_application_and_a_statement_carries_the_balance_forward_with_reversals_shown()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await SaleAsync(s, "2026-08-01", 300_000m, dueDate: "2026-08-31");
        await SaleAsync(s, "2026-09-10", 120_000m, dueDate: "2026-10-10");
        var wrongEntry = await SaleAsync(s, "2026-09-15", 50_000m);
        await owner.PostAsync($"/api/v1/accounting/journal-entries/{wrongEntry}/reverse", new { reason = "Billed twice", reversalDate = "2026-09-20" });
        var credit = await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = "2026-09-12", currency = "IQD", lines = new[] { Line("4100", debit: 20_000m), Line("1210", credit: 20_000m, customer: s.Customer) } });
        await owner.PostAsync($"/api/v1/accounting/journals/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);

        var statement = await owner.GetOkAsync($"/api/v1/receivables/statement?companyId={s.CompanyId}&partnerId={s.Customer}&from=2026-09-01&to=2026-09-30");
        var iqd = statement.GetProperty("currencies").EnumerateArray().Single();
        iqd.GetProperty("opening").GetDecimal().ShouldBe(300_000m);
        var lines = iqd.GetProperty("lines").EnumerateArray().Select(static l => (l.GetProperty("date").GetString(), l.GetProperty("amount").GetDecimal(), l.GetProperty("reversal").GetBoolean())).ToList();
        lines.ShouldBe([("2026-09-10", 120_000m, false), ("2026-09-12", -20_000m, false), ("2026-09-15", 50_000m, false), ("2026-09-20", -50_000m, true)]);
        iqd.GetProperty("closing").GetDecimal().ShouldBe(400_000m);
        (await ItemsAsync(s, status: "live")).Sum(static i => i.GetProperty("remainingTc").GetDecimal()).ShouldBe(400_000m);

        // A held invoice takes no application until released; the hold needs a reason and a manage permission.
        var old = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == 300_000m);
        var creditItem = (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == -20_000m);
        var reader = await InviteAsync(s, "ar_reader", "receivables.open_item.read");
        (await reader.PostErrorAsync($"/api/v1/receivables/open-items/{old.GetProperty("id").GetGuid()}/hold", new { reason = "Dispute" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        (await owner.PostErrorAsync($"/api/v1/receivables/open-items/{old.GetProperty("id").GetGuid()}/hold", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receivables.hold_reason_required");
        var held = await owner.PostAsync($"/api/v1/receivables/open-items/{old.GetProperty("id").GetGuid()}/hold", new { reason = "Dispute over damaged goods" }, HttpStatusCode.OK);
        held.GetProperty("item").GetProperty("onHold").GetBoolean().ShouldBeTrue();
        (await owner.PostErrorAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = creditItem.GetProperty("id").GetGuid(), settledItemId = old.GetProperty("id").GetGuid(), amount = 20_000m }, HttpStatusCode.Conflict)).Code.ShouldBe("settlement.item_blocked");
        await owner.PostAsync($"/api/v1/receivables/open-items/{old.GetProperty("id").GetGuid()}/release", new { }, HttpStatusCode.OK);
        await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = creditItem.GetProperty("id").GetGuid(), settledItemId = old.GetProperty("id").GetGuid(), amount = 20_000m }, HttpStatusCode.OK);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == old.GetProperty("id").GetGuid()).GetProperty("remainingTc").GetDecimal().ShouldBe(280_000m);

        // The reader sees the items and the partner's 360 view shows the receivable side.
        (await reader.GetOkAsync($"/api/v1/receivables/open-items?companyId={s.CompanyId}&partnerId={s.Customer}&status=live")).GetArrayLength().ShouldBe(2);
        var activity = await owner.GetOkAsync($"/api/v1/partners/{s.Customer}/customer-360");
        var panel = activity.GetProperty("panels").EnumerateArray().Single(static p => p.GetProperty("source").GetString() == "receivables");
        panel.GetProperty("balances").EnumerateArray().Single().GetProperty("open").GetDecimal().ShouldBe(400_000m);
        await owner.AssertInvariantsAsync();
    }
}

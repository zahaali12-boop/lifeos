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

    [Fact]
    public async Task A_promise_to_pay_is_recorded_and_a_balance_is_written_off_in_part_then_in_full_and_a_write_off_reversed()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await SaleAsync(s, "2026-09-05", 1000m, dueDate: "2026-09-15");
        var owed = (await ItemsAsync(s)).ShouldHaveSingleItem();
        var owedId = owed.GetProperty("id").GetGuid();

        // The customer promises to pay by the 1st; a date in the past is refused; the promise can be cleared.
        (await owner.PostErrorAsync($"/api/v1/receivables/open-items/{owedId}/promise", new { promisedOn = "2026-09-01", note = "Late" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receivables.promise_date_past");
        var promised = await owner.PostAsync($"/api/v1/receivables/open-items/{owedId}/promise", new { promisedOn = "2026-10-01", note = "Cheque in the post, said the buyer" }, HttpStatusCode.OK);
        promised.GetProperty("item").GetProperty("promisedPaymentDate").GetString().ShouldBe("2026-10-01");
        var cleared = await owner.PostAsync($"/api/v1/receivables/open-items/{owedId}/promise", new { promisedOn = (string?)null }, HttpStatusCode.OK);
        cleared.GetProperty("item").GetProperty("promisedPaymentDate").ValueKind.ShouldBe(JsonValueKind.Null);

        // 300 of it written off as a small balance: Dr write-off / Cr receivables, the item stays open for 700, the control agrees.
        var clerk = await InviteAsync(s, "ar_clerk_no_wo", "receivables.open_item.read", "receivables.open_item.manage");
        (await clerk.PostErrorAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "Not worth chasing", amount = 300m, kind = "small_balance" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        (await owner.PostErrorAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "", amount = 300m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("write_off.reason_required");
        (await owner.PostErrorAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "Too much", amount = 1500m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("write_off.amount_invalid");
        var small = await owner.PostAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "Not worth chasing", amount = 300m, kind = "small_balance", writeOffDate = "2026-09-20" });
        small.GetProperty("number").GetString().ShouldStartWith("WO-2026-");
        small.GetProperty("status").GetString().ShouldBe("posted");
        small.GetProperty("amountTc").GetDecimal().ShouldBe(300m);
        var afterSmall = (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == owedId);
        afterSmall.GetProperty("remainingTc").GetDecimal().ShouldBe(700m);
        afterSmall.GetProperty("status").GetString().ShouldBe("partially_settled");
        (await ControlAsync(s, "2026-09-20")).ShouldBe(700m);
        (await RoleAsync(s, "WriteOff")).ShouldBe(300m);
        var writeOffSettlement = (await owner.GetOkAsync($"/api/v1/receivables/settlements?companyId={s.CompanyId}&openItemId={owedId}")).EnumerateArray().Single();
        writeOffSettlement.GetProperty("kind").GetString().ShouldBe("write_off");
        writeOffSettlement.GetProperty("writeOffTc").GetDecimal().ShouldBe(300m);
        (await owner.PostErrorAsync($"/api/v1/receivables/settlements/{writeOffSettlement.GetProperty("id").GetGuid()}/reverse", new { reason = "Oops" }, HttpStatusCode.Conflict)).Code.ShouldBe("settlement.reverse_document");
        (await owner.GetOkAsync($"/api/v1/receivables/write-offs?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(1);
        await owner.AssertInvariantsAsync();

        // Reversed: the item is owed in full again and the write-off's credit item is gone.
        var smallId = small.GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/receivables/write-offs/{smallId}/reverse", new { reason = "Customer paid after all", reversalDate = "2026-09-19" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("write_off.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/receivables/write-offs/{smallId}/reverse", new { reason = "Customer paid after all", reversalDate = "2026-09-21" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == owedId).GetProperty("remainingTc").GetDecimal().ShouldBe(1000m);
        (await ItemsAsync(s, status: "reversed")).ShouldHaveSingleItem().GetProperty("documentType").GetString().ShouldBe("ar_write_off");
        (await RoleAsync(s, "WriteOff")).ShouldBe(0m);
        (await ControlAsync(s, "2026-09-21")).ShouldBe(1000m);
        await owner.AssertInvariantsAsync();

        // Then the whole balance as bad debt: the item is settled, the control is empty, the statement shows the write-off.
        var bad = await owner.PostAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "Customer in liquidation" });
        bad.GetProperty("kind").GetString().ShouldBe("bad_debt");
        bad.GetProperty("amountTc").GetDecimal().ShouldBe(1000m);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == owedId).GetProperty("status").GetString().ShouldBe("settled");
        (await RoleAsync(s, "BadDebt")).ShouldBe(1000m);
        (await ControlAsync(s, "2026-09-22")).ShouldBe(0m);
        (await owner.PostErrorAsync($"/api/v1/receivables/open-items/{owedId}/write-off", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("write_off.item_not_open");
        var statement = await owner.GetOkAsync($"/api/v1/receivables/statement?companyId={s.CompanyId}&partnerId={s.Customer}&from=2026-09-01&to=2026-09-30");
        var iqd = statement.GetProperty("currencies").EnumerateArray().Single();
        iqd.GetProperty("closing").GetDecimal().ShouldBe(0m);
        iqd.GetProperty("lines").EnumerateArray().Count(static l => l.GetProperty("documentType").GetString() == "ar_write_off").ShouldBe(3, "two write-offs and the reversal of one");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Dunning_levels_climb_with_days_overdue_and_a_run_issues_letters_charges_fees_and_holds_credit_while_a_promise_keeps_an_item_out()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // The ladder: a reminder after a week, a second notice with a fee and a credit hold after a month; a rung out of order is refused.
        var first = await owner.PostAsync("/api/v1/receivables/dunning/levels", new { companyId = s.CompanyId, level = 1, daysOverdue = 7, name = Name("Reminder", "تذكير"), text = Name("Our records show the following items are past due.", "تشير سجلاتنا إلى تأخر سداد البنود التالية.") });
        first.GetProperty("feeCurrency").GetString().ShouldBe("IQD");
        await owner.PostAsync("/api/v1/receivables/dunning/levels", new { companyId = s.CompanyId, level = 2, daysOverdue = 30, name = Name("Second notice", "إشعار ثانٍ"), feeAmount = 5_000m, blockCustomer = true });
        (await owner.PostErrorAsync("/api/v1/receivables/dunning/levels", new { companyId = s.CompanyId, level = 3, daysOverdue = 10, name = Name("Final", "نهائي") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dunning.ladder_order");
        (await owner.PostErrorAsync("/api/v1/receivables/dunning/levels", new { companyId = s.CompanyId, level = 2, daysOverdue = 60, name = Name("Again", "مجددًا") }, HttpStatusCode.Conflict)).Code.ShouldBe("dunning.level_taken");
        (await owner.GetOkAsync($"/api/v1/receivables/dunning/levels?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2);

        // What is owed: an old invoice, a recent one, one not yet due, and another customer's overdue item with a promise to pay.
        await SaleAsync(s, "2026-07-01", 100_000m, dueDate: "2026-08-01");
        await SaleAsync(s, "2026-09-01", 50_000m, dueDate: "2026-09-10");
        await SaleAsync(s, "2026-09-15", 20_000m, dueDate: "2026-10-15");
        await SaleAsync(s, "2026-08-01", 30_000m, customer: s.Other, dueDate: "2026-09-01");
        var promisedItem = (await ItemsAsync(s, s.Other)).ShouldHaveSingleItem();
        await owner.PostAsync($"/api/v1/receivables/open-items/{promisedItem.GetProperty("id").GetGuid()}/promise", new { promisedOn = "2026-10-05", note = "Paying with the October run" }, HttpStatusCode.OK);

        // On 15 September only the old invoice has climbed: one letter at level 2, with its fee and the hold.
        var preview = await owner.GetOkAsync($"/api/v1/receivables/dunning/preview?companyId={s.CompanyId}&runDate=2026-09-15");
        var previewed = preview.GetProperty("letters").EnumerateArray().ShouldHaveSingleItem();
        previewed.GetProperty("level").GetInt32().ShouldBe(2);
        previewed.GetProperty("items").GetArrayLength().ShouldBe(1);
        previewed.GetProperty("overdueFc").GetDecimal().ShouldBe(100_000m);
        (await owner.PostErrorAsync("/api/v1/receivables/dunning/runs", new { companyId = s.CompanyId, runDate = "2026-12-01" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dunning.run_date_future");
        var run1 = await owner.PostAsync("/api/v1/receivables/dunning/runs", new { companyId = s.CompanyId, runDate = "2026-09-15" });
        run1.GetProperty("number").GetString().ShouldStartWith("DUN-2026-");
        run1.GetProperty("letters").GetInt32().ShouldBe(1);
        run1.GetProperty("feesFc").GetDecimal().ShouldBe(5_000m);
        var letter1 = run1.GetProperty("letterList").EnumerateArray().Single();
        letter1.GetProperty("number").GetString().ShouldBe(run1.GetProperty("number").GetString() + "-01");
        letter1.GetProperty("blockedCustomer").GetBoolean().ShouldBeTrue();
        letter1.GetProperty("feeItemId").ValueKind.ShouldBe(JsonValueKind.String);
        var items = await ItemsAsync(s);
        items.Single(i => i.GetProperty("originalTc").GetDecimal() == 100_000m).GetProperty("dunningLevel").GetInt32().ShouldBe(2);
        items.Single(i => i.GetProperty("originalTc").GetDecimal() == 50_000m).GetProperty("dunningLevel").GetInt32().ShouldBe(0);
        var fee = items.Single(i => i.GetProperty("documentType").GetString() == "ar_dunning_letter");
        (fee.GetProperty("kind").GetString(), fee.GetProperty("originalTc").GetDecimal(), fee.GetProperty("dueDate").GetString()).ShouldBe(("adjustment", 5_000m, "2026-09-15"));
        (await RoleAsync(s, "InterestIncome")).ShouldBe(-5_000m);
        (await ControlAsync(s, "2026-09-22")).ShouldBe(205_000m);
        var account = (await owner.GetOkAsync($"/api/v1/partners/{s.Customer}/customer-accounts")).EnumerateArray().Single(a => a.GetProperty("companyId").GetGuid() == s.CompanyId);
        account.GetProperty("creditStatus").GetString().ShouldBe("on_hold");
        account.GetProperty("dunningLevel").GetInt32().ShouldBe(2);
        await owner.AssertInvariantsAsync();

        // A week later the recent invoice and the fee itself reach the first rung: a new letter at the customer's level naming all three; the promised item stays out; then nothing more is due.
        var run2 = await owner.PostAsync("/api/v1/receivables/dunning/runs", new { companyId = s.CompanyId, runDate = "2026-09-22" });
        var letter2 = run2.GetProperty("letterList").EnumerateArray().Single();
        letter2.GetProperty("level").GetInt32().ShouldBe(2);
        letter2.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("level").GetInt32()).Order().ShouldBe([1, 1, 2]);
        letter2.GetProperty("overdueFc").GetDecimal().ShouldBe(155_000m);
        letter2.GetProperty("partnerId").GetGuid().ShouldBe(s.Customer);
        (await ItemsAsync(s)).Single(i => i.GetProperty("originalTc").GetDecimal() == 50_000m).GetProperty("dunningLevel").GetInt32().ShouldBe(1);
        (await owner.PostErrorAsync("/api/v1/receivables/dunning/runs", new { companyId = s.CompanyId, runDate = "2026-09-22" }, HttpStatusCode.Conflict)).Code.ShouldBe("dunning.nothing_due");
        (await owner.GetOkAsync($"/api/v1/receivables/dunning/runs?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/receivables/dunning/letters?companyId={s.CompanyId}&partnerId={s.Customer}")).GetArrayLength().ShouldBe(2);
        var sent = await owner.PostAsync($"/api/v1/receivables/dunning/letters/{letter2.GetProperty("id").GetGuid()}/sent", new { }, HttpStatusCode.OK);
        sent.GetProperty("sentAt").ValueKind.ShouldBe(JsonValueKind.String);
        var reader = await InviteAsync(s, "dun_reader", "receivables.dunning.read");
        (await reader.PostErrorAsync("/api/v1/receivables/dunning/runs", new { companyId = s.CompanyId, runDate = "2026-09-22" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_partner_who_is_customer_and_supplier_has_a_receivable_netted_against_a_payable_and_the_netting_reversed()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PutAsync($"/api/v1/partners/{s.Customer}", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true, isSupplier = true });
        await owner.PutAsync($"/api/v1/partners/{s.Customer}/supplier-accounts/{s.CompanyId}", new { currency = "IQD", leadTimeDays = 7 });

        // They owe 1 000 on a sale; the company owes them 600 on a service invoice.
        await SaleAsync(s, "2026-09-05", 1000m, dueDate: "2026-10-05");
        var receivable = (await ItemsAsync(s)).ShouldHaveSingleItem().GetProperty("id").GetGuid();
        var purchase = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, kind = "expense", supplierInvoiceNumber = "S-1", documentDate = "2026-09-10", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 600m, description = "Shelf fitting" } } });
        await owner.PostAsync($"/api/v1/purchasing/invoices/{purchase.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var payable = (await owner.PostAsync($"/api/v1/purchasing/invoices/{purchase.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await RoleAsync(s, "AP")).ShouldBe(-600m);

        // Netted for the smaller remainder: the payable is settled, 400 stays owed, both controls agree with their items.
        (await owner.PostErrorAsync("/api/v1/receivables/nettings", new { receivableItemId = receivable, payableItemId = payable, amount = 700m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("netting.amount_invalid");
        var netting = await owner.PostAsync("/api/v1/receivables/nettings", new { receivableItemId = receivable, payableItemId = payable, nettingDate = "2026-09-20", reason = "Agreed with the partner" });
        netting.GetProperty("number").GetString().ShouldStartWith("NET-2026-");
        netting.GetProperty("amountTc").GetDecimal().ShouldBe(600m);
        netting.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(0m);
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == receivable).GetProperty("remainingTc").GetDecimal().ShouldBe(400m);
        (await owner.GetOkAsync($"/api/v1/payables/open-items/{payable}")).GetProperty("item").GetProperty("status").GetString().ShouldBe("settled");
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await ControlAsync(s, "2026-09-20")).ShouldBe(400m);
        (await owner.GetOkAsync($"/api/v1/receivables/settlements?companyId={s.CompanyId}&openItemId={receivable}")).EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("netting");
        (await owner.GetOkAsync($"/api/v1/payables/settlements?companyId={s.CompanyId}&openItemId={payable}")).EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("netting");
        (await owner.GetOkAsync($"/api/v1/receivables/nettings?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(1);
        await owner.AssertInvariantsAsync();

        // Reversed: both invoices are open in full again.
        var reversed = await owner.PostAsync($"/api/v1/receivables/nettings/{netting.GetProperty("id").GetGuid()}/reverse", new { reason = "The partner paid instead", reversalDate = "2026-09-21" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await ItemsAsync(s)).Single(i => i.GetProperty("id").GetGuid() == receivable).GetProperty("remainingTc").GetDecimal().ShouldBe(1000m);
        (await owner.GetOkAsync($"/api/v1/payables/open-items/{payable}")).GetProperty("item").GetProperty("remainingTc").GetDecimal().ShouldBe(600m);
        (await RoleAsync(s, "AP")).ShouldBe(-600m);
        (await ControlAsync(s, "2026-09-21")).ShouldBe(1000m);
        await owner.AssertInvariantsAsync();
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }
}

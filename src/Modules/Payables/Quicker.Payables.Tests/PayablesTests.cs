using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    private async Task<HttpClient> InviteAsync(Setup s, string roleCode, params string[] grants)
    {
        var role = await s.Owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{s.Ws.Slug}@example.test";
        await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
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
    public async Task A_journal_on_the_payables_control_opens_the_suppliers_item_and_reversing_it_follows_while_documents_keep_their_own()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        object Line(string account, decimal debit = 0m, decimal credit = 0m, Guid? supplier = null, string? dueDate = null) =>
            new { accountCode = account, debit, credit, subledgerType = supplier is null ? null : "AP", subledgerRef = supplier, dueDate };
        async Task<JsonElement> JournalAsync(string date, params object[] lines) =>
            await owner.PostAsync($"/api/v1/accounting/companies/{s.CompanyId}/journals", new { kind = "manual", postingDate = date, currency = "IQD", description = Name("Rent", "إيجار"), lines });
        async Task<List<JsonElement>> ItemsAsync() =>
            (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).EnumerateArray().Select(static o => o.GetProperty("item")).ToList();

        // Only a supplier can be named on the payables control.
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("A customer", "زبون"), isCustomer = true })).GetProperty("id").GetGuid();
        foreach (var notASupplier in new[] { customer, Guid.NewGuid() })
        {
            var draft = await JournalAsync("2026-09-05", Line("6110", debit: 100m), Line("2110", credit: 100m, supplier: notASupplier));
            (await owner.PostErrorAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.supplier_required");
        }

        // Rent owed to the supplier: the credit opens an item owed to them, due when the line says.
        var rent = await JournalAsync("2026-09-05", Line("6110", debit: 1000m), Line("2110", credit: 1000m, supplier: s.Supplier, dueDate: "2026-10-15"));
        var rentPosted = await owner.PostAsync($"/api/v1/accounting/journals/{rent.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var rentEntry = rentPosted.GetProperty("journalEntryId").GetGuid();
        var owed = (await ItemsAsync()).ShouldHaveSingleItem();
        (owed.GetProperty("kind").GetString(), owed.GetProperty("documentType").GetString(), owed.GetProperty("originalTc").GetDecimal(), owed.GetProperty("dueDate").GetString()).ShouldBe(("adjustment", "journal_entry", 1000m, "2026-10-15"));
        await owner.AssertInvariantsAsync();

        // A credit from the supplier by journal (a debit on the control) is theirs to owe, and applies to the rent like any credit.
        var credit = await JournalAsync("2026-09-08", Line("2110", debit: 300m, supplier: s.Supplier), Line("6110", credit: 300m));
        await owner.PostAsync($"/api/v1/accounting/journals/{credit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var creditItem = (await ItemsAsync()).Single(i => i.GetProperty("originalTc").GetDecimal() == -300m);
        await owner.PostAsync("/api/v1/payables/settlements/apply", new { settlingItemId = creditItem.GetProperty("id").GetGuid(), settledItemId = owed.GetProperty("id").GetGuid(), amount = 300m }, HttpStatusCode.OK);
        (await ItemsAsync()).Single(i => i.GetProperty("id").GetGuid() == owed.GetProperty("id").GetGuid()).GetProperty("remainingTc").GetDecimal().ShouldBe(700m);
        await owner.AssertInvariantsAsync();

        // A settled item is not reversed from under its settlement: neither through the entry nor by correcting the journal.
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{rentEntry}/reverse", new { reason = "Wrong supplier" }, HttpStatusCode.Conflict)).Code.ShouldBe("payables.item_settled");

        // An unsettled one follows its journal's reversal.
        var fee = await JournalAsync("2026-09-10", Line("6110", debit: 200m), Line("2110", credit: 200m, supplier: s.Supplier));
        var feeEntry = (await owner.PostAsync($"/api/v1/accounting/journals/{fee.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("journalEntryId").GetGuid();
        await owner.PostAsync($"/api/v1/accounting/journal-entries/{feeEntry}/reverse", new { reason = "Billed twice" });
        (await ItemsAsync()).ShouldNotContain(i => i.GetProperty("originalTc").GetDecimal() == 200m);
        (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}&status=reversed")).EnumerateArray().ShouldHaveSingleItem().GetProperty("item").GetProperty("originalTc").GetDecimal().ShouldBe(200m);
        await owner.AssertInvariantsAsync();

        // A document's entry is reversed through the document, and no one posts in a document module's name.
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "E-1", documentDate = "2026-09-12", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 50m, description = "Courier" } } });
        await owner.PostAsync($"/api/v1/purchasing/invoices/{invoice.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        var invoiceEntry = (await owner.PostAsync($"/api/v1/purchasing/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("journalEntryId").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{invoiceEntry}/reverse", new { reason = "Undo" }, HttpStatusCode.Conflict)).Code.ShouldBe("journal_entry.owned_by_document");
        (await owner.PostErrorAsync("/api/v1/accounting/postings", new { companyId = s.CompanyId, sourceModule = "purchasing", sourceDocumentType = "purchase_invoice", sourceDocumentId = Guid.NewGuid(), postingDate = "2026-09-12", currency = "IQD", lines = new object[] { new { accountRole = "PurchaseExpense", amount = 10m }, new { accountRole = "AP", amount = -10m, subledgerType = "AP", subledgerRef = s.Supplier } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.source_module_reserved");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_supplier_statement_carries_the_balance_forward_lists_documents_and_reversals_and_ends_at_what_the_open_items_hold()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await InvoiceAsync(s, s.Supplier, "A-08", "2026-08-01", 300_000m);
        await InvoiceAsync(s, s.Supplier, "A-09", "2026-09-10", 120_000m);
        var note = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "debit_note", supplierInvoiceNumber = "CN-1", documentDate = "2026-09-12", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 20_000m, description = "Rent overcharged" } } });
        await owner.PostAsync($"/api/v1/purchasing/invoices/{note.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{note.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var wrong = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "A-WRONG", documentDate = "2026-09-15", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 50_000m, description = "Billed twice" } } });
        var wrongId = wrong.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/post", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/reverse", new { reason = "Billed twice" }, HttpStatusCode.OK);
        await InvoiceAsync(s, s.Other, "B-09", "2026-09-20", 80_000m);

        // September for Alpha: 300 000 owed from August; the September invoice, the debit note, the wrong invoice and its reversal.
        var statement = await owner.GetOkAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}&from=2026-09-01&to=2026-09-30");
        statement.GetProperty("partnerCode").GetString().ShouldBe("SUP-A");
        var iqd = statement.GetProperty("currencies").Only();
        (iqd.GetProperty("currency").GetString(), iqd.GetProperty("opening").GetDecimal(), iqd.GetProperty("increases").GetDecimal(), iqd.GetProperty("decreases").GetDecimal(), iqd.GetProperty("closing").GetDecimal()).ShouldBe(("IQD", 300_000m, 170_000m, 70_000m, 400_000m));
        var lines = iqd.GetProperty("lines").EnumerateArray().Select(static l => (l.GetProperty("kind").GetString(), l.GetProperty("reversal").GetBoolean(), l.GetProperty("amount").GetDecimal(), l.GetProperty("balance").GetDecimal())).ToList();
        lines.ShouldBe([("invoice", false, 120_000m, 420_000m), ("debit_note", false, -20_000m, 400_000m), ("invoice", false, 50_000m, 450_000m), ("invoice", true, -50_000m, 400_000m)]);
        iqd.GetProperty("lines")[0].GetProperty("supplierReference").GetString().ShouldBe("A-09");

        // The closing balance is what Alpha's open items still hold.
        var held = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).EnumerateArray()
            .Where(static i => i.GetProperty("item").GetProperty("status").GetString() != "reversed").Sum(static i => i.GetProperty("item").GetProperty("remainingTc").GetDecimal());
        held.ShouldBe(400_000m);

        // By default the current month (today is the 22nd); August alone shows only the first invoice.
        var current = await owner.GetOkAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}");
        (current.GetProperty("from").GetString(), current.GetProperty("to").GetString()).ShouldBe(("2026-09-01", "2026-09-22"));
        var august = (await owner.GetOkAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}&from=2026-08-01&to=2026-08-31")).GetProperty("currencies").Only();
        (august.GetProperty("opening").GetDecimal(), august.GetProperty("closing").GetDecimal(), august.GetProperty("lines").GetArrayLength()).ShouldBe((0m, 300_000m, 1));
        (await owner.GetOkAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}&from=2026-07-01&to=2026-07-31")).GetProperty("currencies").GetArrayLength().ShouldBe(0);
        (await owner.GetAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}&from=2026-09-30&to=2026-09-01")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Reading it takes the permission to read open items.
        var clerk = await InviteAsync(s, "clerk", "inventory.item.read");
        (await clerk.GetAsync($"/api/v1/payables/statement?companyId={s.CompanyId}&partnerId={s.Supplier}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task The_partners_360_view_shows_what_the_company_owes_it_and_its_latest_documents_to_those_who_may_read_payables()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await InvoiceAsync(s, s.Supplier, "A-08", "2026-08-01", 300_000m);
        await InvoiceAsync(s, s.Supplier, "A-09", "2026-09-30", 120_000m);
        var wrong = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "A-WRONG", documentDate = "2026-09-15", lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = 50_000m, description = "Billed twice" } } });
        var wrongId = wrong.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/submit", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/post", new { }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/purchasing/invoices/{wrongId}/reverse", new { reason = "Billed twice" }, HttpStatusCode.OK);

        // Alpha also buys from us: a customer account and an opportunity sit beside what we owe it.
        await owner.PutAsync($"/api/v1/partners/{s.Supplier}/customer-accounts/{s.CompanyId}", new { });
        await owner.PostAsync("/api/v1/partners/opportunities", new { companyId = s.CompanyId, partnerId = s.Supplier, title = "Office fit-out", expectedAmount = 900_000 });

        var view = await owner.GetOkAsync($"/api/v1/partners/{s.Supplier}/customer-360");
        view.GetProperty("partner").GetProperty("customerAccounts").Only().GetProperty("companyId").GetGuid().ShouldBe(s.CompanyId);
        view.GetProperty("pipeline").GetProperty("open").Only().GetProperty("title").GetString().ShouldBe("Office fit-out");
        var payables = view.GetProperty("panels").EnumerateArray().Single(static p => p.GetProperty("source").GetString() == "payables");
        var balance = payables.GetProperty("balances").Only();
        (balance.GetProperty("side").GetString(), balance.GetProperty("currency").GetString(), balance.GetProperty("open").GetDecimal(), balance.GetProperty("openFunctional").GetDecimal()).ShouldBe(("payable", "IQD", 420_000m, 420_000m));
        balance.GetProperty("overdue").GetDecimal().ShouldBe(300_000m, "today is the 22nd: August's invoice is past due, September's is not");
        balance.GetProperty("openItems").GetInt32().ShouldBe(2);
        balance.GetProperty("oldestDueOn").GetString().ShouldBe("2026-08-01");
        payables.GetProperty("documents").EnumerateArray().Select(static d => d.GetProperty("number").GetString()).ShouldNotContain(wrong.GetProperty("number").GetString());
        payables.GetProperty("documents").GetArrayLength().ShouldBe(2, "the reversed invoice is not among the latest documents");

        // A sales rep who may not read payables sees the customer side only.
        var rep = await InviteAsync(s, "rep", "partners.customer.read");
        var repView = await rep.GetOkAsync($"/api/v1/partners/{s.Supplier}/customer-360");
        repView.GetProperty("panels").GetArrayLength().ShouldBe(0);
        repView.GetProperty("partner").GetProperty("supplierAccounts").GetArrayLength().ShouldBe(0);
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

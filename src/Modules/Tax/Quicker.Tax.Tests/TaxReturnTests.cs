using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Tax.Tests;

/// <summary>
/// The return computed from the tax ledger (roadmap 5.3c, A-149): boxes by the codes' mapping, reconciled to the tax
/// accounts of the general ledger, drill-down to the entries behind a box, filing that locks the period under the
/// exclusive lock a posting only shares, and the generic UBL 2.1 export and clearance submission every tenant gets.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TaxReturnTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid RegimeId, Guid Supplier);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "RIYADH-IT", legalName = Name("Riyadh IT Supplies", "الرياض لتقنية المعلومات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "SAR" });
        return new Setup(ws, owner, companyId, regimeId, supplier);
    }

    /// <summary>An expense invoice with no item, on the default purchase row (SA-S, 15%): created and submitted, ready to post.</summary>
    private static async Task<Guid> DraftExpenseInvoiceAsync(HttpClient owner, Guid companyId, Guid supplierId, string date, decimal amount, string description)
    {
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId, partnerId = supplierId, kind = "expense", documentDate = date, postingDate = date, applyWht = false, lines = new[] { new { kind = "expense", description, quantity = 1m, unitPrice = amount } } });
        var id = invoice.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{id}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        return id;
    }

    /// <summary>Drafts, submits and posts an expense invoice, asserting it posts.</summary>
    private static async Task<JsonElement> PostExpenseInvoiceAsync(HttpClient owner, Guid companyId, Guid supplierId, string date, decimal amount, string description)
    {
        var id = await DraftExpenseInvoiceAsync(owner, companyId, supplierId, date, amount, description);
        return await owner.PostAsync($"/api/v1/purchasing/invoices/{id}/post", new { }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_return_totals_boxes_from_the_ledger_reconciled_to_the_journal_drills_down_and_files_the_period_locking_it()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Two invoices in September: 10,000 + 4,000 net at 15%, both SA-S, box 7 on both sides of the return.
        await PostExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-10", 10_000m, "Consulting");
        var second = await PostExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-20", 4_000m, "Software licences");

        var preview = await owner.GetOkAsync($"/api/v1/tax/returns/preview?companyId={s.CompanyId}&regimeId={s.RegimeId}&periodStart=2026-09-01&periodEnd=2026-09-30");
        preview.GetProperty("regimeCode").GetString().ShouldBe("SA-VAT");
        preview.GetProperty("currency").GetString().ShouldBe("SAR");
        var boxes = preview.GetProperty("boxes");
        boxes.GetArrayLength().ShouldBe(1);
        var box7 = boxes.Only();
        box7.GetProperty("code").GetString().ShouldBe("7");
        box7.GetProperty("baseAmount").GetDecimal().ShouldBe(14_000m);
        box7.GetProperty("taxAmount").GetDecimal().ShouldBe(2_100m);
        preview.GetProperty("reconciled").GetBoolean().ShouldBeTrue();
        var reconciliation = preview.GetProperty("reconciliation").Only();
        reconciliation.GetProperty("taxCode").GetString().ShouldBe("SA-S");
        reconciliation.GetProperty("accountRole").GetString().ShouldBe("InputTax");
        reconciliation.GetProperty("ledgerMovement").GetDecimal().ShouldBe(2_100m);
        reconciliation.GetProperty("entriesMovement").GetDecimal().ShouldBe(2_100m);
        reconciliation.GetProperty("difference").GetDecimal().ShouldBe(0m);
        preview.GetProperty("netPayable").GetDecimal().ShouldBe(-2_100m, "only input tax was recovered, so the company is due a refund");

        // The drill-down of box 7's tax column names both invoices.
        var drill = await owner.GetOkAsync($"/api/v1/tax/returns/drilldown?companyId={s.CompanyId}&regimeId={s.RegimeId}&periodStart=2026-09-01&periodEnd=2026-09-30&box=7");
        drill.GetArrayLength().ShouldBe(4, "each invoice contributes a base line and a tax line");
        drill.EnumerateArray().Count(static l => l.GetProperty("kind").GetString() == "tax").ShouldBe(2);
        drill.EnumerateArray().Select(static l => l.GetProperty("sourceDocumentNumber").GetString()).ShouldContain(second.GetProperty("number").GetString());

        // A quiet period previews with nothing to report.
        var octoberPreview = await owner.GetOkAsync($"/api/v1/tax/returns/preview?companyId={s.CompanyId}&regimeId={s.RegimeId}&periodStart=2026-10-01&periodEnd=2026-10-31");
        octoberPreview.GetProperty("boxes").GetArrayLength().ShouldBe(0);
        octoberPreview.GetProperty("netPayable").GetDecimal().ShouldBe(0m);

        // Filing books the figures and lists the period; re-filing the same range is refused, and so is a posting into it.
        var filed = await owner.PostAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30", reference = "ZATCA-2026-09" }, HttpStatusCode.OK);
        filed.GetProperty("status").GetString().ShouldBe("filed");
        filed.GetProperty("netPayable").GetDecimal().ShouldBe(-2_100m);
        var listed = (await owner.GetOkAsync($"/api/v1/tax/returns?companyId={s.CompanyId}&regimeId={s.RegimeId}")).Only();
        listed.GetProperty("status").GetString().ShouldBe("filed");
        listed.GetProperty("reference").GetString().ShouldBe("ZATCA-2026-09");
        listed.GetProperty("netPayable").GetDecimal().ShouldBe(-2_100m);

        (await owner.PostErrorAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30" }, HttpStatusCode.Conflict)).Code.ShouldBe("tax.period_overlap");

        var lateId = await DraftExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-25", 500m, "Late bill");
        (await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{lateId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("tax.period_filed");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Filing_takes_the_exclusive_lock_a_posting_only_shares_so_the_two_never_tear()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Building and submitting an invoice touches no tax lock; only posting it does. Racing that final step
        // against filing an otherwise-empty September proves the database serialises them: whichever wins the row
        // lock first, the outcome is never torn — a posting that succeeded is inside what got filed, and one that
        // was refused (tax.period_filed) never touched the ledger.
        var invoiceId = await DraftExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-15", 1_000m, "Raced invoice");
        var filing = owner.PostAsJsonAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30", reference = "RACE" }, Json);
        var posting = owner.PostAsJsonAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, Json);
        await Task.WhenAll(filing, posting);

        var filingResponse = await filing;
        var postingResponse = await posting;
        filingResponse.StatusCode.ShouldBe(HttpStatusCode.OK, (await filingResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        postingResponse.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.Conflict]);

        var listed = (await owner.GetOkAsync($"/api/v1/tax/returns?companyId={s.CompanyId}&regimeId={s.RegimeId}")).Only();
        var netPayable = listed.GetProperty("netPayable").GetDecimal();

        if (postingResponse.StatusCode == HttpStatusCode.OK)
        {
            // The posting won the race: it is part of what was filed.
            netPayable.ShouldBe(-150m);
        }
        else
        {
            // Filing won the race: the posting found the period already filed and touched nothing.
            (await postingResponse.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("tax.period_filed");
            netPayable.ShouldBe(0m);
        }
    }

    [Fact]
    public async Task Filing_is_refused_when_the_ledger_and_the_tax_accounts_disagree()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var posted = await PostExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-10", 10_000m, "Consulting");
        var standard = posted.GetProperty("lines").Only().GetProperty("taxCodeId").GetGuid();

        // A row lands on the input tax account outside the tax module (a manual correction, a bug): the books and the
        // tax ledger now disagree, and filing refuses rather than book a return the accounts do not support.
        await using (var connection = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var line = await connection.QuerySingleAsync<(Guid EntryId, Guid AccountId)>(
                "SELECT entry_id, account_id FROM app.gl_journal_lines WHERE tenant_id = @t AND company_id = @c AND account_role = 'InputTax' LIMIT 1",
                new { t = s.Ws.TenantId, c = s.CompanyId });
            await connection.ExecuteAsync("""
                INSERT INTO app.gl_journal_lines (tenant_id, id, entry_id, company_id, posting_date, line_no, account_id, account_role, debit_tc, currency_tc, rate_tc_fc, debit_fc, rate_date, tax_code_id)
                VALUES (@t, @id, @entry, @c, DATE '2026-09-12', 99, @account, 'InputTax', 1, 'SAR', 1, 1, DATE '2026-09-12', @code)
                """, new { t = s.Ws.TenantId, id = Guid.CreateVersion7(), entry = line.EntryId, c = s.CompanyId, account = line.AccountId, code = standard });
        }

        var preview = await owner.GetOkAsync($"/api/v1/tax/returns/preview?companyId={s.CompanyId}&regimeId={s.RegimeId}&periodStart=2026-09-01&periodEnd=2026-09-30");
        preview.GetProperty("reconciled").GetBoolean().ShouldBeFalse();
        preview.GetProperty("reconciliation").Only().GetProperty("difference").GetDecimal().ShouldBe(1m);

        var (code, problem) = await owner.PostErrorAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30" }, HttpStatusCode.Conflict);
        code.ShouldBe("tax.return_not_reconciled");
        problem.GetProperty("why").GetProperty("reconciliation").Only().GetProperty("difference").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task The_generic_UBL_export_carries_the_header_parties_and_tax_summary_and_clearance_is_wired_without_an_adapter()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var posted = await PostExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-10", 10_000m, "Consulting");
        var number = posted.GetProperty("number").GetString();
        var id = posted.GetProperty("id").GetGuid();

        var response = await owner.GetAsync(new Uri($"/api/v1/tax/documents/purchase_invoice/{id}/ubl", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        XNamespace inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        xml.Root!.Name.ShouldBe(inv + "Invoice");
        xml.Root.Element(cbc + "ID")!.Value.ShouldBe(number);
        xml.Root.Element(cbc + "InvoiceTypeCode")!.Value.ShouldBe("380");
        xml.Root.Element(cbc + "DocumentCurrencyCode")!.Value.ShouldBe("SAR");
        var subtotal = xml.Root.Element(cac + "TaxTotal")!.Element(cac + "TaxSubtotal")!;
        subtotal.Element(cbc + "TaxableAmount")!.Value.ShouldBe("10000.00");
        subtotal.Element(cbc + "TaxAmount")!.Value.ShouldBe("1500.00");
        subtotal.Element(cac + "TaxCategory")!.Element(cbc + "ID")!.Value.ShouldBe("SA-S");
        subtotal.Element(cac + "TaxCategory")!.Element(cbc + "Percent")!.Value.ShouldBe("15");
        xml.Root.Element(cac + "LegalMonetaryTotal")!.Element(cbc + "PayableAmount")!.Value.ShouldBe("11500.00");

        // A document with nothing in the tax ledger has nothing to export.
        (await owner.PostErrorAsync("/api/v1/tax/documents/purchase_invoice/00000000-0000-0000-0000-000000000000/clearance/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax.document_not_taxed");

        // Saudi Arabia's regime names a scheme (ZATCA), but no adapter is registered yet: submission is refused
        // honestly rather than pretending to clear it.
        (await owner.PostErrorAsync($"/api/v1/tax/documents/purchase_invoice/{id}/clearance/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax.clearance_not_configured");

        // A regime that names no scheme needs no clearance at all.
        var uaeCompany = (await owner.PostAsync("/api/v1/organization/companies", new { code = "DXB", legalName = Name("Dubai Trading", "دبي للتجارة"), country = "AE", functionalCurrency = "AED", timeZone = "Asia/Dubai" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "DXBMAIN", companyId = uaeCompany });
        var uae = await owner.PostAsync("/api/v1/tax/templates/AE-VAT/install", new { });
        var uaeRegimeId = uae.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = uaeCompany, regimeId = uaeRegimeId, registrationNumber = "100123456700003", registeredFrom = "2018-01-01" });
        var uaeSupplier = (await owner.PostAsync("/api/v1/partners", new { code = "DXB-SUP", legalName = Name("Dubai Supplies", "دبي للتوريدات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{uaeSupplier}/supplier-accounts/{uaeCompany}", new { currency = "AED" });
        var uaeInvoice = await PostExpenseInvoiceAsync(owner, uaeCompany, uaeSupplier, "2026-09-10", 1_000m, "Office supplies");
        (await owner.PostErrorAsync($"/api/v1/tax/documents/purchase_invoice/{uaeInvoice.GetProperty("id").GetGuid()}/clearance/submit", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax.clearance_not_required");
    }

    [Fact]
    public async Task A_member_with_return_read_previews_and_drills_down_but_only_return_file_may_file()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await PostExpenseInvoiceAsync(owner, s.CompanyId, s.Supplier, "2026-09-10", 10_000m, "Consulting");

        var role = (await owner.PostAsync("/api/v1/roles", new { code = "tax_return_reader", name = Name("Tax return reader", "قارئ الإقرار الضريبي"), description = "", grants = new[] { TaxPermissions.ReturnRead } })).GetProperty("id").GetGuid();
        var email = $"return-{s.Ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Return reader", roleIds = Array.Empty<Guid>() }, HttpStatusCode.Created);
        (await owner.PostAsJsonAsync($"/api/v1/users/{invited.GetProperty("membershipId").GetGuid()}/assignments", new { roleId = role, scopes = new[] { new { scopeType = "company", scopeId = s.CompanyId } } }, Json)).EnsureSuccessStatusCode();
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        var reader = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        (await reader.GetOkAsync($"/api/v1/tax/returns/preview?companyId={s.CompanyId}&regimeId={s.RegimeId}&periodStart=2026-09-01&periodEnd=2026-09-30")).GetProperty("boxes").GetArrayLength().ShouldBe(1);
        (await reader.PostErrorAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();

        var filed = await owner.PostAsync("/api/v1/tax/returns/file", new { companyId = s.CompanyId, regimeId = s.RegimeId, periodStart = "2026-09-01", periodEnd = "2026-09-30" }, HttpStatusCode.OK);
        filed.GetProperty("status").GetString().ShouldBe("filed");
    }
}

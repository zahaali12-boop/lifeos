using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Quicker.Identity.TestSupport;
using Quicker.Web.Exports;

namespace Quicker.Onboarding.Tests;

/// <summary>
/// The setup wizard's back end (A-173): the status counts what exists; opening balances from a sheet post one opening
/// journal whose control-account lines open the customers' and suppliers' items and whose difference goes to opening
/// balance equity; a dry run checks the whole file and keeps nothing; wrong rows are named.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SetupTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static StringContent CsvOf(string csv) => new(csv, Encoding.UTF8, "text/csv");

    [Fact]
    public async Task Status_counts_what_exists_and_opening_balances_post_an_opening_journal_with_open_items()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        // A fresh tenant: nothing yet.
        var empty = await owner.GetOkAsync("/api/v1/onboarding/status");
        empty.GetProperty("companies").GetInt32().ShouldBe(0);
        empty.GetProperty("hasChart").GetBoolean().ShouldBeFalse();
        empty.GetProperty("customers").GetInt32().ShouldBe(0);
        empty.GetProperty("companyId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "GO", legalName = Name("Go Live Co", "شركة الانطلاق"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        var withCompany = await owner.GetOkAsync($"/api/v1/onboarding/status?companyId={companyId}");
        withCompany.GetProperty("companies").GetInt32().ShouldBe(1);
        withCompany.GetProperty("hasChart").GetBoolean().ShouldBeFalse();

        // Balances before the chart exists are refused with a clear reason.
        var noChart = await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n1111,100,\n"), HttpStatusCode.UnprocessableEntity);
        noChart.GetProperty("code").GetString().ShouldBe("opening.chart_required");

        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var cashBox = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Main cash box", "الصندوق الرئيسي"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        await owner.PostContentAsync($"/api/v1/partners/import?companyId={companyId}", CsvOf("code,name_en,name_ar,customer,supplier\nCUS-A,Alpha,ألفا,yes,no\nSUP-B,Beta,بيتا,no,yes\n"));
        var ready = await owner.GetOkAsync($"/api/v1/onboarding/status?companyId={companyId}");
        ready.GetProperty("hasChart").GetBoolean().ShouldBeTrue();
        ready.GetProperty("customers").GetInt32().ShouldBe(1);
        ready.GetProperty("suppliers").GetInt32().ShouldBe(1);
        ready.GetProperty("openingJournals").GetInt32().ShouldBe(0);

        // The sheet: cash, a customer's balance, a supplier's balance and capital; 300,000 short, which opening balance equity absorbs.
        const string sheet = "account,debit,credit,partner,bank_account,due_date,description\n"
            + "1111,1000000,,,CASH,,Cash count\n"
            + "1210,500000,,CUS-A,,2026-10-15,Invoice 2026-114\n"
            + "2110,,200000,SUP-B,,2026-10-20,\n"
            + "3100,,1000000,,,,\n";
        var dry = await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30&dryRun=true", CsvOf(sheet));
        dry.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        dry.GetProperty("journalId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        dry.GetProperty("debit").GetDecimal().ShouldBe(1_500_000m);
        dry.GetProperty("credit").GetDecimal().ShouldBe(1_200_000m);
        dry.GetProperty("difference").GetDecimal().ShouldBe(300_000m);
        dry.GetProperty("lines").GetInt32().ShouldBe(4);
        dry.GetProperty("partnerLines").GetInt32().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/onboarding/status?companyId={companyId}")).GetProperty("openingJournals").GetInt32().ShouldBe(0, "a dry run keeps nothing");
        (await owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={companyId}")).GetArrayLength().ShouldBe(0);

        var posted = await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf(sheet));
        posted.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        posted.GetProperty("number").GetString().ShouldStartWith("MJ-");
        posted.GetProperty("difference").GetDecimal().ShouldBe(300_000m);
        var journalId = posted.GetProperty("journalId").GetGuid();
        (await owner.GetOkAsync($"/api/v1/onboarding/status?companyId={companyId}")).GetProperty("openingJournals").GetInt32().ShouldBe(1);

        // The journal is posted as an opening entry with the equity plug, and the customer's and supplier's items are open.
        var journal = await owner.GetOkAsync($"/api/v1/accounting/journals/{journalId}");
        journal.GetProperty("kind").GetString().ShouldBe("opening");
        journal.GetProperty("status").GetString().ShouldBe("posted");
        var receivables = await owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={companyId}");
        receivables.GetArrayLength().ShouldBe(1);
        var item = receivables[0];
        item.GetProperty("partnerCode").GetString().ShouldBe("CUS-A");
        item.GetProperty("item").GetProperty("kind").GetString().ShouldBe("opening");
        item.GetProperty("item").GetProperty("dueDate").GetString().ShouldBe("2026-10-15");
        var payables = await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={companyId}");
        payables.GetArrayLength().ShouldBe(1);
        var trial = await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/reports/trial-balance?asOf=2026-09-30");
        var equity = trial.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("accountCode").GetString() == "3400");
        (equity.GetProperty("credit").GetDecimal() - equity.GetProperty("debit").GetDecimal()).ShouldBe(300_000m, "opening balance equity carries what the sheet did not balance");

        // The cash counted is in the cash box itself, not only on its ledger account (A-194).
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cashBox}")).GetProperty("balanceTc").GetDecimal().ShouldBe(1_000_000m);
        var cashRow = trial.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("accountCode").GetString() == "1111");
        (cashRow.GetProperty("debit").GetDecimal() - cashRow.GetProperty("credit").GetDecimal()).ShouldBe(1_000_000m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Wrong_rows_refuse_the_file_and_an_excel_workbook_reads_like_the_csv()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "XL", legalName = Name("Excel Co", "شركة إكسل"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "TILL", name = Name("Till", "الصندوق"), kind = "cash", currency = "IQD" });
        await owner.PostContentAsync($"/api/v1/partners/import?companyId={companyId}", CsvOf("code,name_en,name_ar,customer,supplier\nCUS-X,Xi,كسي,yes,no\n"));

        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances", CsvOf("account,debit,credit\n1111,1,\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("opening.as_of_required");
        var unknownPartner = await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit,partner\n1210,100,,NOBODY\n"), HttpStatusCode.UnprocessableEntity);
        unknownPartner.GetProperty("code").GetString().ShouldBe("import.partner_unknown");
        unknownPartner.GetProperty("why").GetProperty("row").GetInt32().ShouldBe(1);
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n1210,100,\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("opening.partner_required");
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n1111,100,\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("opening.bank_account_required");
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit,bank_account\n1111,100,,NOPE\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("import.bank_account_unknown");
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n9999,100,\n"), HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("account.not_found");
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n1111,abc,\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("import.number_invalid");
        (await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", CsvOf("account,debit,credit\n1111,100,100\n"), HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("import.amount_sides");

        // An Excel workbook with a real date cell and number cells reads the same way as the CSV.
        var columns = new List<ExportColumn> { new("account"), new("debit", ExportCellType.Number), new("credit", ExportCellType.Number), new("partner"), new("bank_account"), new("due_date", ExportCellType.Date), new("description") };
        var workbook = TabularExport.Xlsx("Opening", columns,
        [
            new object?[] { "1111", 750000m, null, null, "TILL", null, "Cash" },
            new object?[] { "1210", 250000m, null, "CUS-X", null, new DateOnly(2026, 11, 1), "Open invoice" },
            new object?[] { "3100", null, 1000000m, null, null, null, null },
        ]);
        using var upload = new ByteArrayContent(workbook);
        upload.Headers.ContentType = new MediaTypeHeaderValue(TabularExport.XlsxContentType);
        var posted = await owner.PostContentAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", upload);
        posted.GetProperty("difference").GetDecimal().ShouldBe(0m);
        posted.GetProperty("lines").GetInt32().ShouldBe(3);
        var receivables = await owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={companyId}");
        receivables.GetArrayLength().ShouldBe(1);
        receivables[0].GetProperty("item").GetProperty("dueDate").GetString().ShouldBe("2026-11-01");
    }
}

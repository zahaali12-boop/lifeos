using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The cash and bank screen (trader edition, ADR-0035 §18): each box and account over a period, what it held when the
/// period began, what came in and went out, what it held at the end, and every movement newest first with the balance
/// after it, all from the bank subledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CashReportTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task Each_box_shows_what_came_in_went_out_and_the_balance_after_every_movement()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRC", legalName = Name("Cash Trading", "تجارة النقد"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD", isActive = true })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "BANK", name = Name("Rafidain account", "حساب الرافدين"), kind = "bank", currency = "IQD", isActive = true })).GetProperty("id").GetGuid();
        var kinds = await owner.PostAsync("/api/v1/expenses/categories/defaults", new { }, HttpStatusCode.OK);
        var rent = kinds.EnumerateArray().Single(static k => k.GetProperty("code").GetString() == "RENT").GetProperty("id").GetGuid();

        // The owner put a million in the box on the 1st; the rent of 200,000 left it on the 10th.
        await owner.PostAsync("/api/v1/trader/owner-money", new { companyId, kind = "in", bankAccountId = cash, amount = 1000000m, date = "2026-09-01", note = "Capital" }, HttpStatusCode.OK);
        await owner.PostAsync("/api/v1/expenses/quick", new { companyId, categoryId = rent, amount = 200000m, expenseDate = "2026-09-10", paidFromBankAccountId = cash, description = "September rent" });

        // September: the bank account is empty; the box opened at nothing, took a million, gave 200,000, holds 800,000.
        var report = await owner.GetOkAsync($"/api/v1/trader/cash?companyId={companyId}&from=2026-09-01&to=2026-09-30");
        report.GetProperty("currency").GetString().ShouldBe("IQD");
        report.GetProperty("from").GetString().ShouldBe("2026-09-01");
        report.GetProperty("to").GetString().ShouldBe("2026-09-30");
        var accounts = report.GetProperty("accounts").EnumerateArray().ToList();
        accounts.Select(static a => a.GetProperty("code").GetString()).ShouldBe(["BANK", "CASH"]);
        var bankRow = accounts[0];
        bankRow.GetProperty("balanceNow").GetDecimal().ShouldBe(0m);
        bankRow.GetProperty("movements").GetArrayLength().ShouldBe(0);
        var box = accounts[1];
        box.GetProperty("kind").GetString().ShouldBe("cash");
        box.GetProperty("balanceNow").GetDecimal().ShouldBe(800000m);
        box.GetProperty("opening").GetDecimal().ShouldBe(0m);
        box.GetProperty("in").GetDecimal().ShouldBe(1000000m);
        box.GetProperty("out").GetDecimal().ShouldBe(200000m);
        box.GetProperty("closing").GetDecimal().ShouldBe(800000m);
        var movements = box.GetProperty("movements").EnumerateArray().ToList();
        movements.Count.ShouldBe(2);
        movements[0].GetProperty("date").GetString().ShouldBe("2026-09-10");
        movements[0].GetProperty("out").GetDecimal().ShouldBe(200000m);
        movements[0].GetProperty("in").GetDecimal().ShouldBe(0m);
        movements[0].GetProperty("balanceAfter").GetDecimal().ShouldBe(800000m);
        movements[1].GetProperty("date").GetString().ShouldBe("2026-09-01");
        movements[1].GetProperty("in").GetDecimal().ShouldBe(1000000m);
        movements[1].GetProperty("balanceAfter").GetDecimal().ShouldBe(1000000m);
        movements[1].GetProperty("sourceDocumentType").GetString().ShouldBe("journal_entry");

        // From the 5th: the box opened with the million already in it and only the rent moved; one account when one is named.
        var later = await owner.GetOkAsync($"/api/v1/trader/cash?companyId={companyId}&from=2026-09-05&to=2026-09-30&bankAccountId={cash}");
        var onlyBox = later.GetProperty("accounts").EnumerateArray().Single();
        onlyBox.GetProperty("opening").GetDecimal().ShouldBe(1000000m);
        onlyBox.GetProperty("in").GetDecimal().ShouldBe(0m);
        onlyBox.GetProperty("out").GetDecimal().ShouldBe(200000m);
        onlyBox.GetProperty("closing").GetDecimal().ShouldBe(800000m);
        onlyBox.GetProperty("movements").GetArrayLength().ShouldBe(1);

        // No period at all: everything to today, the opening being nothing.
        var all = await owner.GetOkAsync($"/api/v1/trader/cash?companyId={companyId}&bankAccountId={cash}");
        all.GetProperty("from").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        all.GetProperty("to").GetString().ShouldBe("2026-09-22");
        all.GetProperty("accounts")[0].GetProperty("closing").GetDecimal().ShouldBe(800000m);

        // A period that ends before it begins is refused.
        (await owner.GetAsync($"/api/v1/trader/cash?companyId={companyId}&from=2026-09-30&to=2026-09-01")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        _ = bank;
        await owner.AssertInvariantsAsync();
    }
}

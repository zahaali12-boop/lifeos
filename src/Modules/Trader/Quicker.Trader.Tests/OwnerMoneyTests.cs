using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The owner's money from the trader's screens (ADR-0035 §12, A-195): money put in lands in the cash box and on the
/// capital account, money taken out leaves the box against drawings, each as a posted manual journal the box's balance
/// and the health screen follow; the movements read back with their notes; a wrong kind, a zero amount, a box in
/// another currency and an equity account the chart lacks are refused; the harness passes.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OwnerMoneyTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private async Task<decimal> AccountAsync(Guid tenantId, Guid companyId, string code)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l JOIN app.gl_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.account_id WHERE l.tenant_id = @t AND l.company_id = @c AND a.code = @code", new { t = tenantId, c = companyId, code });
    }

    [Fact]
    public async Task Money_the_owner_puts_in_or_takes_out_moves_the_box_and_the_equity_accounts_as_a_journal()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "OWN", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var dollars = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "USDB", name = Name("Dollar account", "حساب الدولار"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();

        var nothing = await owner.GetOkAsync($"/api/v1/trader/owner-money?companyId={companyId}");
        nothing.GetProperty("putIn").GetDecimal().ShouldBe(0m);
        nothing.GetProperty("capitalAccount").GetString().ShouldBe("3100");
        nothing.GetProperty("drawingsAccount").GetString().ShouldBe("3600");
        nothing.GetProperty("movements").GetArrayLength().ShouldBe(0);

        // The owner starts the business with 5,000,000 in the cash box.
        var started = await owner.PostAsync("/api/v1/trader/owner-money", new { companyId, kind = "in", bankAccountId = cash, amount = 5000000m, date = "2026-09-01" }, HttpStatusCode.OK);
        started.GetProperty("putIn").GetDecimal().ShouldBe(5000000m);
        started.GetProperty("takenOut").GetDecimal().ShouldBe(0m);
        var first = started.GetProperty("movements").EnumerateArray().Single();
        first.GetProperty("kind").GetString().ShouldBe("in");
        first.GetProperty("number").GetString()!.ShouldStartWith("MJ-");
        first.GetProperty("bankAccountCode").GetString().ShouldBe("CASH");
        first.GetProperty("amount").GetDecimal().ShouldBe(5000000m);
        first.GetProperty("note").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        first.GetProperty("status").GetString().ShouldBe("posted");
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(5000000m);
        (await AccountAsync(ws.TenantId, companyId, "3100")).ShouldBe(-5000000m, "capital is a credit");

        // Two weeks later the owner takes 200,000 for the family.
        var taken = await owner.PostAsync("/api/v1/trader/owner-money", new { companyId, kind = "out", bankAccountId = cash, amount = 200000m, date = "2026-09-15", note = "School fees" }, HttpStatusCode.OK);
        taken.GetProperty("takenOut").GetDecimal().ShouldBe(200000m);
        var movements = taken.GetProperty("movements").EnumerateArray().ToList();
        movements.Count.ShouldBe(2);
        movements[0].GetProperty("kind").GetString().ShouldBe("out");
        movements[0].GetProperty("note").GetString().ShouldBe("School fees");
        movements[0].GetProperty("date").GetString().ShouldBe("2026-09-15");
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(4800000m);
        (await AccountAsync(ws.TenantId, companyId, "3600")).ShouldBe(200000m, "drawings are a debit");
        (await AccountAsync(ws.TenantId, companyId, "1111")).ShouldBe(4800000m);

        // The journal is a manual journal of the full edition, and the health screen's cash follows.
        var journal = await owner.GetOkAsync($"/api/v1/accounting/journals/{movements[0].GetProperty("journalId").GetGuid()}");
        journal.GetProperty("kind").GetString().ShouldBe("manual");
        journal.GetProperty("reference").GetString().ShouldBe("OWNER-OUT");
        journal.GetProperty("status").GetString().ShouldBe("posted");
        (await owner.GetOkAsync($"/api/v1/trader/health?companyId={companyId}")).GetProperty("viability").GetProperty("cash").GetDecimal().ShouldBe(4800000m);

        // Refused: a kind that is neither, nothing, a dollar account for dinars, and an equity account the chart does not have.
        (await owner.PostErrorAsync("/api/v1/trader/owner-money", new { companyId, kind = "loan", bankAccountId = cash, amount = 1m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("owner.kind_invalid");
        (await owner.PostErrorAsync("/api/v1/trader/owner-money", new { companyId, kind = "in", bankAccountId = cash, amount = 0m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("owner.amount_invalid");
        (await owner.PostErrorAsync("/api/v1/trader/owner-money", new { companyId, kind = "in", bankAccountId = dollars, amount = 100m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("owner.box_currency");
        await owner.PutAsync("/api/v1/organization/settings/trader.owner.drawings_account", new { value = "9999", valueType = "string" });
        var missing = await owner.PostErrorAsync("/api/v1/trader/owner-money", new { companyId, kind = "out", bankAccountId = cash, amount = 100m }, HttpStatusCode.UnprocessableEntity);
        missing.Code.ShouldBe("owner.account_missing");
        (await owner.GetOkAsync($"/api/v1/trader/owner-money?companyId={companyId}")).GetProperty("drawingsAccount").GetString().ShouldBe("9999");
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(4800000m, "nothing was written");
        await owner.AssertInvariantsAsync();
    }
}

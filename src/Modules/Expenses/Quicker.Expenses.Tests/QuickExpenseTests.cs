using System.Net;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Expenses.Tests;

/// <summary>
/// An expense in one step (ADR-0035 §12, A-195): the usual kinds of spend made once; the rent paid out of the cash box
/// (a petty cash voucher from the fund made on the box), the electricity paid to its company from the bank (its bill,
/// paid the same day), the phone bill left owed (its bill, in what the shop owes); a payee is asked for where the money
/// did not leave a cash box; an account in another currency is refused; every box and account ties to the ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuickExpenseTests(ApiHostFixture host)
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
    public async Task An_expense_in_one_step_is_paid_from_the_box_paid_from_the_bank_or_left_owed()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "SHOP", legalName = Name("Parts Trading", "تجارة القطع"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "BANK", name = Name("Rafidain", "الرافدين"), kind = "bank", currency = "IQD" })).GetProperty("id").GetGuid();
        var dollars = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "USDB", name = Name("Dollar account", "حساب الدولار"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();
        var electricity = (await owner.PostAsync("/api/v1/partners", new { code = "ELEC", legalName = Name("Electricity office", "دائرة الكهرباء"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{electricity}/supplier-accounts/{companyId}", new { currency = "IQD" });

        // The usual kinds of spend, made once: a second call adds nothing.
        var kinds = await owner.PostAsync("/api/v1/expenses/categories/defaults", new { }, HttpStatusCode.OK);
        kinds.GetArrayLength().ShouldBe(10);
        var rent = kinds.EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "RENT");
        rent.GetProperty("accountCode").GetString().ShouldBe("6110");
        var again = await owner.PostAsync("/api/v1/expenses/categories/defaults", new { }, HttpStatusCode.OK);
        again.GetArrayLength().ShouldBe(10);
        again.EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "RENT").GetProperty("id").GetGuid().ShouldBe(rent.GetProperty("id").GetGuid());
        Guid Kind(string code) => kinds.EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        // The rent, 300,000 out of the cash box: a petty cash voucher from the fund made on the box.
        var rentPaid = await owner.PostAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("RENT"), amount = 300000m, expenseDate = "2026-09-05", paidFromBankAccountId = cash, description = "September rent" });
        rentPaid.GetProperty("outcome").GetString().ShouldBe("paid");
        rentPaid.GetProperty("paymentNumber").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        var voucher = rentPaid.GetProperty("expense");
        voucher.GetProperty("kind").GetString().ShouldBe("petty_cash");
        voucher.GetProperty("status").GetString().ShouldBe("confirmed");
        voucher.GetProperty("fundCode").GetString().ShouldBe("CASH");
        voucher.GetProperty("totalGross").GetDecimal().ShouldBe(300000m);
        var funds = await owner.GetOkAsync($"/api/v1/expenses/funds?companyId={companyId}");
        funds.GetArrayLength().ShouldBe(1);
        funds[0].GetProperty("bankAccountCode").GetString().ShouldBe("CASH");
        funds[0].GetProperty("balance").GetDecimal().ShouldBe(-300000m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-300000m);
        (await AccountAsync(ws.TenantId, companyId, "6110")).ShouldBe(300000m);

        // Another one out of the same box reuses the fund.
        await owner.PostAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("TRANSPORT"), amount = 25000m, expenseDate = "2026-09-06", paidFromBankAccountId = cash });
        (await owner.GetOkAsync($"/api/v1/expenses/funds?companyId={companyId}")).GetArrayLength().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-325000m);

        // The electricity from the bank needs to say who was paid; then it is the office's bill, paid the same day.
        (await owner.PostErrorAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("UTILITIES"), amount = 80000m, paidFromBankAccountId = bank }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.quick_payee_required");
        var electricityPaid = await owner.PostAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("UTILITIES"), amount = 80000m, expenseDate = "2026-09-07", paidFromBankAccountId = bank, partnerId = electricity, reference = "ELEC-0926" });
        electricityPaid.GetProperty("outcome").GetString().ShouldBe("paid");
        electricityPaid.GetProperty("paymentNumber").GetString()!.ShouldStartWith("PAY-");
        var bill = electricityPaid.GetProperty("expense");
        bill.GetProperty("kind").GetString().ShouldBe("supplier");
        bill.GetProperty("paymentStatus").GetString().ShouldBe("paid");
        bill.GetProperty("remainingTc").GetDecimal().ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-80000m);
        (await AccountAsync(ws.TenantId, companyId, "6120")).ShouldBe(80000m);

        // The phone bill, not paid yet: owed to the office until Money pays it.
        (await owner.PostErrorAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("PHONE"), amount = 45000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.quick_payee_required");
        var owed = await owner.PostAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("PHONE"), amount = 45000m, expenseDate = "2026-09-08", partnerId = electricity, dueDate = "2026-09-30" });
        owed.GetProperty("outcome").GetString().ShouldBe("owed");
        owed.GetProperty("expense").GetProperty("paymentStatus").GetString().ShouldBe("unpaid");
        owed.GetProperty("expense").GetProperty("dueDate").GetString().ShouldBe("2026-09-30");
        var aging = await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={companyId}");
        aging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(45000m);

        // Refused: a dollar account, a zero amount, a kind that is not there.
        (await owner.PostErrorAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("OTHER"), amount = 10m, paidFromBankAccountId = dollars, partnerId = electricity }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.quick_account_currency");
        (await owner.PostErrorAsync("/api/v1/expenses/quick", new { companyId, categoryId = Kind("OTHER"), amount = 0m, paidFromBankAccountId = cash }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.amount_invalid");
        (await owner.PostErrorAsync("/api/v1/expenses/quick", new { companyId, categoryId = Guid.NewGuid(), amount = 10m, paidFromBankAccountId = cash }, HttpStatusCode.NotFound)).Code.ShouldBe("expense_category.not_found");

        // The month's list carries all four with what each is.
        var month = await owner.GetOkAsync($"/api/v1/expenses?companyId={companyId}&status=confirmed&from=2026-09-01&to=2026-09-30");
        month.GetArrayLength().ShouldBe(4);
        month.EnumerateArray().Sum(static e => e.GetProperty("totalGross").GetDecimal()).ShouldBe(450000m);
        (await AccountAsync(ws.TenantId, companyId, "2110")).ShouldBe(-45000m, "only the phone bill is still owed");
        await owner.AssertInvariantsAsync();
    }
}

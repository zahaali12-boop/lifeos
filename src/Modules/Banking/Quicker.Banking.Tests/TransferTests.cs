using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// Transfers between the company's own accounts (ADR-0035 §24, A-210) in a dinar company: cash paid into the bank,
/// dollars bought with dinars above the day's rate (a realised loss), dollars sold for cash above it (a gain), dollars
/// moved between two dollar boxes, the refusals, a transfer reversed, and every step reconciled by the harness.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransferTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Cash, Guid Bank, Guid Dollars, Guid Dollars2, Guid Euros);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "TRF", legalName = Name("Baghdad Imports", "استيرادات بغداد"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        foreach (var (from, rate) in new[] { ("2026-10-01", 1310m), ("2026-10-10", 1320m) })
        {
            await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = from, rate });
        }

        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "BANK", name = Name("Rafidain account", "حساب الرافدين"), kind = "bank", currency = "IQD" })).GetProperty("id").GetGuid();
        var dollars = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "USD", name = Name("Dollar box", "صندوق الدولار"), kind = "cash", currency = "USD" })).GetProperty("id").GetGuid();
        var dollars2 = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "USD2", name = Name("Dollar account", "حساب الدولار"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();
        var euros = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "EUR", name = Name("Euro account", "حساب اليورو"), kind = "bank", currency = "EUR" })).GetProperty("id").GetGuid();

        // What the boxes held at go-live: 10 000 000 in the bank and 2 000 000 in the cash box.
        var opening = await owner.PostAsync($"/api/v1/onboarding/companies/{companyId}/opening-balances?asOf=2026-09-30", new { lines = new object[] { new { account = "1121", debit = 10_000_000m, credit = 0m, bankAccount = "BANK" }, new { account = "1111", debit = 2_000_000m, credit = 0m, bankAccount = "CASH" }, new { account = "3100", debit = 0m, credit = 12_000_000m } } }, HttpStatusCode.OK);
        opening.ValueKind.ShouldBe(JsonValueKind.Object);
        return new Setup(ws, owner, companyId, cash, bank, dollars, dollars2, euros);
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private static async Task<(decimal Tc, decimal Fc)> BalanceAsync(Setup s, Guid id)
    {
        var account = await s.Owner.GetOkAsync($"/api/v1/banking/bank-accounts/{id}");
        return (account.GetProperty("balanceTc").GetDecimal(), account.GetProperty("balanceFc").GetDecimal());
    }

    [Fact]
    public async Task Cash_paid_into_the_bank_dollars_bought_and_sold_with_their_exchange_differences_and_a_transfer_reversed_keep_every_box_and_ledger_together()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Refusals: one account twice, nothing sent, two different amounts between accounts of one currency.
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Cash, toBankAccountId = s.Cash, amount = 100m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.same_account");
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Cash, toBankAccountId = s.Bank, amount = 0m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.amount_invalid");
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Cash, toBankAccountId = s.Bank, amount = 1_500_000m, amountReceived = 1_499_000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.amounts_differ");

        // 1. 1 500 000 of cash paid into the bank: the cash box down, the bank up, nothing else.
        var deposit = await owner.PostAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Cash, toBankAccountId = s.Bank, amount = 1_500_000m, transferDate = "2026-10-01", reference = "DEP-77", notes = "Cash of the week to the bank" });
        var depositId = deposit.GetProperty("id").GetGuid();
        deposit.GetProperty("number").GetString().ShouldStartWith("TRF-2026-");
        deposit.GetProperty("status").GetString().ShouldBe("posted");
        deposit.GetProperty("amountReceived").GetDecimal().ShouldBe(1_500_000m);
        deposit.GetProperty("exchangeDifferenceFc").GetDecimal().ShouldBe(0m);
        deposit.GetProperty("fromBankAccountCode").GetString().ShouldBe("CASH");
        deposit.GetProperty("toBankAccountCode").GetString().ShouldBe("BANK");
        (await BalanceAsync(s, s.Cash)).ShouldBe((500_000m, 500_000m));
        (await BalanceAsync(s, s.Bank)).ShouldBe((11_500_000m, 11_500_000m));
        await owner.AssertInvariantsAsync();

        // 2. 1 000 dollars bought for 1 320 000 dinars on a day the rate is 1 310: the dollars are worth 1 310 000, the 10 000 above is a loss.
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Bank, toBankAccountId = s.Dollars, amount = 1_320_000m, transferDate = "2026-10-01" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.amount_received_required");
        var bought = await owner.PostAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Bank, toBankAccountId = s.Dollars, amount = 1_320_000m, amountReceived = 1_000m, transferDate = "2026-10-01" });
        bought.GetProperty("currencySent").GetString().ShouldBe("IQD");
        bought.GetProperty("currencyReceived").GetString().ShouldBe("USD");
        bought.GetProperty("exchangeRate").GetDecimal().ShouldBe(1310m);
        bought.GetProperty("amountSentFc").GetDecimal().ShouldBe(1_320_000m);
        bought.GetProperty("amountReceivedFc").GetDecimal().ShouldBe(1_310_000m);
        bought.GetProperty("exchangeDifferenceFc").GetDecimal().ShouldBe(-10_000m);
        (await RoleAsync(s, "FxLossRealized")).ShouldBe(10_000m);
        (await BalanceAsync(s, s.Bank)).ShouldBe((10_180_000m, 10_180_000m));
        (await BalanceAsync(s, s.Dollars)).ShouldBe((1_000m, 1_310_000m));
        await owner.AssertInvariantsAsync();

        // 3. 400 dollars sold for 530 000 dinars of cash when the rate is 1 320: they leave the books at 528 000, the 2 000 above is a gain.
        var sold = await owner.PostAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Dollars, toBankAccountId = s.Cash, amount = 400m, amountReceived = 530_000m, transferDate = "2026-10-10" });
        sold.GetProperty("exchangeRate").GetDecimal().ShouldBe(1320m);
        sold.GetProperty("amountSentFc").GetDecimal().ShouldBe(528_000m);
        sold.GetProperty("exchangeDifferenceFc").GetDecimal().ShouldBe(2_000m);
        (await RoleAsync(s, "FxGainRealized")).ShouldBe(-2_000m);
        (await BalanceAsync(s, s.Dollars)).ShouldBe((600m, 782_000m));
        (await BalanceAsync(s, s.Cash)).ShouldBe((1_030_000m, 1_030_000m));
        await owner.AssertInvariantsAsync();

        // 4. Dollars between two dollar accounts, valued at the day's rate on both sides; none without a rate; none straight from dollars to euros.
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Dollars, toBankAccountId = s.Dollars2, amount = 100m, transferDate = "2026-09-30" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.rate_missing");
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Dollars, toBankAccountId = s.Euros, amount = 100m, amountReceived = 92m, transferDate = "2026-10-10" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.currencies_unsupported");
        var moved = await owner.PostAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Dollars, toBankAccountId = s.Dollars2, amount = 100m, transferDate = "2026-10-10" });
        moved.GetProperty("amountSentFc").GetDecimal().ShouldBe(132_000m);
        moved.GetProperty("amountReceivedFc").GetDecimal().ShouldBe(132_000m);
        (await BalanceAsync(s, s.Dollars)).ShouldBe((500m, 650_000m));
        (await BalanceAsync(s, s.Dollars2)).ShouldBe((100m, 132_000m));
        await owner.AssertInvariantsAsync();

        // The dollar box's transfers, newest first; the trader's cash book names each movement a transfer.
        var ofDollars = await owner.GetOkAsync($"/api/v1/banking/transfers?companyId={s.CompanyId}&bankAccountId={s.Dollars}");
        ofDollars.GetArrayLength().ShouldBe(3);
        ofDollars[0].GetProperty("transferDate").GetString().ShouldBe("2026-10-10");
        var book = await owner.GetOkAsync($"/api/v1/trader/cash?companyId={s.CompanyId}&bankAccountId={s.Dollars}&from=2026-10-01&to=2026-10-31");
        var movements = book.GetProperty("accounts")[0].GetProperty("movements");
        movements.GetArrayLength().ShouldBe(3);
        movements.EnumerateArray().ShouldAllBe(m => m.GetProperty("sourceDocumentType").GetString() == "bank_transfer");

        // 5. The cash paid in reversed: both boxes given back, the transfer marked, a second reversal refused.
        (await owner.PostErrorAsync($"/api/v1/banking/transfers/{depositId}/reverse", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.reason_required");
        (await owner.PostErrorAsync($"/api/v1/banking/transfers/{depositId}/reverse", new { reason = "Entered twice", reversalDate = "2026-09-30" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/banking/transfers/{depositId}/reverse", new { reason = "Entered twice", reversalDate = "2026-10-12" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("reversalReason").GetString().ShouldBe("Entered twice");
        (await BalanceAsync(s, s.Cash)).ShouldBe((2_530_000m, 2_530_000m));
        (await BalanceAsync(s, s.Bank)).ShouldBe((8_680_000m, 8_680_000m));
        (await owner.PostErrorAsync($"/api/v1/banking/transfers/{depositId}/reverse", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("transfer.not_posted");
        (await owner.GetOkAsync($"/api/v1/banking/transfers/{depositId}")).GetProperty("status").GetString().ShouldBe("reversed");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_closed_box_and_another_companys_box_are_refused()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var other = await owner.PostAsync("/api/v1/organization/companies", new { code = "OTH", legalName = Name("Other Co", "شركة أخرى"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var otherId = other.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "OTHC", companyId = otherId });
        var foreignBox = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId = otherId, code = "OCASH", name = Name("Other cash", "صندوق آخر"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Cash, toBankAccountId = foreignBox, amount = 100m }, HttpStatusCode.NotFound)).Code.ShouldBe("bank_account.not_found");

        await owner.PutAsync($"/api/v1/banking/bank-accounts/{s.Dollars2}", new { companyId = s.CompanyId, code = "USD2", name = Name("Dollar account", "حساب الدولار"), kind = "bank", currency = "USD", isActive = false });
        (await owner.PostErrorAsync("/api/v1/banking/transfers", new { companyId = s.CompanyId, fromBankAccountId = s.Dollars, toBankAccountId = s.Dollars2, amount = 10m, transferDate = "2026-10-10" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.bank_account_inactive");
        (await owner.GetOkAsync($"/api/v1/banking/transfers?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(0);
    }
}

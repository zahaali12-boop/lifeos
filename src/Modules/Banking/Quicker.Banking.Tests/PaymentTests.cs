using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// Supplier payments (roadmap 4.7, hard scenario 3 on the payables side): a EUR invoice in a USD company paid in two
/// instalments at different rates with bank fees, each relieving the payable at its booked value with the difference
/// as realised FX; a local payment taking the early-payment discount and withholding tax, with a remainder on account
/// applied and unapplied; an advance applied; a proposal turned into a payment; every step reconciled by the harness.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Euro, Guid Local, Guid Main, Guid EuroBank, Guid Cash);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "FX", legalName = Name("Forex Co", "شركة الصرف"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        foreach (var (from, rate) in new[] { ("2026-09-01", 1.10m), ("2026-09-10", 1.08m), ("2026-09-20", 1.12m) })
        {
            await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "EUR", toCurrency = "USD", validFrom = from, rate });
        }

        var euro = (await owner.PostAsync("/api/v1/partners", new { code = "EURO", legalName = Name("Euro Parts", "قطع أوروبية"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{euro}/supplier-accounts/{companyId}", new { currency = "EUR", leadTimeDays = 7 });
        var wht = (await owner.PostAsync("/api/v1/partners/wht-codes", new { code = "WHT5P", name = Name("5 % at payment", "٥٪ عند الدفع"), ratePct = 5m, withholdAt = "payment" })).GetProperty("id").GetGuid();
        var terms = (await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "2-10-N30", name = Name("2 % in 10 days, net 30", "٢٪ خلال ١٠ أيام، صافي ٣٠"), dueBasis = "invoice_date", dueDays = 30, earlyDiscountPct = 2m, earlyDiscountDays = 10 })).GetProperty("id").GetGuid();
        var local = (await owner.PostAsync("/api/v1/partners", new { code = "LOCAL", legalName = Name("Local Services", "خدمات محلية"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{local}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 1, whtCodeId = wht, paymentTermsId = terms });

        (await owner.PostErrorAsync("/api/v1/banking/bank-accounts", new { companyId, code = "X", name = Name("Wrong", "خطأ"), kind = "wallet", currency = "USD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bank_account.kind_invalid");
        var main = await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main USD account", "الحساب الرئيسي بالدولار"), kind = "bank", currency = "USD", bankName = "Trade Bank", iban = "IQ98 TBIQ 0000 1234 5678 901" });
        main.GetProperty("glAccountCode").GetString().ShouldBe("1121");
        (await owner.PostErrorAsync("/api/v1/banking/bank-accounts", new { companyId, code = "main", name = Name("Again", "مرة أخرى"), kind = "bank", currency = "USD" }, HttpStatusCode.Conflict)).Code.ShouldBe("bank_account.code_taken");
        var euroBank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "EURB", name = Name("Euro account", "حساب اليورو"), kind = "bank", currency = "EUR" })).GetProperty("id").GetGuid();
        var cash = await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "USD" });
        cash.GetProperty("glAccountCode").GetString().ShouldBe("1111");
        return new Setup(ws, owner, companyId, euro, local, main.GetProperty("id").GetGuid(), euroBank, cash.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> InvoiceAsync(Setup s, Guid partner, string reference, string date, decimal amount)
    {
        var invoice = await s.Owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = partner, kind = "expense", supplierInvoiceNumber = reference, documentDate = date, lines = new[] { new { kind = "expense", quantity = 1m, unitPrice = amount, description = "Consulting" } } });
        var id = invoice.GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/purchasing/invoices/{id}/submit", new { }, HttpStatusCode.OK);
        var posted = await s.Owner.PostAsync($"/api/v1/purchasing/invoices/{id}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private static async Task<JsonElement> ItemAsync(Setup s, Guid id) => (await s.Owner.GetOkAsync($"/api/v1/payables/open-items/{id}")).GetProperty("item");

    private static async Task<JsonElement> BankAsync(Setup s, Guid id) => await s.Owner.GetOkAsync($"/api/v1/banking/bank-accounts/{id}");

    [Fact]
    public async Task A_eur_invoice_paid_in_two_instalments_at_different_rates_with_fees_books_realised_fx_and_a_local_payment_takes_discount_and_withholding()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // EUR 1 000 booked at 1.10: the payable carries 1 100 USD.
        var euroItem = await InvoiceAsync(s, s.Euro, "E-1", "2026-09-01", 1000m);
        (await ItemAsync(s, euroItem)).GetProperty("originalFc").GetDecimal().ShouldBe(1100m);

        // First instalment from the USD account at 1.08 with a 5 USD fee: 400 EUR leave the books at 440, the bank gives 437, the 8 is a gain.
        (await owner.PostErrorAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, paymentDate = "2026-09-10", lines = new[] { new { openItemId = euroItem, amount = 400m } }, charges = 5m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payment.bank_amount_required");
        (await owner.PostErrorAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, paymentDate = "2026-09-10", lines = new[] { new { openItemId = euroItem, amount = 1400m } }, charges = 5m, bankAmount = 1517m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payment.line_amount_invalid");
        var first = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, paymentDate = "2026-09-10", reference = "TT-1", lines = new[] { new { openItemId = euroItem, amount = 400m } }, charges = 5m, bankAmount = 437m });
        var firstId = first.GetProperty("id").GetGuid();
        first.GetProperty("number").GetString().ShouldStartWith("PAY-2026-");
        first.GetProperty("currency").GetString().ShouldBe("EUR");
        first.GetProperty("exchangeRate").GetDecimal().ShouldBe(1.08m);
        first.GetProperty("amountTc").GetDecimal().ShouldBe(400m);
        var firstPosted = await owner.PostAsync($"/api/v1/banking/payments/{firstId}/post", new { }, HttpStatusCode.OK);
        firstPosted.GetProperty("status").GetString().ShouldBe("posted");
        (await owner.PostErrorAsync($"/api/v1/banking/payments/{firstId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("payment.not_draft");
        (await RoleAsync(s, "FxGainRealized")).ShouldBe(-8m);
        (await RoleAsync(s, "BankCharges")).ShouldBe(5m);
        (await RoleAsync(s, "AP")).ShouldBe(-660m);
        var mainAfter = await BankAsync(s, s.Main);
        mainAfter.GetProperty("balanceTc").GetDecimal().ShouldBe(-437m);
        mainAfter.GetProperty("balanceFc").GetDecimal().ShouldBe(-437m);
        var euroAfterFirst = await ItemAsync(s, euroItem);
        euroAfterFirst.GetProperty("remainingTc").GetDecimal().ShouldBe(600m);
        euroAfterFirst.GetProperty("remainingFc").GetDecimal().ShouldBe(660m);
        euroAfterFirst.GetProperty("status").GetString().ShouldBe("partially_settled");
        var ownItem = await ItemAsync(s, firstPosted.GetProperty("openItemId").GetGuid());
        ownItem.GetProperty("kind").GetString().ShouldBe("payment_on_account");
        ownItem.GetProperty("status").GetString().ShouldBe("settled");
        var settlement = (await owner.GetOkAsync($"/api/v1/payables/settlements?companyId={s.CompanyId}&openItemId={euroItem}")).EnumerateArray().Single();
        settlement.GetProperty("kind").GetString().ShouldBe("payment");
        settlement.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(-8m);
        await owner.AssertInvariantsAsync();

        // Second instalment from the EUR account at 1.12 with a 2 EUR fee: 600 EUR leave at 660, the bank gives 674.24 for 602, the 12 is a loss.
        var second = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.EuroBank, paymentDate = "2026-09-20", lines = new[] { new { openItemId = euroItem, amount = 600m } }, charges = 2m });
        var secondId = second.GetProperty("id").GetGuid();
        second.GetProperty("bankAmount").GetDecimal().ShouldBe(602m);
        await owner.PostAsync($"/api/v1/banking/payments/{secondId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "FxLossRealized")).ShouldBe(12m);
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        var euroBank = await BankAsync(s, s.EuroBank);
        euroBank.GetProperty("balanceTc").GetDecimal().ShouldBe(-602m);
        euroBank.GetProperty("balanceFc").GetDecimal().ShouldBe(-674.24m);
        (await ItemAsync(s, euroItem)).GetProperty("status").GetString().ShouldBe("settled");
        await owner.AssertInvariantsAsync();

        // Reversed the next day: the payable reopens at its booked value, the loss and the bank movement are mirrored.
        (await owner.PostErrorAsync($"/api/v1/banking/payments/{secondId}/reverse", new { reason = "Bounced", reversalDate = "2026-09-19" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("payment.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/banking/payments/{secondId}/reverse", new { reason = "Transfer rejected by the bank", reversalDate = "2026-09-21" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await RoleAsync(s, "FxLossRealized")).ShouldBe(0m);
        (await RoleAsync(s, "AP")).ShouldBe(-660m);
        (await BankAsync(s, s.EuroBank)).GetProperty("balanceTc").GetDecimal().ShouldBe(0m);
        var reopened = await ItemAsync(s, euroItem);
        reopened.GetProperty("remainingTc").GetDecimal().ShouldBe(600m);
        reopened.GetProperty("remainingFc").GetDecimal().ShouldBe(660m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.EuroBank}/transactions")).GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();

        // A local invoice on 2/10 net 30 with 5 % withheld at payment, paid in cash within the discount window with 700 on account.
        var localItem = await InvoiceAsync(s, s.Local, "L-1", "2026-09-15", 10_000m);
        var localInfo = await ItemAsync(s, localItem);
        localInfo.GetProperty("discountDate").GetString().ShouldBe("2026-09-25");
        localInfo.GetProperty("dueDate").GetString().ShouldBe("2026-10-15");
        var local = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, method = "cash", lines = new[] { new { openItemId = localItem, amount = 10_000m } }, onAccount = 700m });
        var localId = local.GetProperty("id").GetGuid();
        local.GetProperty("discountTc").GetDecimal().ShouldBe(200m);
        local.GetProperty("whtTc").GetDecimal().ShouldBe(500m);
        local.GetProperty("amountTc").GetDecimal().ShouldBe(10_000m);
        var line = local.GetProperty("lines")[0];
        line.GetProperty("cashTc").GetDecimal().ShouldBe(9_300m);
        var localPosted = await owner.PostAsync($"/api/v1/banking/payments/{localId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "DiscountTaken")).ShouldBe(-200m);
        (await RoleAsync(s, "WhtPayable")).ShouldBe(-500m);
        (await RoleAsync(s, "Cash")).ShouldBe(-10_000m);
        (await RoleAsync(s, "AP")).ShouldBe(40m);
        (await ItemAsync(s, localItem)).GetProperty("status").GetString().ShouldBe("settled");
        var onAccount = await ItemAsync(s, localPosted.GetProperty("openItemId").GetGuid());
        onAccount.GetProperty("remainingTc").GetDecimal().ShouldBe(-700m);
        onAccount.GetProperty("status").GetString().ShouldBe("partially_settled");
        await owner.AssertInvariantsAsync();

        // The 700 on account applied to a second invoice, then unapplied so the payment can be reversed.
        var localSecond = await InvoiceAsync(s, s.Local, "L-2", "2026-09-22", 1_000m);
        var applied = await owner.PostAsync("/api/v1/payables/settlements/apply", new { settlingItemId = onAccount.GetProperty("id").GetGuid(), settledItemId = localSecond, amount = 700m }, HttpStatusCode.OK);
        applied.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ItemAsync(s, localSecond)).GetProperty("remainingTc").GetDecimal().ShouldBe(300m);
        (await owner.PostErrorAsync($"/api/v1/banking/payments/{localId}/reverse", new { reason = "Wrong supplier" }, HttpStatusCode.Conflict)).Code.ShouldBe("payment.applied");
        var unapplied = await owner.PostAsync($"/api/v1/payables/settlements/{applied.GetProperty("id").GetGuid()}/reverse", new { reason = "Applied to the wrong invoice" }, HttpStatusCode.OK);
        unapplied.GetProperty("amountTc").GetDecimal().ShouldBe(-700m);
        (await ItemAsync(s, localSecond)).GetProperty("remainingTc").GetDecimal().ShouldBe(1_000m);
        await owner.PostAsync($"/api/v1/banking/payments/{localId}/reverse", new { reason = "Wrong supplier" }, HttpStatusCode.OK);
        (await RoleAsync(s, "DiscountTaken")).ShouldBe(0m);
        (await RoleAsync(s, "WhtPayable")).ShouldBe(0m);
        (await RoleAsync(s, "Cash")).ShouldBe(0m);
        (await ItemAsync(s, localItem)).GetProperty("remainingTc").GetDecimal().ShouldBe(10_000m);
        await owner.AssertInvariantsAsync();

        // An advance of 2 000 applied to the first local invoice: it moves from the advances control to payables.
        var advance = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, kind = "supplier_advance", method = "cash", onAccount = 2_000m });
        var advanceId = advance.GetProperty("id").GetGuid();
        var advancePosted = await owner.PostAsync($"/api/v1/banking/payments/{advanceId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "SupplierAdvances")).ShouldBe(2_000m);
        var advanceItem = await ItemAsync(s, advancePosted.GetProperty("openItemId").GetGuid());
        advanceItem.GetProperty("kind").GetString().ShouldBe("advance");
        var applyAdvance = await owner.PostAsync("/api/v1/payables/settlements/apply", new { settlingItemId = advanceItem.GetProperty("id").GetGuid(), settledItemId = localItem, amount = 2_000m }, HttpStatusCode.OK);
        applyAdvance.GetProperty("kind").GetString().ShouldBe("advance_application");
        applyAdvance.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, "SupplierAdvances")).ShouldBe(0m);
        (await ItemAsync(s, localItem)).GetProperty("remainingTc").GetDecimal().ShouldBe(8_000m);
        await owner.AssertInvariantsAsync();

        // A proposal for the month: both local invoices, discounts still open, paid with one payment drafted from it.
        var proposal = await owner.PostAsync("/api/v1/payables/proposals", new { companyId = s.CompanyId, payThrough = "2026-10-31", currency = "USD", partnerId = s.Local });
        var proposalId = proposal.GetProperty("id").GetGuid();
        proposal.GetProperty("discountTc").GetDecimal().ShouldBe(180m);
        (await owner.PostErrorAsync("/api/v1/banking/payments/from-proposal", new { proposalId, bankAccountId = s.Main }, HttpStatusCode.Conflict)).Code.ShouldBe("payment.proposal_not_approved");
        await owner.PostAsync($"/api/v1/payables/proposals/{proposalId}/approve", new { }, HttpStatusCode.OK);
        var drafted = await owner.PostAsync("/api/v1/banking/payments/from-proposal", new { proposalId, bankAccountId = s.Main }, HttpStatusCode.OK);
        var draft = drafted.EnumerateArray().Single();
        draft.GetProperty("lines").GetArrayLength().ShouldBe(2);
        draft.GetProperty("discountTc").GetDecimal().ShouldBe(180m);
        draft.GetProperty("whtTc").GetDecimal().ShouldBe(450m);
        draft.GetProperty("amountTc").GetDecimal().ShouldBe(8_370m);
        (await owner.GetOkAsync($"/api/v1/payables/proposals/{proposalId}")).GetProperty("status").GetString().ShouldBe("executed");
        await owner.PostAsync($"/api/v1/banking/payments/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "AP")).ShouldBe(-660m);
        (await ItemAsync(s, localSecond)).GetProperty("status").GetString().ShouldBe("settled");
        var aging = await owner.GetOkAsync($"/api/v1/payables/open-items/aging?companyId={s.CompanyId}");
        aging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(660m);
        (await owner.GetOkAsync($"/api/v1/banking/payments?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(5);
        await owner.AssertInvariantsAsync();
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// Customer receipts (roadmap 5.7, hard scenario 3 on the receivables side): a EUR invoice in a USD company collected
/// in two instalments at different rates with bank charges, each relieving the receivable at its booked value with the
/// difference as realised FX; a local receipt giving the early-payment discount and taking the tax the customer
/// withheld, with a remainder on account applied and unapplied; a deposit applied; every step reconciled by the harness.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReceiptTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Euro, Guid Local, Guid Wht, Guid Main, Guid EuroBank, Guid Cash);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "FXR", legalName = Name("Forex Sales Co", "شركة الصرف للمبيعات"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        foreach (var (from, rate) in new[] { ("2026-09-01", 1.10m), ("2026-09-10", 1.08m), ("2026-09-20", 1.12m) })
        {
            await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "EUR", toCurrency = "USD", validFrom = from, rate });
        }

        var euro = (await owner.PostAsync("/api/v1/partners", new { code = "EURO-C", legalName = Name("Euro Stores", "متاجر أوروبية"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{euro}/customer-accounts/{companyId}", new { currency = "EUR" });
        var wht = (await owner.PostAsync("/api/v1/partners/wht-codes", new { code = "WHT5R", name = Name("5 % withheld by the customer", "٥٪ يحجزها العميل"), ratePct = 5m, withholdAt = "payment" })).GetProperty("id").GetGuid();
        var terms = (await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "2-10-N30", name = Name("2 % in 10 days, net 30", "٢٪ خلال ١٠ أيام، صافي ٣٠"), dueBasis = "invoice_date", dueDays = 30, earlyDiscountPct = 2m, earlyDiscountDays = 10 })).GetProperty("id").GetGuid();
        var local = (await owner.PostAsync("/api/v1/partners", new { code = "LOCAL-C", legalName = Name("Local Retail", "تجزئة محلية"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{local}/customer-accounts/{companyId}", new { currency = "USD", paymentTermsId = terms });

        var main = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main USD account", "الحساب الرئيسي بالدولار"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();
        var euroBank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "EURB", name = Name("Euro account", "حساب اليورو"), kind = "bank", currency = "EUR" })).GetProperty("id").GetGuid();
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "USD" })).GetProperty("id").GetGuid();
        return new Setup(ws, owner, companyId, euro, local, wht, main, euroBank, cash);
    }

    /// <summary>A service invoice to the customer, posted; its single open item.</summary>
    private static async Task<Guid> InvoiceAsync(Setup s, Guid partner, string reference, string date, decimal amount)
    {
        var invoice = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = partner, customerReference = reference, documentDate = date, lines = new[] { new { kind = "service", quantity = 1m, unitPrice = amount, description = "Consulting" } } });
        var id = invoice.GetProperty("id").GetGuid();
        var posted = await s.Owner.PostAsync($"/api/v1/sales/invoices/{id}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private static async Task<JsonElement> ItemAsync(Setup s, Guid id) => (await s.Owner.GetOkAsync($"/api/v1/receivables/open-items/{id}")).GetProperty("item");

    private static async Task<JsonElement> BankAsync(Setup s, Guid id) => await s.Owner.GetOkAsync($"/api/v1/banking/bank-accounts/{id}");

    [Fact]
    public async Task A_eur_invoice_collected_in_two_instalments_at_different_rates_with_charges_books_realised_fx_and_a_local_receipt_gives_discount_and_takes_withholding()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // EUR 1 000 invoiced at 1.10: the receivable carries 1 100 USD.
        var euroItem = await InvoiceAsync(s, s.Euro, "EC-1", "2026-09-01", 1000m);
        (await ItemAsync(s, euroItem)).GetProperty("originalFc").GetDecimal().ShouldBe(1100m);

        // First instalment into the USD account at 1.08, the bank keeping 5 USD: 400 EUR leave the books at 440, the bank credits 427, the 8 is a loss.
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, receiptDate = "2026-09-10", lines = new[] { new { openItemId = euroItem, amount = 400m } }, charges = 5m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.bank_amount_required");
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, receiptDate = "2026-09-10", lines = new[] { new { openItemId = euroItem, amount = 1400m } }, charges = 5m, bankAmount = 1507m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.line_amount_invalid");
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Main, lines = new[] { new { openItemId = euroItem, amount = 400m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.item_unknown");
        var first = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.Main, receiptDate = "2026-09-10", reference = "TT-IN-1", lines = new[] { new { openItemId = euroItem, amount = 400m } }, charges = 5m, bankAmount = 427m });
        var firstId = first.GetProperty("id").GetGuid();
        first.GetProperty("number").GetString().ShouldStartWith("RCT-2026-");
        first.GetProperty("kind").GetString().ShouldBe("customer_receipt");
        first.GetProperty("currency").GetString().ShouldBe("EUR");
        first.GetProperty("exchangeRate").GetDecimal().ShouldBe(1.08m);
        first.GetProperty("amountTc").GetDecimal().ShouldBe(400m);
        (await owner.GetOkAsync($"/api/v1/banking/payments?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(0, "a receipt is not a payment");
        var firstPosted = await owner.PostAsync($"/api/v1/banking/receipts/{firstId}/post", new { }, HttpStatusCode.OK);
        firstPosted.GetProperty("status").GetString().ShouldBe("posted");
        (await owner.PostErrorAsync($"/api/v1/banking/receipts/{firstId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("receipt.not_draft");
        (await RoleAsync(s, "FxLossRealized")).ShouldBe(8m);
        (await RoleAsync(s, "BankCharges")).ShouldBe(5m);
        (await RoleAsync(s, "AR")).ShouldBe(660m);
        var mainAfter = await BankAsync(s, s.Main);
        mainAfter.GetProperty("balanceTc").GetDecimal().ShouldBe(427m);
        mainAfter.GetProperty("balanceFc").GetDecimal().ShouldBe(427m);
        var euroAfterFirst = await ItemAsync(s, euroItem);
        euroAfterFirst.GetProperty("remainingTc").GetDecimal().ShouldBe(600m);
        euroAfterFirst.GetProperty("remainingFc").GetDecimal().ShouldBe(660m);
        euroAfterFirst.GetProperty("status").GetString().ShouldBe("partially_settled");
        var ownItem = await ItemAsync(s, firstPosted.GetProperty("openItemId").GetGuid());
        ownItem.GetProperty("kind").GetString().ShouldBe("receipt_on_account");
        ownItem.GetProperty("status").GetString().ShouldBe("settled");
        var settlement = (await owner.GetOkAsync($"/api/v1/receivables/settlements?companyId={s.CompanyId}&openItemId={euroItem}")).EnumerateArray().Single();
        settlement.GetProperty("kind").GetString().ShouldBe("receipt");
        settlement.GetProperty("fxGainLossFc").GetDecimal().ShouldBe(-8m);
        await owner.AssertInvariantsAsync();

        // Second instalment into the EUR account at 1.12, the bank keeping 2 EUR: 600 EUR leave at 660, the bank credits 598 worth 669.76, the 12 is a gain.
        var second = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Euro, bankAccountId = s.EuroBank, receiptDate = "2026-09-20", lines = new[] { new { openItemId = euroItem, amount = 600m } }, charges = 2m });
        var secondId = second.GetProperty("id").GetGuid();
        second.GetProperty("bankAmount").GetDecimal().ShouldBe(598m);
        await owner.PostAsync($"/api/v1/banking/receipts/{secondId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "FxGainRealized")).ShouldBe(-12m);
        (await RoleAsync(s, "AR")).ShouldBe(0m);
        var euroBank = await BankAsync(s, s.EuroBank);
        euroBank.GetProperty("balanceTc").GetDecimal().ShouldBe(598m);
        euroBank.GetProperty("balanceFc").GetDecimal().ShouldBe(669.76m);
        (await ItemAsync(s, euroItem)).GetProperty("status").GetString().ShouldBe("settled");
        await owner.AssertInvariantsAsync();

        // Reversed the next day: the receivable reopens at its booked value, the gain and the bank movement are mirrored.
        (await owner.PostErrorAsync($"/api/v1/banking/receipts/{secondId}/reverse", new { reason = "Bounced", reversalDate = "2026-09-19" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.reversal_date_invalid");
        var reversed = await owner.PostAsync($"/api/v1/banking/receipts/{secondId}/reverse", new { reason = "Transfer recalled by the customer's bank", reversalDate = "2026-09-21" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await RoleAsync(s, "FxGainRealized")).ShouldBe(0m);
        (await RoleAsync(s, "AR")).ShouldBe(660m);
        (await BankAsync(s, s.EuroBank)).GetProperty("balanceTc").GetDecimal().ShouldBe(0m);
        var reopened = await ItemAsync(s, euroItem);
        reopened.GetProperty("remainingTc").GetDecimal().ShouldBe(600m);
        reopened.GetProperty("remainingFc").GetDecimal().ShouldBe(660m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.EuroBank}/transactions")).GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();

        // A local invoice on 2/10 net 30, paid in cash within the discount window, the customer withholding 5 %, with 700 more on account.
        var localItem = await InvoiceAsync(s, s.Local, "LC-1", "2026-09-15", 10_000m);
        var localInfo = await ItemAsync(s, localItem);
        localInfo.GetProperty("discountDate").GetString().ShouldBe("2026-09-25");
        localInfo.GetProperty("dueDate").GetString().ShouldBe("2026-10-15");
        var local = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, method = "cash", lines = new[] { new { openItemId = localItem, amount = 10_000m } }, onAccount = 700m, whtCodeId = s.Wht });
        var localId = local.GetProperty("id").GetGuid();
        local.GetProperty("discountTc").GetDecimal().ShouldBe(200m);
        local.GetProperty("whtTc").GetDecimal().ShouldBe(500m);
        local.GetProperty("amountTc").GetDecimal().ShouldBe(10_000m);
        local.GetProperty("lines")[0].GetProperty("cashTc").GetDecimal().ShouldBe(9_300m);
        var localPosted = await owner.PostAsync($"/api/v1/banking/receipts/{localId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "DiscountGiven")).ShouldBe(200m);
        (await RoleAsync(s, "WhtReceivable")).ShouldBe(500m);
        (await RoleAsync(s, "Cash")).ShouldBe(10_000m);
        (await RoleAsync(s, "AR")).ShouldBe(-40m);
        (await ItemAsync(s, localItem)).GetProperty("status").GetString().ShouldBe("settled");
        var onAccount = await ItemAsync(s, localPosted.GetProperty("openItemId").GetGuid());
        onAccount.GetProperty("remainingTc").GetDecimal().ShouldBe(-700m);
        onAccount.GetProperty("status").GetString().ShouldBe("partially_settled");
        await owner.AssertInvariantsAsync();

        // The 700 on account applied to a second invoice, then unapplied so the receipt can be reversed.
        var localSecond = await InvoiceAsync(s, s.Local, "LC-2", "2026-09-22", 1_000m);
        var applied = await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = onAccount.GetProperty("id").GetGuid(), settledItemId = localSecond, amount = 700m }, HttpStatusCode.OK);
        applied.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ItemAsync(s, localSecond)).GetProperty("remainingTc").GetDecimal().ShouldBe(300m);
        (await owner.PostErrorAsync($"/api/v1/banking/receipts/{localId}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.Conflict)).Code.ShouldBe("receipt.applied");
        await owner.PostAsync($"/api/v1/receivables/settlements/{applied.GetProperty("id").GetGuid()}/reverse", new { reason = "Applied to the wrong invoice" }, HttpStatusCode.OK);
        (await ItemAsync(s, localSecond)).GetProperty("remainingTc").GetDecimal().ShouldBe(1_000m);
        await owner.PostAsync($"/api/v1/banking/receipts/{localId}/reverse", new { reason = "Wrong customer" }, HttpStatusCode.OK);
        (await RoleAsync(s, "DiscountGiven")).ShouldBe(0m);
        (await RoleAsync(s, "WhtReceivable")).ShouldBe(0m);
        (await RoleAsync(s, "Cash")).ShouldBe(0m);
        (await ItemAsync(s, localItem)).GetProperty("remainingTc").GetDecimal().ShouldBe(10_000m);
        await owner.AssertInvariantsAsync();

        // A deposit of 2 000 applied to the first local invoice: it moves from the deposits control to receivables.
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, kind = "customer_deposit", method = "cash", lines = new[] { new { openItemId = localItem, amount = 10m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("receipt.deposit_no_lines");
        var deposit = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, kind = "customer_deposit", method = "cash", onAccount = 2_000m });
        var depositPosted = await owner.PostAsync($"/api/v1/banking/receipts/{deposit.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "CustomerDeposits")).ShouldBe(-2_000m);
        var depositItem = await ItemAsync(s, depositPosted.GetProperty("openItemId").GetGuid());
        depositItem.GetProperty("kind").GetString().ShouldBe("deposit");
        var applyDeposit = await owner.PostAsync("/api/v1/receivables/settlements/apply", new { settlingItemId = depositItem.GetProperty("id").GetGuid(), settledItemId = localItem, amount = 2_000m }, HttpStatusCode.OK);
        applyDeposit.GetProperty("kind").GetString().ShouldBe("deposit_application");
        applyDeposit.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, "CustomerDeposits")).ShouldBe(0m);
        (await ItemAsync(s, localItem)).GetProperty("remainingTc").GetDecimal().ShouldBe(8_000m);

        // What is owed now: 660 on the EUR invoice, 8 000 and 1 000 on the local ones.
        var aging = await owner.GetOkAsync($"/api/v1/receivables/open-items/aging?companyId={s.CompanyId}");
        aging.GetProperty("totals").GetProperty("totalFc").GetDecimal().ShouldBe(9_660m);
        (await owner.GetOkAsync($"/api/v1/banking/receipts?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(4);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_receipt_prints_as_a_payment_receipt_plain_and_branded_and_a_draft_carries_the_watermark()
    {
        // T12b: the receipt is a document of its own in the print pipeline, with the balance the customer still owes after it.
        var s = await SetUpAsync();
        var item = await InvoiceAsync(s, s.Local, "LR-PRINT", "2026-09-05", 1000m);
        var draft = await s.Owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Local, bankAccountId = s.Cash, receiptDate = "2026-09-12", method = "cash", lines = new[] { new { openItemId = item, amount = 400m } } });
        var id = draft.GetProperty("id").GetGuid();
        var number = draft.GetProperty("number").GetString()!;
        (await s.Owner.GetTextAsync($"/api/v1/print/preview?documentType=bank_receipt&documentId={id}")).ShouldContain("DRAFT");

        await s.Owner.PostAsync($"/api/v1/banking/receipts/{id}/post", new { }, HttpStatusCode.OK);
        var html = await s.Owner.GetTextAsync($"/api/v1/print/preview?documentType=bank_receipt&documentId={id}");
        html.ShouldContain("Payment receipt / سند قبض");
        html.ShouldContain(number);
        html.ShouldContain("Local Retail / تجزئة محلية");
        html.ShouldContain("400.00 USD");
        html.ShouldContain("Cash / نقداً");
        html.ShouldContain("CASH &#183; Cash box"); // the template escapes the middle dot
        // The balance after it: the invoice less the amount and the early-payment discount the customer's terms gave.
        var posted = await s.Owner.GetOkAsync($"/api/v1/banking/receipts/{id}");
        var remaining = 1000m - 400m - posted.GetProperty("discountTc").GetDecimal();
        html.ShouldContain($"{remaining.ToString("N2", CultureInfo.InvariantCulture)} USD");
        html.ShouldNotContain("DRAFT");

        var brand = await s.Owner.GetTextAsync($"/api/v1/print/preview?documentType=bank_receipt&documentId={id}&variant=brand");
        brand.ShouldContain("class=\"amount-card\"");
        brand.ShouldContain(number);
        brand.ShouldContain("Cairo-Variable.ttf");
        var types = await s.Owner.GetOkAsync("/api/v1/print/document-types");
        types.EnumerateArray().Single(t => t.GetProperty("documentType").GetString() == "bank_receipt").GetProperty("variants").EnumerateArray().Select(static v => v.GetProperty("code").GetString()).ShouldBe(["standard", "brand"]);
        await s.Owner.AssertInvariantsAsync();
    }
}

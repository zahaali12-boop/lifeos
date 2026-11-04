using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// Cheques and post-dated cheques (roadmap 6.3, POSTING_RULES §5, hard scenario 10): a post-dated cheque received
/// against an invoice sits on PDC receivable with the invoice settled, is deposited at maturity (cheques under
/// collection), bounces with the bank's charge and a fee re-charged to the customer, which restores the customer's
/// balance (the invoice is open again) and keeps the history; its replacement is deposited and clears into the bank.
/// An issued cheque takes its number from the book, sits on PDC payable with the supplier's invoice settled, is paid
/// by the bank, returned unpaid, and cancelled, which reopens the invoice. The harness holds after every step.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ChequeTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Customer, Guid Supplier, Guid Main, Guid Office);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "CHQ", legalName = Name("Cheque Traders", "تجار الشيكات"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "NOOR", legalName = Name("Al-Noor Markets", "أسواق النور"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "USD" });
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "STAT", legalName = Name("Stationery House", "بيت القرطاسية"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD" });
        var main = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main USD account", "الحساب الرئيسي"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();
        var office = (await owner.PostAsync("/api/v1/expenses/categories", new { code = "OFFICE", name = Name("Office supplies", "مستلزمات مكتبية"), accountCode = "6150" })).GetProperty("id").GetGuid();
        return new Setup(ws, owner, companyId, customer, supplier, main, office);
    }

    private static async Task<Guid> InvoiceAsync(Setup s, string reference, string date, decimal amount)
    {
        var invoice = await s.Owner.PostAsync("/api/v1/sales/invoices", new { companyId = s.CompanyId, partnerId = s.Customer, customerReference = reference, documentDate = date, lines = new[] { new { kind = "service", quantity = 1m, unitPrice = amount, description = "Consulting", accountRole = "Revenue" } } });
        var posted = await s.Owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        return posted.GetProperty("openItems").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private static async Task<JsonElement> ItemAsync(Setup s, Guid id) => (await s.Owner.GetOkAsync($"/api/v1/receivables/open-items/{id}")).GetProperty("item");

    private static async Task<decimal> BankBalanceAsync(Setup s) => (await s.Owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}")).GetProperty("balanceTc").GetDecimal();

    [Fact]
    public async Task A_post_dated_cheque_received_deposited_at_maturity_bounces_with_charges_restoring_the_customer_balance_and_its_replacement_clears()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var invoiceItem = await InvoiceAsync(s, "PO-10", "2026-09-05", 1000m);

        // A cheque is not money yet: charges wait for the bank; the number is required; the account must be in the cheque's currency.
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-09-05", method = "cheque", lines = new[] { new { openItemId = invoiceItem, amount = 1000m } }, charges = 5m, cheque = new { chequeNumber = "778899" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("cheque.no_charges");
        (await owner.PostErrorAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-09-05", method = "cheque", lines = new[] { new { openItemId = invoiceItem, amount = 1000m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("cheque.number_required");

        // Received on the 5th, dated the 1st of next month: post-dated, the invoice settled, the customer's balance down, PDC receivable up, the bank untouched.
        var receipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-09-05", method = "cheque", reference = "Rafidain 778899", lines = new[] { new { openItemId = invoiceItem, amount = 1000m } }, cheque = new { chequeNumber = "778899", drawerBank = "Rafidain Bank", dueDate = "2026-10-01" } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var cheque = receipt.GetProperty("cheque");
        (cheque.GetProperty("status").GetString(), cheque.GetProperty("isPostDated").GetBoolean(), cheque.GetProperty("direction").GetString()).ShouldBe(("draft", true, "received"));
        var chequeId = cheque.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/banking/receipts/{receiptId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "AR")).ShouldBe(0m);
        (await RoleAsync(s, "PdcReceivable")).ShouldBe(1000m);
        (await BankBalanceAsync(s)).ShouldBe(0m);
        (await ItemAsync(s, invoiceItem)).GetProperty("status").GetString().ShouldBe("settled");
        var inHand = await owner.GetOkAsync($"/api/v1/banking/cheques/{chequeId}");
        inHand.GetProperty("status").GetString().ShouldBe("in_hand");
        inHand.GetProperty("events").EnumerateArray().Select(static e => e.GetProperty("event").GetString()).ShouldBe(["received"]);
        (await owner.GetOkAsync($"/api/v1/banking/cheques/forecast?companyId={s.CompanyId}&from=2026-10-01&to=2026-10-31")).EnumerateArray().Single().GetProperty("receivedFc").GetDecimal().ShouldBe(1000m);
        await owner.AssertInvariantsAsync();

        // Not before its date; deposited on the 1st: PDC receivable to cheques under collection on one slip.
        (await owner.PostErrorAsync("/api/v1/banking/cheques/deposits", new { companyId = s.CompanyId, bankAccountId = s.Main, chequeIds = new[] { chequeId }, depositDate = "2026-09-28" }, HttpStatusCode.Conflict)).Code.ShouldBe("deposit.before_due");
        var deposit = await owner.PostAsync("/api/v1/banking/cheques/deposits", new { companyId = s.CompanyId, bankAccountId = s.Main, chequeIds = new[] { chequeId }, depositDate = "2026-10-01" });
        deposit.GetProperty("number").GetString().ShouldStartWith("CHD-2026-");
        (deposit.GetProperty("status").GetString(), deposit.GetProperty("total").GetDecimal(), deposit.GetProperty("chequeCount").GetInt32()).ShouldBe(("deposited", 1000m, 1));
        (await RoleAsync(s, "PdcReceivable")).ShouldBe(0m);
        (await RoleAsync(s, "ChequesUnderCollection")).ShouldBe(1000m);
        (await owner.PostErrorAsync($"/api/v1/banking/receipts/{receiptId}/reverse", new { reason = "Too late" }, HttpStatusCode.Conflict)).Code.ShouldBe("cheque.in_progress");
        await owner.AssertInvariantsAsync();

        // Bounced on the 3rd: the bank charges 15 and the customer is re-charged 25. The receipt is reversed, the invoice open again,
        // the cheque's journey unwound, the customer owing the invoice and the fee, and every step kept on the cheque.
        var bounced = await owner.PostAsync($"/api/v1/banking/cheques/{chequeId}/bounce", new { date = "2026-10-03", bankCharge = 15m, customerFee = 25m, reason = "Insufficient funds" }, HttpStatusCode.OK);
        bounced.GetProperty("status").GetString().ShouldBe("bounced");
        bounced.GetProperty("events").EnumerateArray().Select(static e => e.GetProperty("event").GetString()).ShouldBe(["received", "deposited", "bounced"]);
        bounced.GetProperty("events").EnumerateArray().Last().GetProperty("bankCharge").GetDecimal().ShouldBe(15m);
        (await owner.GetOkAsync($"/api/v1/banking/receipts/{receiptId}")).GetProperty("status").GetString().ShouldBe("reversed");
        var reopened = await ItemAsync(s, invoiceItem);
        (reopened.GetProperty("status").GetString(), reopened.GetProperty("remainingTc").GetDecimal()).ShouldBe(("open", 1000m));
        (await RoleAsync(s, "AR")).ShouldBe(1025m);
        (await RoleAsync(s, "ChequesUnderCollection")).ShouldBe(0m);
        (await RoleAsync(s, "PdcReceivable")).ShouldBe(0m);
        (await RoleAsync(s, "BankCharges")).ShouldBe(15m);
        (await RoleAsync(s, "InterestIncome")).ShouldBe(-25m);
        (await BankBalanceAsync(s)).ShouldBe(-15m);
        var fee = await ItemAsync(s, bounced.GetProperty("feeOpenItemId").GetGuid());
        (fee.GetProperty("kind").GetString(), fee.GetProperty("documentType").GetString(), fee.GetProperty("remainingTc").GetDecimal()).ShouldBe(("adjustment", "bank_cheque", 25m));
        (await owner.GetOkAsync($"/api/v1/banking/cheques/deposits/{deposit.GetProperty("id").GetGuid()}")).GetProperty("status").GetString().ShouldBe("bounced");
        await owner.AssertInvariantsAsync();

        // The replacement: a new cheque for the invoice, linked to the bounced one; deposited and cleared into the bank, less a 2 collection fee.
        var replacement = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-10-05", method = "cheque", lines = new[] { new { openItemId = invoiceItem, amount = 1000m } }, cheque = new { chequeNumber = "778950", drawerBank = "Rafidain Bank", dueDate = "2026-10-10", replacesChequeId = chequeId } });
        var replacementId = replacement.GetProperty("cheque").GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/banking/receipts/{replacement.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/banking/cheques/{chequeId}")).GetProperty("replacedById").GetGuid().ShouldBe(replacementId);
        (await ItemAsync(s, invoiceItem)).GetProperty("status").GetString().ShouldBe("settled");
        var secondSlip = await owner.PostAsync("/api/v1/banking/cheques/deposits", new { companyId = s.CompanyId, bankAccountId = s.Main, chequeIds = new[] { replacementId }, depositDate = "2026-10-10" });
        var cleared = await owner.PostAsync($"/api/v1/banking/cheques/deposits/{secondSlip.GetProperty("id").GetGuid()}/clear", new { date = "2026-10-12", bankCharge = 2m }, HttpStatusCode.OK);
        cleared.GetProperty("status").GetString().ShouldBe("cleared");
        (await BankBalanceAsync(s)).ShouldBe(983m);
        (await RoleAsync(s, "ChequesUnderCollection")).ShouldBe(0m);
        (await RoleAsync(s, "BankCharges")).ShouldBe(17m);
        var register = await owner.GetOkAsync($"/api/v1/banking/cheques?companyId={s.CompanyId}&direction=received");
        register.EnumerateArray().Select(static c => c.GetProperty("status").GetString()).OrderBy(static x => x, StringComparer.Ordinal).ShouldBe(["bounced", "cleared"]);
        (await owner.GetOkAsync($"/api/v1/banking/cheques/forecast?companyId={s.CompanyId}&from=2026-10-01&to=2026-10-31")).EnumerateArray().Single().GetProperty("receivedCount").GetInt32().ShouldBe(0);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_issued_cheque_takes_its_number_from_the_book_settles_the_supplier_is_paid_returned_unpaid_and_cancelled()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var expense = await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", dueDate = "2026-11-04", reference = "ST-55", lines = new[] { new { categoryId = s.Office, amount = 600m, description = "Paper" } } });
        await owner.PostAsync($"/api/v1/expenses/{expense.GetProperty("id").GetGuid()}/confirm", new { }, HttpStatusCode.OK);
        var apItem = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).Only().GetProperty("item").GetProperty("id").GetGuid();

        // A cheque book of ten leaves; a payment by cheque takes the first one, due in November: PDC payable, the invoice settled, the bank untouched.
        var book = await owner.PostAsync("/api/v1/banking/cheque-books", new { companyId = s.CompanyId, bankAccountId = s.Main, prefix = "", firstNumber = 1001, lastNumber = 1010 });
        var bookId = book.GetProperty("id").GetGuid();
        book.GetProperty("remaining").GetInt64().ShouldBe(10);
        (await owner.PostErrorAsync("/api/v1/banking/cheque-books", new { companyId = s.CompanyId, bankAccountId = s.Main, prefix = "", firstNumber = 1005, lastNumber = 1020 }, HttpStatusCode.Conflict)).Code.ShouldBe("cheque_book.overlaps");
        var payment = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Supplier, bankAccountId = s.Main, paymentDate = "2026-10-20", method = "cheque", lines = new[] { new { openItemId = apItem, amount = 600m } }, cheque = new { chequeBookId = bookId, dueDate = "2026-11-15" } });
        var paymentId = payment.GetProperty("id").GetGuid();
        var cheque = payment.GetProperty("cheque");
        (cheque.GetProperty("chequeNumber").GetString(), cheque.GetProperty("isPostDated").GetBoolean(), cheque.GetProperty("direction").GetString()).ShouldBe(("1001", true, "issued"));
        var chequeId = cheque.GetProperty("id").GetGuid();
        (await owner.GetOkAsync($"/api/v1/banking/cheque-books?companyId={s.CompanyId}")).EnumerateArray().Single().GetProperty("nextNumber").GetInt64().ShouldBe(1002);
        await owner.PostAsync($"/api/v1/banking/payments/{paymentId}/post", new { }, HttpStatusCode.OK);
        (await RoleAsync(s, "PdcPayable")).ShouldBe(-600m);
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await BankBalanceAsync(s)).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/banking/cheques/forecast?companyId={s.CompanyId}&from=2026-11-01&to=2026-11-30")).EnumerateArray().Single().GetProperty("issuedFc").GetDecimal().ShouldBe(600m);
        await owner.AssertInvariantsAsync();

        // Paid by the bank on its date with a 1 charge; a paid cheque's payment cannot be reversed; returned unpaid two days later with a 3 charge.
        var paid = await owner.PostAsync($"/api/v1/banking/cheques/{chequeId}/pay", new { date = "2026-11-15", bankCharge = 1m }, HttpStatusCode.OK);
        paid.GetProperty("status").GetString().ShouldBe("paid");
        (await RoleAsync(s, "PdcPayable")).ShouldBe(0m);
        (await BankBalanceAsync(s)).ShouldBe(-601m);
        (await owner.PostErrorAsync($"/api/v1/banking/payments/{paymentId}/reverse", new { reason = "Wrong supplier", reversalDate = "2026-11-16" }, HttpStatusCode.Conflict)).Code.ShouldBe("cheque.in_progress");
        await owner.AssertInvariantsAsync();
        var returned = await owner.PostAsync($"/api/v1/banking/cheques/{chequeId}/bounce", new { date = "2026-11-17", bankCharge = 3m, reason = "Signature differs" }, HttpStatusCode.OK);
        returned.GetProperty("status").GetString().ShouldBe("bounced");
        (await RoleAsync(s, "PdcPayable")).ShouldBe(-600m);
        (await BankBalanceAsync(s)).ShouldBe(-4m);
        await owner.AssertInvariantsAsync();

        // Cancelled: the payment is reversed and the supplier's invoice is open again.
        var cancelled = await owner.PostAsync($"/api/v1/banking/cheques/{chequeId}/cancel", new { date = "2026-11-18", reason = "Replaced by a transfer" }, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("cancelled");
        cancelled.GetProperty("events").EnumerateArray().Select(static e => e.GetProperty("event").GetString()).ShouldBe(["issued", "paid", "bounced", "cancelled"]);
        (await owner.GetOkAsync($"/api/v1/banking/payments/{paymentId}")).GetProperty("status").GetString().ShouldBe("reversed");
        (await RoleAsync(s, "PdcPayable")).ShouldBe(0m);
        (await RoleAsync(s, "AP")).ShouldBe(-600m);
        (await RoleAsync(s, "BankCharges")).ShouldBe(4m);
        await owner.AssertInvariantsAsync();
    }
}

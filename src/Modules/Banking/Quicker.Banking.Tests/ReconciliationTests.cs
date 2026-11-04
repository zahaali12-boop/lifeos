using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// Bank reconciliation (roadmap 6.2): a month's statement imported for an account whose books hold a receipt, a
/// payment, a deposited cheque and a payment the bank has not seen; the matcher pairs the receipt and the payment,
/// clears the cheque from the statement line, and books the bank's fee through a rule; the interest line is booked
/// by hand; the report brings the bank's balance to the book's by the outstanding payment; the reconciliation is
/// completed, the movements reconciled for good, and the aging shows what the bank still has to confirm.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReconciliationTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Customer, Guid Supplier, Guid Main, Guid Office);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "REC", legalName = Name("Reconciled Traders", "تجار المطابقة"), country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
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

    private static async Task<Guid> ExpenseItemAsync(Setup s, string date, decimal amount, string reference)
    {
        var expense = await s.Owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = date, dueDate = date, reference, lines = new[] { new { categoryId = s.Office, amount, description = "Paper" } } });
        await s.Owner.PostAsync($"/api/v1/expenses/{expense.GetProperty("id").GetGuid()}/confirm", new { }, HttpStatusCode.OK);
        var items = await s.Owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}");
        return items.EnumerateArray().Select(static i => i.GetProperty("item")).Single(i => i.GetProperty("status").GetString() == "open" && i.GetProperty("remainingTc").GetDecimal() == amount).GetProperty("id").GetGuid();
    }

    private static async Task<decimal> BankBalanceAsync(Setup s) => (await s.Owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}")).GetProperty("balanceTc").GetDecimal();

    private static async Task<JsonElement> ImportAsync(Setup s, string fileName, string content, string? format, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        using var form = new MultipartFormDataContent { { new StringContent(s.Main.ToString()), "bankAccountId" }, { file, "file", fileName } };
        if (format is not null)
        {
            form.Add(new StringContent(format), "format");
        }

        using var response = await s.Owner.PostAsync("/api/v1/banking/reconciliation/statements/import", form, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task A_statement_is_imported_matched_automatically_clears_the_deposited_cheque_books_the_fee_by_rule_and_the_account_reconciles_to_zero()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // The books of October: a transfer received, a transfer paid, a cheque received and deposited, and a payment the bank has not processed.
        var invoice = await InvoiceAsync(s, "INV-1", "2026-10-01", 1000m);
        var receipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-10-02", method = "transfer", reference = "TRF-5501", lines = new[] { new { openItemId = invoice, amount = 1000m } } });
        await owner.PostAsync($"/api/v1/banking/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var paper = await ExpenseItemAsync(s, "2026-10-04", 600m, "ST-55");
        var payment = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Supplier, bankAccountId = s.Main, paymentDate = "2026-10-05", method = "transfer", reference = "TRF-5502", lines = new[] { new { openItemId = paper, amount = 600m } } });
        await owner.PostAsync($"/api/v1/banking/payments/{payment.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var second = await InvoiceAsync(s, "INV-2", "2026-10-06", 500m);
        var chequeReceipt = await owner.PostAsync("/api/v1/banking/receipts", new { companyId = s.CompanyId, partnerId = s.Customer, bankAccountId = s.Main, receiptDate = "2026-10-06", method = "cheque", lines = new[] { new { openItemId = second, amount = 500m } }, cheque = new { chequeNumber = "778899", drawerBank = "Rafidain Bank", dueDate = "2026-10-08" } });
        var chequeId = chequeReceipt.GetProperty("cheque").GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/banking/receipts/{chequeReceipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        await owner.PostAsync("/api/v1/banking/cheques/deposits", new { companyId = s.CompanyId, bankAccountId = s.Main, chequeIds = new[] { chequeId }, depositDate = "2026-10-08" });
        var ink = await ExpenseItemAsync(s, "2026-10-19", 250m, "ST-56");
        var late = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Supplier, bankAccountId = s.Main, paymentDate = "2026-10-20", method = "transfer", reference = "TRF-5503", lines = new[] { new { openItemId = ink, amount = 250m } } });
        await owner.PostAsync($"/api/v1/banking/payments/{late.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        (await BankBalanceAsync(s)).ShouldBe(150m);

        // A rule for the bank's monthly fee, and a mapping for the bank's CSV layout (dates day first).
        (await owner.PostErrorAsync("/api/v1/banking/reconciliation/rules", new { companyId = s.CompanyId, name = Name("Monthly fee", "الرسم الشهري"), accountRole = "AR", direction = "out", conditions = new { descriptionContains = new[] { "FEE" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("reconciliation.role_is_control");
        var rule = await owner.PostAsync("/api/v1/banking/reconciliation/rules", new { companyId = s.CompanyId, name = Name("Monthly fee", "الرسم الشهري"), accountRole = "BankCharges", direction = "out", priority = 10, conditions = new { descriptionContains = new[] { "FEE", "CHARGE" }, amountMax = 50m } });
        rule.GetProperty("conditions").GetProperty("descriptionContains").GetArrayLength().ShouldBe(2);
        var mapping = await owner.PostAsync("/api/v1/banking/reconciliation/mappings", new { name = "Rafidain CSV", settings = new { dateFormat = "dd/MM/yyyy" } });
        mapping.GetProperty("settings").GetProperty("dateFormat").GetString().ShouldBe("dd/MM/yyyy");

        // The bank's statement: the receipt a day later than booked, the payment, the cheque credited, a fee and interest; the closing balance is the bank's.
        const string csv = """
            Date,Description,Reference,Amount
            2026-10-03,Transfer from Al-Noor Markets,TRF-5501,1000.00
            2026-10-05,Payment Stationery House,TRF-5502,-600.00
            2026-10-09,Cheque deposit 778899,,500.00
            2026-10-15,MONTHLY FEE,,-12.50
            2026-10-31,INTEREST CREDIT,,3.25
            """;
        var preview = await owner.PostAsync("/api/v1/banking/reconciliation/preview?fileName=october.csv", new { content = csv }, HttpStatusCode.OK);
        (preview.GetProperty("format").GetString(), preview.GetProperty("lineCount").GetInt32()).ShouldBe(("csv", 5));
        var statement = await ImportAsync(s, "october.csv", csv, null);
        var statementId = statement.GetProperty("id").GetGuid();
        (statement.GetProperty("lineCount").GetInt32(), statement.GetProperty("skippedCount").GetInt32(), statement.GetProperty("unmatchedCount").GetInt32()).ShouldBe((5, 0, 5));
        (statement.GetProperty("fromDate").GetString(), statement.GetProperty("toDate").GetString()).ShouldBe(("2026-10-03", "2026-10-31"));
        // The same file again adds nothing.
        (await ImportAsync(s, "october.csv", csv, null, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("statement.already_imported");

        // The matcher: the transfer in and out by amount near in date, the cheque cleared from its line (cheques under collection to the bank), the fee booked by the rule.
        var matched = await owner.PostAsync($"/api/v1/banking/reconciliation/statements/{statementId}/auto-match", new { }, HttpStatusCode.OK);
        (matched.GetProperty("matched").GetInt32(), matched.GetProperty("created").GetInt32(), matched.GetProperty("chequesCleared").GetInt32(), matched.GetProperty("remaining").GetInt32()).ShouldBe((3, 1, 1, 1));
        (await owner.GetOkAsync($"/api/v1/banking/cheques/{chequeId}")).GetProperty("status").GetString().ShouldBe("cleared");
        (await BankBalanceAsync(s)).ShouldBe(637.50m);
        var detail = await owner.GetOkAsync($"/api/v1/banking/reconciliation/statements/{statementId}");
        var lines = detail.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(static l => l.GetProperty("status").GetString()).ShouldBe(["matched", "matched", "matched", "matched", "unmatched"]);
        lines[0].GetProperty("matches").EnumerateArray().Single().GetProperty("matchKind").GetString().ShouldBe("auto");
        lines[2].GetProperty("matches").EnumerateArray().Single().GetProperty("matchKind").GetString().ShouldBe("cheque");
        lines[3].GetProperty("matches").EnumerateArray().Single().GetProperty("matchKind").GetString().ShouldBe("created");
        await owner.AssertInvariantsAsync();

        // The report before the interest is booked: the bank's balance, less the payment in transit, is the book's plus the unmatched line; the difference is nil but a line is open.
        var report = await owner.GetOkAsync($"/api/v1/banking/reconciliation/report?bankAccountId={s.Main}&asOf=2026-10-31&statementBalance=890.75");
        (report.GetProperty("bookBalance").GetDecimal(), report.GetProperty("outstandingPayments").GetDecimal(), report.GetProperty("depositsInTransit").GetDecimal(), report.GetProperty("unmatchedBankItems").GetDecimal()).ShouldBe((637.50m, 250m, 0m, 3.25m));
        (report.GetProperty("adjustedBankBalance").GetDecimal(), report.GetProperty("difference").GetDecimal(), report.GetProperty("canComplete").GetBoolean()).ShouldBe((640.75m, 0m, false));
        report.GetProperty("outstandingTransactions").EnumerateArray().Single().GetProperty("reference").GetString().ShouldBe("TRF-5503");

        // The interest booked by hand from its line: the bank against interest income, paired at once; a control role is refused; the reconciliation then balances.
        var interestLine = lines[4].GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/banking/reconciliation/lines/{interestLine}/create", new { accountRole = "AR" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("reconciliation.role_is_control");
        var created = await owner.PostAsync($"/api/v1/banking/reconciliation/lines/{interestLine}/create", new { accountRole = "InterestIncome", description = "October interest" }, HttpStatusCode.OK);
        created.GetProperty("status").GetString().ShouldBe("matched");
        created.GetProperty("matches").EnumerateArray().Single().GetProperty("transactionKind").GetString().ShouldBe("interest");
        (await BankBalanceAsync(s)).ShouldBe(640.75m);
        var open = await owner.PostAsync("/api/v1/banking/reconciliation/reconciliations", new { bankAccountId = s.Main, asOf = "2026-10-31", statementBalance = 890.75m });
        open.GetProperty("number").GetString().ShouldStartWith("REC-2026-");
        (open.GetProperty("status").GetString(), open.GetProperty("difference").GetDecimal(), open.GetProperty("outstandingPayments").GetDecimal()).ShouldBe(("completed", 0m, 250m));
        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/transactions?bankAccountId={s.Main}")).EnumerateArray().Single().GetProperty("reference").GetString().ShouldBe("TRF-5503");
        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/statements/{statementId}")).GetProperty("status").GetString().ShouldBe("reconciled");
        (await owner.PostErrorAsync($"/api/v1/banking/reconciliation/lines/{lines[0].GetProperty("id").GetGuid()}/unmatch", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("reconciliation.completed");
        await owner.AssertInvariantsAsync();

        // A wrong statement balance leaves the reconciliation open; a later one cannot be recorded before this date.
        (await owner.PostErrorAsync("/api/v1/banking/reconciliation/reconciliations", new { bankAccountId = s.Main, asOf = "2026-10-15", statementBalance = 400m }, HttpStatusCode.Conflict)).Code.ShouldBe("reconciliation.later_completed");
        var november = await owner.PostAsync("/api/v1/banking/reconciliation/reconciliations", new { bankAccountId = s.Main, asOf = "2026-11-30", statementBalance = 900m });
        (november.GetProperty("status").GetString(), november.GetProperty("difference").GetDecimal()).ShouldBe(("open", 9.25m));
        (await owner.PostErrorAsync($"/api/v1/banking/reconciliation/reconciliations/{november.GetProperty("id").GetGuid()}/complete", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("reconciliation.not_balanced");

        // The aging at the end of November: the payment of 20 October has waited 41 days.
        var aging = (await owner.GetOkAsync($"/api/v1/banking/reconciliation/aging?companyId={s.CompanyId}&asOf=2026-11-30")).EnumerateArray().Single();
        (aging.GetProperty("count").GetInt32(), aging.GetProperty("current").GetDecimal(), aging.GetProperty("days31To60").GetDecimal(), aging.GetProperty("total").GetDecimal()).ShouldBe((1, 0m, -250m, -250m));
        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/reconciliations?bankAccountId={s.Main}")).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Lines_are_matched_by_hand_to_several_movements_excluded_and_unmatched_and_a_statement_in_use_cannot_be_deleted()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var first = await ExpenseItemAsync(s, "2026-10-04", 100m, "ST-61");
        var p1 = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Supplier, bankAccountId = s.Main, paymentDate = "2026-10-10", method = "transfer", reference = "A-1", lines = new[] { new { openItemId = first, amount = 100m } } });
        await owner.PostAsync($"/api/v1/banking/payments/{p1.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        var secondItem = await ExpenseItemAsync(s, "2026-10-04", 40m, "ST-62");
        var p2 = await owner.PostAsync("/api/v1/banking/payments", new { companyId = s.CompanyId, partnerId = s.Supplier, bankAccountId = s.Main, paymentDate = "2026-10-11", method = "transfer", reference = "A-2", lines = new[] { new { openItemId = secondItem, amount = 40m } } });
        await owner.PostAsync($"/api/v1/banking/payments/{p2.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);

        // The bank booked both as one debit of 140 on the 12th, and a duplicate line of its own.
        var statement = await ImportAsync(s, "batch.csv", "Date,Description,Amount\n2026-10-12,Batch payment,-140.00\n2026-10-12,Batch payment (reversed by bank),-140.00\n", "csv");
        var statementId = statement.GetProperty("id").GetGuid();
        statement.GetProperty("lineCount").GetInt32().ShouldBe(2);
        var lineIds = statement.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("id").GetGuid()).ToList();
        var movements = (await owner.GetOkAsync($"/api/v1/banking/reconciliation/transactions?bankAccountId={s.Main}")).EnumerateArray().Select(static t => t.GetProperty("id").GetGuid()).ToList();
        movements.Count.ShouldBe(2);
        (await owner.PostErrorAsync($"/api/v1/banking/reconciliation/lines/{lineIds[0]}/match", new { bankTransactionIds = new[] { movements[0] } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("reconciliation.amount_mismatch");
        var matched = await owner.PostAsync($"/api/v1/banking/reconciliation/lines/{lineIds[0]}/match", new { bankTransactionIds = movements }, HttpStatusCode.OK);
        matched.GetProperty("matches").GetArrayLength().ShouldBe(2);
        (await owner.PostErrorAsync($"/api/v1/banking/reconciliation/lines/{lineIds[1]}/match", new { bankTransactionIds = movements }, HttpStatusCode.Conflict)).Code.ShouldBe("reconciliation.transaction_taken");
        using (var inUse = await owner.DeleteAsync($"/api/v1/banking/reconciliation/statements/{statementId}", TestContext.Current.CancellationToken))
        {
            inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            JsonDocument.Parse(await inUse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("code").GetString().ShouldBe("statement.in_use");
        }

        // The bank's duplicate is set aside; the report then balances with nothing in transit.
        var excluded = await owner.PostAsync($"/api/v1/banking/reconciliation/lines/{lineIds[1]}/exclude", new { reason = "Bank's own duplicate" }, HttpStatusCode.OK);
        (excluded.GetProperty("status").GetString(), excluded.GetProperty("excludeReason").GetString()).ShouldBe(("excluded", "Bank's own duplicate"));
        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/lines?bankAccountId={s.Main}")).GetArrayLength().ShouldBe(0);
        var report = await owner.GetOkAsync($"/api/v1/banking/reconciliation/report?bankAccountId={s.Main}&asOf=2026-10-31&statementBalance=-140");
        (report.GetProperty("difference").GetDecimal(), report.GetProperty("canComplete").GetBoolean()).ShouldBe((0m, true));

        // Unmatched again: both movements are open and the statement can be removed; the excluded line comes back too.
        var unmatched = await owner.PostAsync($"/api/v1/banking/reconciliation/lines/{lineIds[0]}/unmatch", new { }, HttpStatusCode.OK);
        unmatched.GetProperty("status").GetString().ShouldBe("unmatched");
        (await owner.PostAsync($"/api/v1/banking/reconciliation/lines/{lineIds[1]}/include", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("unmatched");
        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/transactions?bankAccountId={s.Main}")).GetArrayLength().ShouldBe(2);
        using (var deleted = await owner.DeleteAsync($"/api/v1/banking/reconciliation/statements/{statementId}", TestContext.Current.CancellationToken))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await owner.GetOkAsync($"/api/v1/banking/reconciliation/statements?bankAccountId={s.Main}")).GetArrayLength().ShouldBe(0);
        await owner.AssertInvariantsAsync();
    }
}

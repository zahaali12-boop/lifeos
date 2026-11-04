using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Expenses.Tests;

/// <summary>
/// Expenses (founder programme item 2, roadmap 6.4, A-174): a supplier bill recorded with its details and date,
/// confirmed into an accrual (the categories' accounts and input tax against the payable) and paid on its own date;
/// an employee claim owed to the employee; petty cash vouchers spending a fund that is replenished from the bank;
/// category policies and an approval workflow holding an expense until finance decides. The harness runs after each.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ExpenseTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId, string Email);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Supplier, Guid Employee, Guid Main, Guid Cash, Guid Office, Guid Travel, Guid Utilities, Guid Standard, Guid NonRecoverable);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });

        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        var domestic = groups.EnumerateArray().Single(static g => g.GetProperty("kind").GetString() == "partner" && g.GetProperty("code").GetString() == "DOMESTIC").GetProperty("id").GetGuid();
        var standard = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        var nonRecoverable = (await owner.PostAsync($"/api/v1/tax/regimes/{regimeId}/codes", new
        {
            code = "SA-NR",
            name = Name("Standard rate, not recoverable", "النسبة الأساسية، غير قابلة للاسترداد"),
            kind = "vat",
            treatment = "standard",
            isRecoverable = false,
            rates = new[] { new { validFrom = "2020-07-01", ratePct = 15m } },
            purchaseBaseBox = "7",
        })).GetProperty("id").GetGuid();

        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "RIYADH-IT", legalName = Name("Riyadh IT Supplies", "الرياض لتقنية المعلومات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "SAR", taxGroupId = domestic });
        var employee = (await owner.PostAsync("/api/v1/partners", new { code = "EMP-ALI", legalName = Name("Ali Hassan", "علي حسن"), kind = "person", isEmployee = true })).GetProperty("id").GetGuid();

        var main = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main account", "الحساب الرئيسي"), kind = "bank", currency = "SAR" })).GetProperty("id").GetGuid();
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Office cash box", "صندوق المكتب"), kind = "cash", currency = "SAR" })).GetProperty("id").GetGuid();

        var office = (await owner.PostAsync("/api/v1/expenses/categories", new { code = "OFFICE", name = Name("Office supplies", "مستلزمات مكتبية"), accountCode = "6150", taxCodeId = standard })).GetProperty("id").GetGuid();
        var travel = (await owner.PostAsync("/api/v1/expenses/categories", new { code = "TRAVEL", name = Name("Travel", "سفر"), accountCode = "6140", taxCodeId = nonRecoverable, receiptRequired = true })).GetProperty("id").GetGuid();
        var utilities = (await owner.PostAsync("/api/v1/expenses/categories", new { code = "UTIL", name = Name("Utilities", "مرافق"), accountCode = "6120", taxCodeId = standard, maxAmount = 1000m })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/expenses/categories", new { code = "office", name = Name("Again", "مرة أخرى"), accountCode = "6150" }, HttpStatusCode.Conflict)).Code.ShouldBe("expense_category.code_taken");
        return new Setup(ws, owner, companyId, supplier, employee, main, cash, office, travel, utilities, standard, nonRecoverable);
    }

    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid(), email);
    }

    private async Task<T> QueryAsync<T>(Setup s, string sql, object? extra = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<T>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = (extra as Guid?) ?? Guid.Empty }) ?? default!;
    }

    private Task<decimal> RoleAsync(Setup s, string role) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private Task<decimal> AccountAsync(Setup s, string code) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l JOIN app.gl_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.account_id WHERE l.tenant_id = @t AND l.company_id = @c AND a.code = '{code}'");

    private static object Line(Guid categoryId, decimal amount, string? description = null, Guid? taxCodeId = null) => new { categoryId, amount, description, taxCodeId };

    private static (decimal Net, decimal Tax, decimal Gross) Totals(JsonElement e) => (e.GetProperty("totalNet").GetDecimal(), e.GetProperty("totalTax").GetDecimal(), e.GetProperty("totalGross").GetDecimal());

    [Fact]
    public async Task A_supplier_expense_is_recorded_with_its_date_confirmed_into_an_accrual_and_paid_on_its_payment_dates()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Recorded with its details: the draft already shows the tax the engine will post.
        var draft = await owner.PostAsync("/api/v1/expenses", new
        {
            companyId = s.CompanyId,
            kind = "supplier",
            partnerId = s.Supplier,
            expenseDate = "2026-10-05",
            dueDate = "2026-11-04",
            reference = "INV-7781",
            description = "October stationery and the Jeddah trip",
            lines = new[] { Line(s.Office, 1000m, "Stationery"), Line(s.Travel, 500m, "Jeddah trip") },
        });
        var id = draft.GetProperty("id").GetGuid();
        draft.GetProperty("status").GetString().ShouldBe("draft");
        Totals(draft).ShouldBe((1500m, 225m, 1725m), "15 % recoverable on the stationery, 15 % non-recoverable on the trip");
        draft.GetProperty("paymentStatus").GetString().ShouldBe("n/a");
        draft.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", lines = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.lines_required");
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", dueDate = "2026-10-01", lines = new[] { Line(s.Office, 10m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.due_before_date");
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", lines = new[] { Line(s.Office, 10.001m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.amount_precision");

        // Confirming posts the accrual on the expense date: Dr 6150 1000, Dr 6140 575 (its VAT is not recoverable), Dr input tax 150 / Cr AP 1725.
        var confirmed = await owner.PostAsync($"/api/v1/expenses/{id}/confirm", new { }, HttpStatusCode.OK);
        confirmed.GetProperty("status").GetString().ShouldBe("confirmed");
        confirmed.GetProperty("number").GetString().ShouldBe("EXP-2026-00001");
        confirmed.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (confirmed.GetProperty("paymentStatus").GetString(), confirmed.GetProperty("paidTc").GetDecimal(), confirmed.GetProperty("remainingTc").GetDecimal()).ShouldBe(("unpaid", 0m, 1725m));
        var lines = confirmed.GetProperty("lines").EnumerateArray().ToList();
        (lines[0].GetProperty("taxAmount").GetDecimal(), lines[0].GetProperty("taxRecoverable").GetBoolean()).ShouldBe((150m, true));
        (lines[1].GetProperty("taxAmount").GetDecimal(), lines[1].GetProperty("taxRecoverable").GetBoolean()).ShouldBe((75m, false));
        (await AccountAsync(s, "6150")).ShouldBe(1000m);
        (await AccountAsync(s, "6140")).ShouldBe(575m);
        (await RoleAsync(s, "InputTax")).ShouldBe(150m);
        (await RoleAsync(s, "AP")).ShouldBe(-1725m);
        (await QueryAsync<long>(s, "SELECT count(*) FROM app.tax_entries WHERE tenant_id = @t AND company_id = @c AND source_document_id = @r", id)).ShouldBe(2L);

        // The payable: one open item on the supplier, due on the due date, settled by payments on their own dates.
        var item = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).Only().GetProperty("item");
        (item.GetProperty("documentType").GetString(), item.GetProperty("kind").GetString(), item.GetProperty("documentNumber").GetString(), item.GetProperty("dueDate").GetString(), item.GetProperty("remainingTc").GetDecimal()).ShouldBe(("expense", "invoice", "EXP-2026-00001", "2026-11-04", 1725m));
        (await owner.PutErrorAsync($"/api/v1/expenses/{id}", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", lines = new[] { Line(s.Office, 10m) } }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.not_draft");
        (await owner.PostErrorAsync($"/api/v1/expenses/{id}/confirm", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.not_draft");

        var first = await owner.PostAsync($"/api/v1/expenses/{id}/pay", new { bankAccountId = s.Main, paymentDate = "2026-10-20", amount = 725m }, HttpStatusCode.OK);
        first.GetProperty("paymentNumber").GetString().ShouldNotBeNullOrEmpty();
        var afterFirst = first.GetProperty("expense");
        (afterFirst.GetProperty("paymentStatus").GetString(), afterFirst.GetProperty("paidTc").GetDecimal(), afterFirst.GetProperty("remainingTc").GetDecimal()).ShouldBe(("partial", 725m, 1000m));
        (await owner.PostErrorAsync($"/api/v1/expenses/{id}/pay", new { bankAccountId = s.Main, paymentDate = "2026-11-03", amount = 5000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.payment_amount");
        (await owner.PostErrorAsync($"/api/v1/expenses/{id}/reverse", new { reason = "Wrong supplier" }, HttpStatusCode.Conflict)).Code.ShouldBe("payables.item_settled");

        var second = await owner.PostAsync($"/api/v1/expenses/{id}/pay", new { bankAccountId = s.Main, paymentDate = "2026-11-03" }, HttpStatusCode.OK);
        (second.GetProperty("expense").GetProperty("paymentStatus").GetString(), second.GetProperty("expense").GetProperty("remainingTc").GetDecimal()).ShouldBe(("paid", 0m));
        (await owner.PostErrorAsync($"/api/v1/expenses/{id}/pay", new { bankAccountId = s.Main }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.already_paid");
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}/transactions")).GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-1725m);
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/payables/open-items/{item.GetProperty("id").GetGuid()}")).GetProperty("item").GetProperty("status").GetString().ShouldBe("settled");

        // The list carries the payment state; a draft can be deleted, a confirmed expense cannot.
        var listed = (await owner.GetOkAsync($"/api/v1/expenses?companyId={s.CompanyId}&status=confirmed")).Only();
        (listed.GetProperty("number").GetString(), listed.GetProperty("paymentStatus").GetString()).ShouldBe(("EXP-2026-00001", "paid"));
        var scratch = (await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-06", lines = new[] { Line(s.Office, 40m) } })).GetProperty("id").GetGuid();
        await owner.DeleteOkAsync($"/api/v1/expenses/{scratch}");
        (await owner.GetAsync(new Uri($"/api/v1/expenses/{scratch}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync(new Uri($"/api/v1/expenses/{id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Petty_cash_vouchers_spend_a_fund_replenished_from_the_bank_and_an_employee_claim_is_owed_to_the_employee()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // A fund sits on a cash box, never on a bank account.
        (await owner.PostErrorAsync("/api/v1/expenses/funds", new { companyId = s.CompanyId, code = "PC-X", name = Name("Wrong", "خطأ"), bankAccountId = s.Main, floatAmount = 100m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("fund.bank_account_kind");
        var fund = await owner.PostAsync("/api/v1/expenses/funds", new { companyId = s.CompanyId, code = "PC-RUH", name = Name("Riyadh office petty cash", "الصندوق النثري لمكتب الرياض"), bankAccountId = s.Cash, floatAmount = 2000m });
        var fundId = fund.GetProperty("id").GetGuid();
        (fund.GetProperty("currency").GetString(), fund.GetProperty("balance").GetDecimal(), fund.GetProperty("toReplenish").GetDecimal()).ShouldBe(("SAR", 0m, 2000m));

        // Replenished from the bank: Dr cash 2000 / Cr bank 2000, both movements in the bank subledger.
        var replenishment = await owner.PostAsync($"/api/v1/expenses/funds/{fundId}/replenish", new { fromBankAccountId = s.Main, amount = 2000m, replenishedOn = "2026-10-01" });
        (replenishment.GetProperty("number").GetString(), replenishment.GetProperty("status").GetString()).ShouldBe(("PCR-2026-00001", "posted"));
        replenishment.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await owner.PostErrorAsync($"/api/v1/expenses/funds/{fundId}/replenish", new { fromBankAccountId = s.Cash, amount = 10m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("fund.same_account");
        var topped = await owner.GetOkAsync($"/api/v1/expenses/funds/{fundId}");
        (topped.GetProperty("balance").GetDecimal(), topped.GetProperty("toReplenish").GetDecimal()).ShouldBe((2000m, 0m));
        (await RoleAsync(s, "Cash")).ShouldBe(2000m);
        (await RoleAsync(s, "Bank")).ShouldBe(-2000m);

        // A voucher: recorded against the fund, confirmed, it spends the cash box at once; there is nothing to pay later.
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "petty_cash", expenseDate = "2026-10-07", lines = new[] { Line(s.Office, 120m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.fund_required");
        var voucher = await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "petty_cash", fundId, expenseDate = "2026-10-07", reference = "RCPT-19", lines = new[] { Line(s.Office, 120m, "Printer paper") } });
        var voucherId = voucher.GetProperty("id").GetGuid();
        Totals(voucher).ShouldBe((120m, 18m, 138m));
        var paidOut = await owner.PostAsync($"/api/v1/expenses/{voucherId}/confirm", new { }, HttpStatusCode.OK);
        (paidOut.GetProperty("status").GetString(), paidOut.GetProperty("number").GetString(), paidOut.GetProperty("paymentStatus").GetString()).ShouldBe(("confirmed", "EXP-2026-00001", "n/a"));
        var spent = await owner.GetOkAsync($"/api/v1/expenses/funds/{fundId}");
        (spent.GetProperty("balance").GetDecimal(), spent.GetProperty("toReplenish").GetDecimal()).ShouldBe((1862m, 138m));
        (await RoleAsync(s, "Cash")).ShouldBe(1862m);
        (await AccountAsync(s, "6150")).ShouldBe(120m);
        (await RoleAsync(s, "InputTax")).ShouldBe(18m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Cash}/transactions")).GetArrayLength().ShouldBe(2);
        (await owner.PostErrorAsync($"/api/v1/expenses/{voucherId}/pay", new { bankAccountId = s.Main }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.paid_from_fund");

        // Reversing the voucher puts the cash back, mirrors the tax and the journal, on the reversal date.
        (await owner.PostErrorAsync($"/api/v1/expenses/{voucherId}/reverse", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.reason_required");
        var reversed = await owner.PostAsync($"/api/v1/expenses/{voucherId}/reverse", new { reason = "Receipt belongs to another company", reversalDate = "2026-10-08" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("reversalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await owner.GetOkAsync($"/api/v1/expenses/funds/{fundId}")).GetProperty("balance").GetDecimal().ShouldBe(2000m);
        (await RoleAsync(s, "InputTax")).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Cash}/transactions")).GetArrayLength().ShouldBe(3);

        // An employee claim is owed to the employee until it is paid; paying needs the employee registered as a payee of the company.
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Employee, expenseDate = "2026-10-12", lines = new[] { Line(s.Travel, 300m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.partner_not_supplier");
        var claim = await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "employee", partnerId = s.Employee, expenseDate = "2026-10-12", description = "Taxi and hotel, Dammam visit", lines = new[] { Line(s.Travel, 300m, "Dammam visit") } });
        var claimId = claim.GetProperty("id").GetGuid();
        Totals(claim).ShouldBe((300m, 45m, 345m));
        var owed = await owner.PostAsync($"/api/v1/expenses/{claimId}/confirm", new { }, HttpStatusCode.OK);
        (owed.GetProperty("status").GetString(), owed.GetProperty("number").GetString(), owed.GetProperty("remainingTc").GetDecimal()).ShouldBe(("confirmed", "EXP-2026-00002", 345m));
        (await AccountAsync(s, "6140")).ShouldBe(345m, "non-recoverable VAT stays in the cost");
        var owedItem = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Employee}")).Only().GetProperty("item");
        (owedItem.GetProperty("documentType").GetString(), owedItem.GetProperty("remainingTc").GetDecimal()).ShouldBe(("expense", 345m));
        (await owner.PostErrorAsync($"/api/v1/expenses/{claimId}/pay", new { bankAccountId = s.Main, paymentDate = "2026-10-15" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier.not_registered");
        await owner.PutAsync($"/api/v1/partners/{s.Employee}/supplier-accounts/{s.CompanyId}", new { currency = "SAR" });
        var settled = await owner.PostAsync($"/api/v1/expenses/{claimId}/pay", new { bankAccountId = s.Main, paymentDate = "2026-10-15", method = "transfer", reference = "Payroll run 10/2026" }, HttpStatusCode.OK);
        settled.GetProperty("expense").GetProperty("paymentStatus").GetString().ShouldBe("paid");
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await RoleAsync(s, "Bank")).ShouldBe(-2345m);

        // Reversing the replenishment takes the cash back out of the box.
        var undone = await owner.PostAsync($"/api/v1/expenses/replenishments/{replenishment.GetProperty("id").GetGuid()}/reverse", new { reason = "Posted to the wrong fund" }, HttpStatusCode.OK);
        undone.GetProperty("status").GetString().ShouldBe("reversed");
        (await owner.GetOkAsync($"/api/v1/expenses/funds/{fundId}")).GetProperty("balance").GetDecimal().ShouldBe(0m);
        (await RoleAsync(s, "Cash")).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/expenses/funds/{fundId}/replenishments")).Only().GetProperty("status").GetString().ShouldBe("reversed");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Category_policies_refuse_a_line_over_its_limit_and_an_approval_workflow_holds_an_expense_until_finance_decides()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        (await owner.PostErrorAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", lines = new[] { Line(s.Utilities, 1500m) } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("expense.over_limit");

        var finance = await InviteAsync(owner, s.Ws, "finance", "workflow.request.read", "expenses.expense.read");
        var definition = await owner.PostAsync("/api/v1/workflow/definitions", new
        {
            entityType = "expense",
            trigger = "on_submit",
            name = Name("Expense approval", "اعتماد المصروفات"),
            rules = new[] { new { name = Name("Large expenses", "المصروفات الكبيرة"), condition = "amount_in('SAR') > 500", steps = new[] { new { name = Name("Finance", "المالية"), approverKind = "users", approvers = new { membershipIds = new[] { finance.MembershipId } }, mode = "any" } } } },
        });
        await owner.PostAsync($"/api/v1/workflow/definitions/{definition.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);

        // Below the threshold: posted at once.
        var small = (await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-05", lines = new[] { Line(s.Office, 200m) } })).GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/expenses/{small}/confirm", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("confirmed");

        // Above it: held, unchangeable, until finance approves; then posted with the next number.
        var large = (await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-06", lines = new[] { Line(s.Office, 800m) } })).GetProperty("id").GetGuid();
        var pending = await owner.PostAsync($"/api/v1/expenses/{large}/confirm", new { }, HttpStatusCode.OK);
        pending.GetProperty("status").GetString().ShouldBe("pending_approval");
        pending.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        var requestId = pending.GetProperty("approvalRequestId").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/expenses/{large}", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-06", lines = new[] { Line(s.Office, 10m) } }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.not_draft");
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Within budget." }, HttpStatusCode.OK);
        var approved = await owner.GetOkAsync($"/api/v1/expenses/{large}");
        (approved.GetProperty("status").GetString(), approved.GetProperty("number").GetString()).ShouldBe(("confirmed", "EXP-2026-00002"));
        approved.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, "AP")).ShouldBe(-1150m);

        // Rejected: back to its author with the reason; edited below the threshold it goes through at once.
        var rejected = (await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-07", lines = new[] { Line(s.Office, 900m) } })).GetProperty("id").GetGuid();
        var held = await owner.PostAsync($"/api/v1/expenses/{rejected}/confirm", new { }, HttpStatusCode.OK);
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{held.GetProperty("approvalRequestId").GetGuid()}/reject", new { comment = "No budget this month." }, HttpStatusCode.OK);
        var sentBack = await owner.GetOkAsync($"/api/v1/expenses/{rejected}");
        (sentBack.GetProperty("status").GetString(), sentBack.GetProperty("rejectionReason").GetString()).ShouldBe(("rejected", "No budget this month."));
        var edited = await owner.PutAsync($"/api/v1/expenses/{rejected}", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-07", lines = new[] { Line(s.Office, 400m) } });
        edited.GetProperty("status").GetString().ShouldBe("draft");
        (await owner.PostAsync($"/api/v1/expenses/{rejected}/confirm", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("confirmed");

        // Cancelled while pending: the request is withdrawn and nothing is posted.
        var dropped = (await owner.PostAsync("/api/v1/expenses", new { companyId = s.CompanyId, kind = "supplier", partnerId = s.Supplier, expenseDate = "2026-10-08", lines = new[] { Line(s.Office, 700m) } })).GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/expenses/{dropped}/confirm", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("pending_approval");
        (await owner.PostAsync($"/api/v1/expenses/{dropped}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");
        (await owner.PostErrorAsync($"/api/v1/expenses/{dropped}/confirm", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("expense.not_draft");
        (await owner.GetOkAsync($"/api/v1/expenses?companyId={s.CompanyId}&status=confirmed")).GetArrayLength().ShouldBe(3);

        await owner.AssertInvariantsAsync();
    }
}

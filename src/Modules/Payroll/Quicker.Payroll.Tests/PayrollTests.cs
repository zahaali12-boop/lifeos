using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Payroll.Tests;

/// <summary>
/// Payroll and HR, simple (founder programme item 8, A-181): pay components on accounts through posting rules, employees
/// hired over partners with the employee role, leave against the entitlement; a month's run calculated with the basic and
/// the fixed allowances prorated over thirty days for unpaid leave and a mid-month hire, percentages of the basic and of
/// the gross, deductions and employer contributions; the run posted (salaries by component, the liabilities withheld,
/// each employee's net as a payable) and paid from the bank one employee at a time; the payslip printed; an approval
/// workflow holding a run until finance decides; a posted run reversed. The harness runs after each scenario.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PayrollTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Bank, Guid Noura, Guid Faisal, Guid Housing, Guid Transport, Guid GosiEmployee, Guid GosiEmployer, Guid Advance);

    private sealed record Member(HttpClient Client, Guid MembershipId, string Email);

    private async Task<Setup> SetUpAsync(string prefix = "RYD")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = prefix, legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", registrationNumbers = new { crn = "1010010000" } })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main bank", "البنك الرئيسي"), kind = "bank", currency = "SAR" })).GetProperty("id").GetGuid();

        // Two people with the employee role; Noura can be paid from the start, Faisal only once he has a supplier account.
        var noura = (await owner.PostAsync("/api/v1/partners", new { code = "NOURA", legalName = Name("Noura Al-Harbi", "نورة الحربي"), kind = "person", isEmployee = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{noura}/supplier-accounts/{companyId}", new { currency = "SAR" });
        var faisal = (await owner.PostAsync("/api/v1/partners", new { code = "FAISAL", legalName = Name("Faisal Al-Qahtani", "فيصل القحطاني"), kind = "person", isEmployee = true })).GetProperty("id").GetGuid();

        // The components: BASIC comes with the company; the rest name their accounts by code.
        var seeded = await owner.GetOkAsync($"/api/v1/payroll/components?companyId={companyId}");
        seeded.EnumerateArray().Single().GetProperty("code").GetString().ShouldBe("BASIC");
        seeded.EnumerateArray().Single().GetProperty("expenseAccountCode").GetString().ShouldBe("6100", "the chart's salaries account carries the PayrollExpense role");
        var housing = (await owner.PostAsync("/api/v1/payroll/components", new { companyId, code = "HOUSING", name = Name("Housing allowance", "بدل سكن"), kind = "earning", calculation = "percent_of_basic", defaultRatePct = 25m, appliesToAll = true, expenseAccountCode = "6100", sortOrder = 1 })).GetProperty("id").GetGuid();
        var transport = (await owner.PostAsync("/api/v1/payroll/components", new { companyId, code = "TRANSPORT", name = Name("Transport allowance", "بدل نقل"), kind = "earning", calculation = "fixed", defaultAmount = 300m, appliesToAll = true, expenseAccountCode = "6100", sortOrder = 2 })).GetProperty("id").GetGuid();
        var gosiEmployee = (await owner.PostAsync("/api/v1/payroll/components", new { companyId, code = "GOSI-EE", name = Name("Social insurance, employee", "التأمينات الاجتماعية، الموظف"), kind = "deduction", calculation = "percent_of_basic", defaultRatePct = 9.75m, liabilityAccountCode = "2195", sortOrder = 10 })).GetProperty("id").GetGuid();
        var gosiEmployer = (await owner.PostAsync("/api/v1/payroll/components", new { companyId, code = "GOSI-ER", name = Name("Social insurance, employer", "التأمينات الاجتماعية، صاحب العمل"), kind = "employer", calculation = "percent_of_basic", defaultRatePct = 11.75m, expenseAccountCode = "6100", liabilityAccountCode = "2195", sortOrder = 20 })).GetProperty("id").GetGuid();
        var advance = (await owner.PostAsync("/api/v1/payroll/components", new { companyId, code = "ADVANCE", name = Name("Salary advance recovery", "استرداد سلفة"), kind = "deduction", calculation = "fixed", liabilityAccountCode = "2190", sortOrder = 11 })).GetProperty("id").GetGuid();
        return new Setup(ws, owner, companyId, bank, noura, faisal, housing, transport, gosiEmployee, gosiEmployer, advance);
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

    private async Task<decimal> RoleAsync(Setup s, string role)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = @role", new { t = s.Ws.TenantId, c = s.CompanyId, role });
    }

    private async Task<decimal> AccountAsync(Setup s, string code)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l JOIN app.gl_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.account_id WHERE l.tenant_id = @t AND l.company_id = @c AND a.code = @code", new { t = s.Ws.TenantId, c = s.CompanyId, code });
    }

    private static JsonElement Line(JsonElement run, string employeeNumber) => run.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("employeeNumber").GetString() == employeeNumber);

    private static decimal Item(JsonElement line, string code) => line.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("componentCode").GetString() == code).GetProperty("amount").GetDecimal();

    [Fact]
    public async Task A_month_is_calculated_with_prorating_posted_by_component_paid_from_the_bank_and_printed_as_payslips()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Components: a deduction needs a liability account, an expense account must be an expense; the mapping is a posting rule keyed by the component.
        (await owner.PostErrorAsync("/api/v1/payroll/components", new { companyId = s.CompanyId, code = "LOAN", name = Name("Loan", "قرض"), kind = "deduction", calculation = "fixed" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pay_component.liability_account_required");
        (await owner.PostErrorAsync("/api/v1/payroll/components", new { companyId = s.CompanyId, code = "BONUS", name = Name("Bonus", "مكافأة"), kind = "earning", calculation = "fixed", expenseAccountCode = "2195" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pay_component.account_type");
        var profiles = await owner.GetOkAsync($"/api/v1/accounting/companies/{s.CompanyId}/posting-profiles");
        var profile = await owner.GetOkAsync($"/api/v1/accounting/posting-profiles/{profiles.EnumerateArray().Single(static p => p.GetProperty("status").GetString() == "active").GetProperty("id").GetGuid()}");
        profile.GetProperty("rules").EnumerateArray().Count(r => r.GetProperty("payComponentId").ValueKind == JsonValueKind.String).ShouldBe(7, "BASIC, HOUSING, TRANSPORT and GOSI-ER on 6100; GOSI-EE, GOSI-ER on 2195; ADVANCE on 2190");

        // Employees: a partner without the role is refused; numbers run EMP-0001, EMP-0002; Faisal joins mid-month.
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "ACME", legalName = Name("Acme", "أكمي"), isCustomer = true })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/payroll/employees", new { companyId = s.CompanyId, partnerId = customer, hireDate = "2026-01-01", basicSalary = 1000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("employee.partner_not_employee");
        var noura = await owner.PostAsync("/api/v1/payroll/employees", new
        {
            companyId = s.CompanyId,
            partnerId = s.Noura,
            hireDate = "2026-01-01",
            basicSalary = 10000m,
            jobTitle = Name("Accountant", "محاسبة"),
            iban = "SA0380000000608010167519",
            bankName = "Al Rajhi",
            components = new[] { new { componentId = s.GosiEmployee }, new { componentId = s.GosiEmployer } },
        });
        var nouraId = noura.GetProperty("id").GetGuid();
        noura.GetProperty("number").GetString().ShouldBe("EMP-0001");
        noura.GetProperty("canBePaid").GetBoolean().ShouldBeTrue();
        noura.GetProperty("leaveBalance").GetDecimal().ShouldBe(21m);
        var faisal = await owner.PostAsync("/api/v1/payroll/employees", new { companyId = s.CompanyId, partnerId = s.Faisal, hireDate = "2026-09-16", basicSalary = 6000m, paymentMethod = "transfer", components = new[] { new { componentId = s.Advance, amount = 500m } } });
        var faisalId = faisal.GetProperty("id").GetGuid();
        faisal.GetProperty("number").GetString().ShouldBe("EMP-0002");
        faisal.GetProperty("canBePaid").GetBoolean().ShouldBeFalse("no supplier account yet");
        (await owner.PostErrorAsync("/api/v1/payroll/employees", new { companyId = s.CompanyId, partnerId = s.Noura, hireDate = "2026-01-01", basicSalary = 1m }, HttpStatusCode.Conflict)).Code.ShouldBe("employee.already_hired");

        // Leave: three unpaid days and two annual days for Noura in September; overlapping leave is refused.
        var unpaidLeave = (await owner.PostAsync("/api/v1/payroll/leaves", new { employeeId = nouraId, kind = "unpaid", fromDate = "2026-09-07", toDate = "2026-09-09" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/payroll/leaves", new { employeeId = nouraId, kind = "annual", fromDate = "2026-09-14", toDate = "2026-09-15" });
        (await owner.PostErrorAsync("/api/v1/payroll/leaves", new { employeeId = nouraId, kind = "sick", fromDate = "2026-09-15", toDate = "2026-09-16" }, HttpStatusCode.Conflict)).Code.ShouldBe("leave.overlaps");
        (await owner.GetOkAsync($"/api/v1/payroll/employees/{nouraId}")).GetProperty("leaveBalance").GetDecimal().ShouldBe(19m);

        // The run of September: Noura 27 of 30 days (basic 9,000; housing 2,250; transport 270; GOSI 877.50 withheld, 1,057.50 by the employer); Faisal 15 days (basic 3,000; housing 750; transport 150; advance 500 not prorated).
        var run = await owner.PostAsync("/api/v1/payroll/runs", new { companyId = s.CompanyId, year = 2026, month = 9, payDate = "2026-09-28" });
        var runId = run.GetProperty("id").GetGuid();
        run.GetProperty("number").GetString().ShouldBe("PRL-2026-001");
        run.GetProperty("status").GetString().ShouldBe("calculated");
        run.GetProperty("employeesCount").GetInt32().ShouldBe(2);
        var n = Line(run, "EMP-0001");
        n.GetProperty("paidDays").GetDecimal().ShouldBe(27m);
        n.GetProperty("unpaidDays").GetDecimal().ShouldBe(3m);
        n.GetProperty("basicAmount").GetDecimal().ShouldBe(9000m);
        Item(n, "HOUSING").ShouldBe(2250m);
        Item(n, "TRANSPORT").ShouldBe(270m);
        Item(n, "GOSI-EE").ShouldBe(877.50m);
        Item(n, "GOSI-ER").ShouldBe(1057.50m);
        n.GetProperty("gross").GetDecimal().ShouldBe(11520m);
        n.GetProperty("net").GetDecimal().ShouldBe(10642.50m);
        var f = Line(run, "EMP-0002");
        f.GetProperty("paidDays").GetDecimal().ShouldBe(15m);
        f.GetProperty("basicAmount").GetDecimal().ShouldBe(3000m);
        Item(f, "HOUSING").ShouldBe(750m);
        Item(f, "TRANSPORT").ShouldBe(150m);
        Item(f, "ADVANCE").ShouldBe(500m);
        f.GetProperty("gross").GetDecimal().ShouldBe(3900m);
        f.GetProperty("net").GetDecimal().ShouldBe(3400m);
        run.GetProperty("totalGross").GetDecimal().ShouldBe(15420m);
        run.GetProperty("totalDeductions").GetDecimal().ShouldBe(1377.50m);
        run.GetProperty("totalEmployer").GetDecimal().ShouldBe(1057.50m);
        run.GetProperty("totalNet").GetDecimal().ShouldBe(14042.50m);
        (await owner.PostErrorAsync("/api/v1/payroll/runs", new { companyId = s.CompanyId, year = 2026, month = 9 }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.period_exists");

        // No workflow: submitting approves. Posting needs every employee payable; then the journal lands by role and the net opens one payable per employee.
        (await owner.PostAsync($"/api/v1/payroll/runs/{runId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        (await owner.PostErrorAsync($"/api/v1/payroll/runs/{runId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.employee_not_payable");
        await owner.PutAsync($"/api/v1/partners/{s.Faisal}/supplier-accounts/{s.CompanyId}", new { currency = "SAR" });
        var posted = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, "PayrollExpense")).ShouldBe(16477.50m, "gross 15,420 plus the employer's 1,057.50");
        (await RoleAsync(s, "PayrollLiability")).ShouldBe(-2435m, "GOSI withheld 877.50, the employer's 1,057.50 and the advance recovered 500");
        (await AccountAsync(s, "2195")).ShouldBe(-1935m, "social insurance lands on payroll liabilities");
        (await AccountAsync(s, "2190")).ShouldBe(-500m, "the advance recovered lands on employee payables, the account its component names");
        (await RoleAsync(s, "AP")).ShouldBe(-14042.50m);
        var open = await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Noura}");
        open.EnumerateArray().Single().GetProperty("item").GetProperty("documentType").GetString().ShouldBe("payroll_run");
        (await owner.PostErrorAsync($"/api/v1/payroll/runs/{runId}/calculate", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.not_editable");
        (await owner.PostErrorAsync($"/api/v1/payroll/leaves/{unpaidLeave}/cancel", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("leave.period_paid", "the unpaid days were deducted by a posted run");

        // Paid from the bank: one payment per employee, the payables settled, the run paid; paying again finds nothing.
        var paid = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/pay", new { bankAccountId = s.Bank, reference = "SEP-SALARIES" }, HttpStatusCode.OK);
        paid.GetProperty("status").GetString().ShouldBe("paid");
        Line(paid, "EMP-0001").GetProperty("paymentNumber").GetString().ShouldNotBeNullOrEmpty();
        Line(paid, "EMP-0002").GetProperty("paidOn").GetString().ShouldBe("2026-09-28");
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await RoleAsync(s, "Bank")).ShouldBe(-14042.50m);
        (await owner.GetOkAsync($"/api/v1/banking/payments?companyId={s.CompanyId}&partnerId={s.Faisal}")).EnumerateArray().Single().GetProperty("method").GetString().ShouldBe("transfer");
        (await owner.PostErrorAsync($"/api/v1/payroll/runs/{runId}/pay", new { bankAccountId = s.Bank }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.not_posted");
        (await owner.PostErrorAsync($"/api/v1/payroll/runs/{runId}/reverse", new { reason = "Wrong month" }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.not_posted");

        // The payslip prints from the shipped template; the run shows in the list of the year.
        var payslip = await owner.GetTextAsync($"/api/v1/print/preview?documentType=payslip&documentId={Line(paid, "EMP-0001").GetProperty("id").GetGuid()}");
        payslip.ShouldContain("PRL-2026-001-01");
        payslip.ShouldContain("Noura Al-Harbi");
        payslip.ShouldContain("10,642.50");
        payslip.ShouldContain("Housing allowance");
        (await owner.GetOkAsync($"/api/v1/payroll/runs?companyId={s.CompanyId}&year=2026")).EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("paid");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_approval_workflow_holds_a_run_until_finance_decides_and_a_posted_run_is_reversed_and_run_again()
    {
        var s = await SetUpAsync("JED");
        var owner = s.Owner;
        await owner.PutAsync($"/api/v1/partners/{s.Faisal}/supplier-accounts/{s.CompanyId}", new { currency = "SAR" });
        await owner.PostAsync("/api/v1/payroll/employees", new { companyId = s.CompanyId, partnerId = s.Noura, hireDate = "2025-03-01", basicSalary = 8000m, components = new[] { new { componentId = s.GosiEmployee }, new { componentId = s.GosiEmployer } } });
        await owner.PostAsync("/api/v1/payroll/employees", new { companyId = s.CompanyId, partnerId = s.Faisal, hireDate = "2025-06-01", basicSalary = 5000m, terminationDate = "2026-10-15" });

        var finance = await InviteAsync(owner, s.Ws, "finance", "workflow.request.read", "payroll.run.read");
        var definition = await owner.PostAsync("/api/v1/workflow/definitions", new
        {
            entityType = "payroll_run",
            trigger = "on_submit",
            name = Name("Payroll approval", "اعتماد الرواتب"),
            rules = new[] { new { name = Name("Every run", "كل مسير"), condition = "amount_in('SAR') > 500", steps = new[] { new { name = Name("Finance", "المالية"), approverKind = "users", approvers = new { membershipIds = new[] { finance.MembershipId } }, mode = "any" } } } },
        });
        await owner.PostAsync($"/api/v1/workflow/definitions/{definition.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);

        // October: Faisal leaves on the 15th, so he is paid 15 of 30 days; the run waits for finance, who rejects it, then approves the resubmission.
        var run = await owner.PostAsync("/api/v1/payroll/runs", new { companyId = s.CompanyId, year = 2026, month = 10 });
        var runId = run.GetProperty("id").GetGuid();
        Line(run, "EMP-0002").GetProperty("paidDays").GetDecimal().ShouldBe(15m);
        Line(run, "EMP-0002").GetProperty("basicAmount").GetDecimal().ShouldBe(2500m);
        var held = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/submit", new { }, HttpStatusCode.OK);
        held.GetProperty("status").GetString().ShouldBe("pending_approval");
        var requestId = held.GetProperty("approvalRequestId").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/payroll/runs/{runId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("payroll_run.not_approved");
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/reject", new { comment = "Check Faisal's last day." }, HttpStatusCode.OK);
        var rejected = await owner.GetOkAsync($"/api/v1/payroll/runs/{runId}");
        rejected.GetProperty("status").GetString().ShouldBe("calculated");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Check Faisal's last day.");
        var resubmitted = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/submit", new { }, HttpStatusCode.OK);
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{resubmitted.GetProperty("approvalRequestId").GetGuid()}/approve", new { comment = "Confirmed." }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/payroll/runs/{runId}")).GetProperty("status").GetString().ShouldBe("approved");
        var posted = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("status").GetString().ShouldBe("posted");
        var net = posted.GetProperty("totalNet").GetDecimal();
        (await RoleAsync(s, "AP")).ShouldBe(-net);

        // Reversed before payment: the journal mirrored, the payables closed, and the month can be run again; a run not yet posted is cancelled instead.
        var reversed = await owner.PostAsync($"/api/v1/payroll/runs/{runId}/reverse", new { reason = "Faisal's final settlement is handled apart" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("reversalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        (await RoleAsync(s, "AP")).ShouldBe(0m);
        (await RoleAsync(s, "PayrollExpense")).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Noura}&status=open")).GetArrayLength().ShouldBe(0);
        var again = await owner.PostAsync("/api/v1/payroll/runs", new { companyId = s.CompanyId, year = 2026, month = 10 });
        again.GetProperty("number").GetString().ShouldBe("PRL-2026-002");
        (await owner.PostAsync($"/api/v1/payroll/runs/{again.GetProperty("id").GetGuid()}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");
        (await owner.GetOkAsync($"/api/v1/payroll/runs?companyId={s.CompanyId}&status=cancelled")).GetArrayLength().ShouldBe(1);
        await owner.AssertInvariantsAsync();
    }
}

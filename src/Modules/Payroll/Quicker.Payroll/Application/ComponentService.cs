using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Payroll.Contracts;
using Quicker.Payroll.Domain;
using Quicker.Payroll.Persistence;

namespace Quicker.Payroll.Application;

/// <summary>
/// Pay components (A-181): earnings, deductions and employer contributions. Each names the accounts its amounts land on
/// by code; the service keeps them as posting rules (role PayrollExpense or PayrollLiability, keyed by the component)
/// in the company's profile, so runs post by role and the admin sees the mapping among the other rules. A company's
/// first look at its components seeds BASIC, the employee's basic salary, on the salaries account the chart names.
/// </summary>
public sealed class ComponentService(PayrollDbContext db, ICompanyDirectory companies, IChartOfAccounts chart, IPostingRules postingRules, IAuditSink audit, IClock clock)
{
    public const string AuditEntity = "pay_component";
    public const string BasicCode = "BASIC";

    public async Task<IReadOnlyList<PayComponentSummary>> ListAsync(Guid companyId, bool? isActive, CancellationToken cancellationToken)
    {
        await EnsureBasicAsync(companyId, cancellationToken);
        var query = db.Components.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (isActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        return (await query.OrderBy(static c => c.SortOrder).ThenBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<PayComponentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var component = await db.Components.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return component is null ? Error.NotFound(AuditEntity, id) : Map(component);
    }

    public async Task<Result<PayComponentSummary>> SaveAsync(Guid? id, SavePayComponentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Validation.Name(request.Name, "pay_component");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var code = Validation.UpperCode(string.IsNullOrWhiteSpace(request.Code) ? Validation.CodeFromName(name.Value) : request.Code, "pay_component");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var kind = Validation.OneOf(request.Kind, "pay_component.kind", PayComponentKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var calculation = Validation.OneOf(request.Calculation, "pay_component.calculation", PayComponentCalculations.All);
        if (calculation.IsFailure)
        {
            return calculation.Error!;
        }

        if (calculation.Value == PayComponentCalculations.Basic && kind.Value != PayComponentKinds.Earning)
        {
            return Error.Validation("pay_component.basic_is_earning", "The basic salary is an earning.");
        }

        if (request.DefaultAmount < 0m || request.DefaultRatePct is < 0m or > 100m)
        {
            return Error.Validation("pay_component.amount_invalid", "The default amount is zero or above and the rate between 0 and 100 percent.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        // Earnings and employer contributions land on an expense account; deductions and employer contributions on a liability.
        var needsExpense = kind.Value is PayComponentKinds.Earning or PayComponentKinds.Employer;
        var needsLiability = kind.Value is PayComponentKinds.Deduction or PayComponentKinds.Employer;
        var expenseCode = Validation.Trim(request.ExpenseAccountCode);
        var liabilityCode = Validation.Trim(request.LiabilityAccountCode);
        if (needsExpense && expenseCode is null)
        {
            return Error.Validation("pay_component.expense_account_required", "An earning or an employer contribution names the expense account it lands on, by code.");
        }

        if (needsLiability && liabilityCode is null)
        {
            return Error.Validation("pay_component.liability_account_required", "A deduction or an employer contribution names the liability account it is owed on, by code.");
        }

        var chartId = await chart.ChartForCompanyAsync(company.Id, cancellationToken);
        if (chartId is null)
        {
            return Error.Conflict("pay_component.no_chart", "The company has no chart of accounts yet.");
        }

        AccountInfo? expense = null;
        AccountInfo? liability = null;
        if (expenseCode is not null)
        {
            var found = await AccountAsync(chartId.Value, expenseCode, AccountTypes.Expense, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            expense = found.Value;
        }

        if (liabilityCode is not null)
        {
            var found = await AccountAsync(chartId.Value, liabilityCode, AccountTypes.Liability, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            liability = found.Value;
        }

        PayComponent? component = null;
        if (id is { } existing)
        {
            component = await db.Components.SingleOrDefaultAsync(c => c.Id == existing, cancellationToken);
            if (component is null)
            {
                return Error.NotFound(AuditEntity, existing);
            }

            if (component.CompanyId != request.CompanyId)
            {
                return Error.Validation("pay_component.company_fixed", "A component stays in its company.");
            }

            if (component.Code == BasicCode && (code.Value != BasicCode || calculation.Value != PayComponentCalculations.Basic))
            {
                return Error.Validation("pay_component.basic_fixed", "BASIC stays the employee's basic salary.");
            }
        }

        if (await db.Components.AnyAsync(c => c.CompanyId == request.CompanyId && c.Code == code.Value && (component == null || c.Id != component.Id), cancellationToken))
        {
            return Error.Conflict("pay_component.code_taken", $"The company already has a pay component '{code.Value}'.");
        }

        if (calculation.Value == PayComponentCalculations.Basic && await db.Components.AnyAsync(c => c.CompanyId == request.CompanyId && c.Calculation == PayComponentCalculations.Basic && (component == null || c.Id != component.Id), cancellationToken))
        {
            return Error.Conflict("pay_component.basic_exists", "The company already has its basic salary component.");
        }

        var before = component is null ? null : Snapshot(component);
        var now = clock.UtcNow;
        component ??= new PayComponent { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = now };
        component.Code = code.Value;
        component.Name = name.Value;
        component.Kind = kind.Value;
        component.Calculation = calculation.Value;
        component.DefaultAmount = request.DefaultAmount;
        component.DefaultRatePct = request.DefaultRatePct;
        component.AppliesToAll = calculation.Value == PayComponentCalculations.Basic || request.AppliesToAll;
        component.ExpenseAccountCode = expense?.Code;
        component.LiabilityAccountCode = liability?.Code;
        component.SortOrder = request.SortOrder;
        component.IsActive = request.IsActive;
        component.UpdatedAt = now;
        if (id is null)
        {
            db.Components.Add(component);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (expense is not null)
        {
            var ensured = await postingRules.EnsureRuleAsync(company.Id, AccountRoles.PayrollExpense, new PostingKeys(PayComponentId: component.Id), expense.Id, cancellationToken);
            if (ensured.IsFailure)
            {
                return ensured.Error!;
            }
        }

        if (liability is not null)
        {
            var ensured = await postingRules.EnsureRuleAsync(company.Id, AccountRoles.PayrollLiability, new PostingKeys(PayComponentId: component.Id), liability.Id, cancellationToken);
            if (ensured.IsFailure)
            {
                return ensured.Error!;
            }
        }

        await audit.RecordAsync(new AuditEntry(AuditEntity, component.Id, component.Code, id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(component), CompanyId: component.CompanyId), cancellationToken);
        return Map(component);
    }

    /// <summary>The BASIC component of a company, created on first use on the account the chart names for salaries (role PayrollExpense) when the chart has one.</summary>
    public async Task<PayComponent?> EnsureBasicAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var basic = await db.Components.SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Calculation == PayComponentCalculations.Basic, cancellationToken);
        if (basic is not null)
        {
            return basic;
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return null;
        }

        var chartId = await chart.ChartForCompanyAsync(company.Id, cancellationToken);
        var account = chartId is null ? null : (await chart.AccountsForRoleAsync(chartId.Value, AccountRoles.PayrollExpense, cancellationToken)).FirstOrDefault(static a => a.IsActive && !a.IsHeader);
        var now = clock.UtcNow;
        basic = new PayComponent
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            Code = BasicCode,
            Name = LocalizedText.Bilingual("Basic salary", "الراتب الأساسي"),
            Kind = PayComponentKinds.Earning,
            Calculation = PayComponentCalculations.Basic,
            AppliesToAll = true,
            ExpenseAccountCode = account?.Code,
            SortOrder = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Components.Add(basic);
        await db.SaveChangesAsync(cancellationToken);
        if (account is not null)
        {
            await postingRules.EnsureRuleAsync(company.Id, AccountRoles.PayrollExpense, new PostingKeys(PayComponentId: basic.Id), account.Id, cancellationToken);
        }

        await audit.RecordAsync(new AuditEntry(AuditEntity, basic.Id, basic.Code, AuditActions.Created, After: Snapshot(basic), CompanyId: companyId), cancellationToken);
        return basic;
    }

    private async Task<Result<AccountInfo>> AccountAsync(Guid chartId, string code, string expectedType, CancellationToken cancellationToken)
    {
        var account = await chart.FindAccountByCodeAsync(chartId, code, cancellationToken);
        if (account is null || account.IsHeader || !account.IsActive)
        {
            return Error.Validation("pay_component.account_unknown", "The account does not exist in the company's chart, or is a header or inactive.").WithWhy(("accountCode", code));
        }

        if (!string.Equals(account.Type, expectedType, StringComparison.Ordinal))
        {
            return Error.Validation("pay_component.account_type", $"The account is not a {expectedType} account.").WithWhy(("accountCode", code), ("type", account.Type));
        }

        if (account.IsControl)
        {
            return Error.Validation("pay_component.account_is_control", "A control account is fed by its subledger, not by payroll.").WithWhy(("accountCode", code));
        }

        return account;
    }

    private static object Snapshot(PayComponent c) => new { c.Code, name = c.Name, c.Kind, c.Calculation, c.DefaultAmount, c.DefaultRatePct, c.AppliesToAll, c.ExpenseAccountCode, c.LiabilityAccountCode, c.IsActive };

    public static PayComponentSummary Map(PayComponent c) => new(c.Id, c.CompanyId, c.Code, c.Name.Values, c.Kind, c.Calculation, c.DefaultAmount, c.DefaultRatePct, c.AppliesToAll, c.ExpenseAccountCode, c.LiabilityAccountCode, c.SortOrder, c.IsActive, c.UpdatedAt);
}

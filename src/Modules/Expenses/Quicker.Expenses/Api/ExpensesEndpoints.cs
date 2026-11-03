using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Expenses.Application;
using Quicker.Web;

namespace Quicker.Expenses.Api;

/// <summary>The expenses surface of /api/v1: categories, expenses (record, confirm, pay, reverse), petty cash funds and their replenishments.</summary>
public static class ExpensesEndpoints
{
    // Paying from a bank account in the same step is a posted payment: Banking's permission applies with the expense ones.
    private const string BankingPaymentPost = "banking.payment.post";

    public static RouteGroupBuilder MapExpensesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var expenses = api.MapGroup("/expenses").WithTags("Expenses").RequireAuthorization();

        var categories = expenses.MapGroup("/categories");
        categories.MapGet("/", async (bool? isActive, CategoryService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(isActive, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseRead)
            .WithSummary("Expense categories: the account each lands on (by code, resolved in the company's chart), the usual tax code, and the policy a line must respect");
        categories.MapPost("/", async (SaveExpenseCategoryRequest request, CategoryService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static c => $"/api/v1/expenses/categories/{c.Id}"))
            .RequirePermission(ExpensesPermissions.CategoryManage)
            .WithSummary("A new expense category");
        categories.MapPost("/defaults", async (CategoryService service, CancellationToken ct) => ApiProblems.Ok(await service.EnsureDefaultsAsync(ct)))
            .RequirePermission(ExpensesPermissions.CategoryManage)
            .WithSummary("Makes sure the usual kinds of spend exist (rent, salaries, utilities, transport, phone and internet, maintenance, marketing, supplies, fees, other), each on its usual account, and returns the active categories");
        categories.MapPut("/{categoryId:guid}", async (Guid categoryId, SaveExpenseCategoryRequest request, CategoryService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(categoryId, request, ct)))
            .RequirePermission(ExpensesPermissions.CategoryManage)
            .WithSummary("Changes a category; expenses already confirmed keep the account they were posted to");

        var funds = expenses.MapGroup("/funds");
        funds.MapGet("/", async (Guid? companyId, FundService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(ExpensesPermissions.FundRead)
            .WithSummary("Petty cash funds with their balances (from the cash box's movements) and what it takes to bring each back to its float");
        funds.MapPost("/", async (SaveFundRequest request, FundService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static f => $"/api/v1/expenses/funds/{f.Id}"))
            .RequirePermission(ExpensesPermissions.FundManage)
            .WithSummary("A fund on a cash box (a cash or petty cash account) with its custodian and float");
        funds.MapPut("/{fundId:guid}", async (Guid fundId, SaveFundRequest request, FundService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(fundId, request, ct)))
            .RequirePermission(ExpensesPermissions.FundManage)
            .WithSummary("Changes a fund's name, custodian, float or activity");
        funds.MapGet("/{fundId:guid}", async (Guid fundId, FundService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(fundId, ct)))
            .RequirePermission(ExpensesPermissions.FundRead)
            .WithSummary("One fund with its balance");
        funds.MapGet("/{fundId:guid}/replenishments", async (Guid fundId, FundService service, CancellationToken ct) => TypedResults.Ok(await service.ReplenishmentsAsync(fundId, ct)))
            .RequirePermission(ExpensesPermissions.FundRead)
            .WithSummary("The fund's replenishments, newest first");
        funds.MapPost("/{fundId:guid}/replenish", async (Guid fundId, ReplenishFundRequest request, FundService service, CancellationToken ct) => ApiProblems.Created(await service.ReplenishAsync(fundId, request, ct), static r => $"/api/v1/expenses/replenishments/{r.Id}"))
            .RequirePermission(ExpensesPermissions.FundManage)
            .WithSummary("Cash from a bank account into the fund on a day, posted at once: Dr the fund's cash box / Cr the bank, both movements in the bank subledger");
        expenses.MapPost("/replenishments/{replenishmentId:guid}/reverse", async (Guid replenishmentId, ReverseExpenseRequest request, FundService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseReplenishmentAsync(replenishmentId, request, ct)))
            .RequirePermission(ExpensesPermissions.FundManage)
            .WithSummary("Reverses a replenishment: the mirror journal and the mirror movements, dated the reversal");

        expenses.MapGet("/", async (Guid companyId, string? status, string? kind, Guid? partnerId, Guid? fundId, DateOnly? from, DateOnly? to, bool? includeLines, ExpenseService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(companyId, status, kind, partnerId, fundId, from, to, ct, includeLines ?? false)))
            .RequirePermission(ExpensesPermissions.ExpenseRead)
            .WithSummary("Expenses of a company, newest first (up to 500): by status, kind (supplier, employee, petty_cash), payee, fund and expense date; each with what has been paid on it, and with its lines when includeLines is set");
        expenses.MapPost("/", async (SaveExpenseRequest request, ExpenseService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static e => $"/api/v1/expenses/{e.Id}"))
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Records an expense as a draft: its payee or fund, date, currency and lines (category, amount, description, tax code, dimensions); custom fields under host 'expense'");
        expenses.MapPost("/quick", async (QuickExpenseRequest request, QuickExpenseService service, CancellationToken ct) => ApiProblems.Created(await service.RecordAsync(request, ct), static r => $"/api/v1/expenses/{r.Expense.Id}"))
            .RequirePermission(ExpensesPermissions.ExpenseManage).RequirePermission(ExpensesPermissions.ExpenseConfirm).RequirePermission(BankingPaymentPost)
            .WithSummary("Records, confirms and pays an expense in one step: a petty cash voucher when paid from a cash box, the payee's bill paid from a bank account the same day, or the payee's bill left owed; says whether it is paid, owed or awaiting approval");
        expenses.MapGet("/{expenseId:guid}", async (Guid expenseId, ExpenseService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(expenseId, ct), ExpenseService.DocumentType, expenseId))
            .RequirePermission(ExpensesPermissions.ExpenseRead)
            .WithSummary("One expense with its lines, its journal and what was paid");
        expenses.MapPut("/{expenseId:guid}", async (Guid expenseId, SaveExpenseRequest request, ExpenseService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(expenseId, request, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Changes a draft (or rejected) expense");
        expenses.MapDelete("/{expenseId:guid}", async (Guid expenseId, ExpenseService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(expenseId, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Deletes a draft expense");
        expenses.MapPost("/{expenseId:guid}/confirm", async (Guid expenseId, ExpenseService service, CancellationToken ct) => ApiProblems.Ok(await service.ConfirmAsync(expenseId, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseConfirm)
            .WithSummary("Confirms the expense: policy and account checks, tax on the expense date, then the accrual posted (Dr the categories' accounts and input tax / Cr the payable, or the cash box for a petty cash voucher) and the payable opened; waits for approval when a workflow on expenses is active");
        expenses.MapPost("/{expenseId:guid}/pay", async (Guid expenseId, PayExpenseRequest request, ExpenseService service, CancellationToken ct) => ApiProblems.Ok(await service.PayAsync(expenseId, request, ct)))
            .RequirePermission("banking.payment.post")
            .WithSummary("Pays a confirmed supplier or employee expense from a bank account on a day: one supplier payment created and posted, settling the expense's open item (the whole remainder unless an amount is given)");
        expenses.MapPost("/{expenseId:guid}/reverse", async (Guid expenseId, ReverseExpenseRequest request, ExpenseService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(expenseId, request, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseConfirm)
            .WithSummary("Reverses a confirmed expense: refused once a payment stands on it; the payable (or the cash movement), the tax ledger and the journal all mirrored on the reversal date");
        expenses.MapPost("/{expenseId:guid}/cancel", async (Guid expenseId, ExpenseService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(expenseId, ct)))
            .RequirePermission(ExpensesPermissions.ExpenseManage)
            .WithSummary("Cancels a draft, rejected or pending expense (withdrawing its approval request)");
        return api;
    }
}

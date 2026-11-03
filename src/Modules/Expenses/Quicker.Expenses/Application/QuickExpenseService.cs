using Microsoft.EntityFrameworkCore;
using Quicker.Banking.Contracts;
using Quicker.Expenses.Contracts;
using Quicker.Expenses.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;

namespace Quicker.Expenses.Application;

/// <summary>What became of a one-step expense: paid at once, owed until it is paid, or waiting for an approval the workflow asks for.</summary>
public static class QuickExpenseOutcomes
{
    public const string Paid = "paid";
    public const string Owed = "owed";
    public const string PendingApproval = "pending_approval";
}

/// <summary>
/// An expense in one step, as a shop records it (ADR-0035 §12, A-195): what for (the category), how much in the company's
/// currency, when, paid from which bank account or cash box (or not yet), and to whom. A payee is needed unless the
/// money left a cash box.
/// </summary>
public sealed record QuickExpenseRequest(Guid CompanyId, Guid CategoryId, decimal Amount, DateOnly? ExpenseDate = null, Guid? PaidFromBankAccountId = null, Guid? PartnerId = null, DateOnly? DueDate = null, string? Description = null, string? Reference = null);

public sealed record QuickExpenseResult(string Outcome, ExpenseSummary Expense, string? PaymentNumber);

/// <summary>
/// Records, confirms and (when the money left an account) pays an expense in one call, through the expense services
/// so the postings are those of the full edition: a petty cash voucher from the fund that sits on the cash box (made
/// on first use), a supplier or employee expense paid from a bank account on the same day, or a supplier or employee
/// expense left owed. A workflow that holds expenses for approval holds these too, and the result says so.
/// </summary>
public sealed class QuickExpenseService(ExpensesDbContext db, ExpenseService expenses, FundService funds, ICompanyDirectory companies, IBankAccountDirectory bankAccounts, IPartnerDirectory partners, IClock clock)
{
    public async Task<Result<QuickExpenseResult>> RecordAsync(QuickExpenseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return Error.NotFound(CategoryService.EntityType, request.CategoryId);
        }

        if (request.Amount <= 0m)
        {
            return Error.Validation("expense.amount_invalid", "The amount is above zero.");
        }

        var fc = company.FunctionalCurrency.Code;
        var date = request.ExpenseDate ?? clock.TodayIn(company.TimeZone);
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        var line = new SaveExpenseLineRequest(category.Id, request.Amount, description);

        BankAccountInfo? account = null;
        if (request.PaidFromBankAccountId is { } accountId)
        {
            account = await bankAccounts.FindAsync(accountId, cancellationToken);
            if (account is null || account.CompanyId != request.CompanyId)
            {
                return Error.NotFound("bank_account", accountId);
            }

            if (!account.IsActive)
            {
                return Error.Validation("expense.quick_account_inactive", $"'{account.Code}' is closed.").WithWhy(("bankAccount", account.Code));
            }

            if (!string.Equals(account.Currency, fc, StringComparison.Ordinal))
            {
                return Error.Validation("expense.quick_account_currency", $"'{account.Code}' is kept in {account.Currency}; an expense in one step is paid from an account in {fc}.").WithWhy(("bankAccount", account.Code), ("bankCurrency", account.Currency), ("currency", fc));
            }
        }

        // Cash out of a box: a petty cash voucher from the fund on that box, paid when it is recorded.
        if (account is not null && account.Kind is BankAccountKinds.Cash or BankAccountKinds.PettyCash)
        {
            var fund = await FundOnAsync(account, cancellationToken);
            if (fund.IsFailure)
            {
                return fund.Error!;
            }

            var voucher = await expenses.CreateAsync(new SaveExpenseRequest(request.CompanyId, ExpenseKinds.PettyCash, date, [line], FundId: fund.Value, Currency: fc, Reference: reference, Description: description), cancellationToken);
            if (voucher.IsFailure)
            {
                return voucher.Error!;
            }

            var confirmed = await expenses.ConfirmAsync(voucher.Value.Id, cancellationToken);
            return confirmed.IsFailure ? confirmed.Error! : new QuickExpenseResult(confirmed.Value.Status == ExpenseStatuses.Confirmed ? QuickExpenseOutcomes.Paid : QuickExpenseOutcomes.PendingApproval, confirmed.Value, null);
        }

        // Owed to someone, or paid to them from a bank account: the payee's bill or claim.
        if (request.PartnerId is not { } partnerId)
        {
            return Error.Validation("expense.quick_payee_required", account is null ? "An expense not paid yet names who it is owed to." : "An expense paid from a bank account names who was paid.");
        }

        var partner = await partners.FindAsync(partnerId, cancellationToken);
        if (partner is null || !partner.IsActive)
        {
            return Error.NotFound("partner", partnerId);
        }

        var kind = partner.IsSupplier ? ExpenseKinds.Supplier : ExpenseKinds.Employee;
        var created = await expenses.CreateAsync(new SaveExpenseRequest(request.CompanyId, kind, date, [line], PartnerId: partnerId, DueDate: request.DueDate, Currency: fc, Reference: reference, Description: description), cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var owed = await expenses.ConfirmAsync(created.Value.Id, cancellationToken);
        if (owed.IsFailure)
        {
            return owed.Error!;
        }

        if (owed.Value.Status != ExpenseStatuses.Confirmed)
        {
            return new QuickExpenseResult(QuickExpenseOutcomes.PendingApproval, owed.Value, null);
        }

        if (account is null)
        {
            return new QuickExpenseResult(QuickExpenseOutcomes.Owed, owed.Value, null);
        }

        var paid = await expenses.PayAsync(owed.Value.Id, new PayExpenseRequest(account.Id, date, null, "transfer", reference), cancellationToken);
        return paid.IsFailure ? paid.Error! : new QuickExpenseResult(QuickExpenseOutcomes.Paid, paid.Value.Expense, paid.Value.PaymentNumber);
    }

    /// <summary>The petty cash fund that sits on the box, made on first use with the box's own code and name and no float.</summary>
    private async Task<Result<Guid>> FundOnAsync(BankAccountInfo box, CancellationToken cancellationToken)
    {
        var existing = await db.Funds.AsNoTracking().Where(f => f.CompanyId == box.CompanyId && f.BankAccountId == box.Id).Select(static f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            return id;
        }

        var created = await funds.SaveAsync(null, new SaveFundRequest(box.CompanyId, box.Code, box.Name.Values, box.Id), cancellationToken);
        return created.IsSuccess ? created.Value.Id : created.Error!;
    }
}

using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Expenses.Domain;
using Quicker.Expenses.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Expenses.Application;

/// <summary>
/// Expense categories (configuration over code): what kinds of spend exist, the account each lands on by code (resolved
/// in the company's chart when an expense is confirmed, so companies with their own charts share the catalogue), the
/// usual tax code, and the policy a line must respect (a receipt, a ceiling).
/// </summary>
public sealed class CategoryService(ExpensesDbContext db, IAuditSink audit, IClock clock)
{
    public const string EntityType = "expense_category";

    public async Task<IReadOnlyList<ExpenseCategorySummary>> ListAsync(bool? isActive, CancellationToken cancellationToken)
    {
        var query = db.Categories.AsNoTracking().AsQueryable();
        if (isActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        return (await query.OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<ExpenseCategorySummary>> SaveAsync(Guid? id, SaveExpenseCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, EntityType);
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, EntityType);
        if (name.IsFailure)
        {
            return name.Error!;
        }

        if (string.IsNullOrWhiteSpace(request.AccountCode))
        {
            return Error.Validation("expense_category.account_required", "A category names the expense account its lines land on, by code.");
        }

        if (request.MaxAmount is <= 0m)
        {
            return Error.Validation("expense_category.max_amount_invalid", "The ceiling, when set, is above zero.");
        }

        ExpenseCategory? category = null;
        if (id is { } existing)
        {
            category = await db.Categories.SingleOrDefaultAsync(c => c.Id == existing, cancellationToken);
            if (category is null)
            {
                return Error.NotFound(EntityType, existing);
            }
        }

        if (await db.Categories.AnyAsync(c => c.Code == code.Value && (category == null || c.Id != category.Id), cancellationToken))
        {
            return Error.Conflict("expense_category.code_taken", $"An expense category '{code.Value}' already exists.");
        }

        var isNew = category is null;
        var before = category is null ? null : Map(category);
        category ??= new ExpenseCategory { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        category.Code = code.Value;
        category.Name = name.Value;
        category.AccountCode = request.AccountCode.Trim();
        category.TaxCodeId = request.TaxCodeId;
        category.ReceiptRequired = request.ReceiptRequired;
        category.MaxAmount = request.MaxAmount;
        category.IsActive = request.IsActive;
        category.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Categories.Add(category);
        }

        await db.SaveChangesAsync(cancellationToken);
        var after = Map(category);
        await audit.RecordAsync(new AuditEntry(EntityType, category.Id, category.Code, isNew ? AuditActions.Created : AuditActions.Updated, before, after), cancellationToken);
        return after;
    }

    public static ExpenseCategorySummary Map(ExpenseCategory c) => new(c.Id, c.Code, c.Name.Values, c.AccountCode, c.TaxCodeId, c.ReceiptRequired, c.MaxAmount, c.IsActive, c.UpdatedAt);
}

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

    /// <summary>The usual kinds of spend of a small business and the account each lands on in the shipped charts (A-195).</summary>
    private static readonly (string Code, string En, string Ar, string Account)[] Defaults =
    [
        ("RENT", "Rent", "الإيجار", "6110"),
        ("SALARIES", "Salaries and wages", "الرواتب والأجور", "6100"),
        ("UTILITIES", "Electricity, water and fuel", "الكهرباء والماء والوقود", "6120"),
        ("TRANSPORT", "Transport and delivery", "النقل والتوصيل", "6140"),
        ("PHONE", "Phone and internet", "الهاتف والإنترنت", "6190"),
        ("MAINTENANCE", "Repairs and maintenance", "الصيانة والإصلاح", "6180"),
        ("MARKETING", "Marketing and advertising", "التسويق والإعلان", "6130"),
        ("SUPPLIES", "Shop and office supplies", "مستلزمات المحل والمكتب", "6150"),
        ("FEES", "Professional fees", "الأتعاب المهنية", "6160"),
        ("OTHER", "Other expenses", "مصروفات أخرى", "6600"),
    ];

    /// <summary>Makes sure every usual kind exists (by code; the ones already there, renamed or re-pointed, are left alone) and returns the active categories.</summary>
    public async Task<Result<IReadOnlyList<ExpenseCategorySummary>>> EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        var existing = (await db.Categories.AsNoTracking().Select(static c => c.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, en, ar, account) in Defaults)
        {
            if (existing.Contains(code))
            {
                continue;
            }

            var saved = await SaveAsync(null, new SaveExpenseCategoryRequest(code, new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar }, account), cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error!;
            }
        }

        return Result<IReadOnlyList<ExpenseCategorySummary>>.Success(await ListAsync(true, cancellationToken));
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

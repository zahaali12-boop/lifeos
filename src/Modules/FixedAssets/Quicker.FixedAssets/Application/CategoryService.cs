using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.FixedAssets.Contracts;
using Quicker.FixedAssets.Domain;
using Quicker.FixedAssets.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.FixedAssets.Application;

/// <summary>Asset categories (A-175): the defaults an asset starts with and the accounts its postings land on in every company's chart.</summary>
public sealed class CategoryService(FixedAssetsDbContext db, IAuditSink audit, IClock clock)
{
    public const string EntityType = "asset_category";

    public async Task<IReadOnlyList<AssetCategorySummary>> ListAsync(bool? isActive, CancellationToken cancellationToken)
    {
        var query = db.Categories.AsNoTracking().AsQueryable();
        if (isActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        return (await query.OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<AssetCategorySummary>> SaveAsync(Guid? id, SaveAssetCategoryRequest request, CancellationToken cancellationToken)
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

        var method = Validation.OneOf(request.DefaultMethod, "asset_category.method", [.. DepreciationMethods.All]);
        if (method.IsFailure)
        {
            return method.Error!;
        }

        var convention = Validation.OneOf(request.Convention, "asset_category.convention", [.. DepreciationConventions.All]);
        if (convention.IsFailure)
        {
            return convention.Error!;
        }

        if (request.DefaultLifeMonths < 0 || (method.Value is not (DepreciationMethods.None or DepreciationMethods.UnitsOfProduction) && request.DefaultLifeMonths == 0))
        {
            return Error.Validation("asset_category.life_invalid", "A depreciating category names a life in months.");
        }

        if (request.DefaultSalvagePct is < 0m or >= 100m)
        {
            return Error.Validation("asset_category.salvage_invalid", "The salvage share is from 0 to below 100 percent of cost.");
        }

        if (request.DefaultRatePct is { } rate && rate <= 0m)
        {
            return Error.Validation("asset_category.rate_invalid", "A declining rate is above zero percent a year.");
        }

        AssetCategory? category = null;
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
            return Error.Conflict("asset_category.code_taken", $"A category '{code.Value}' already exists.");
        }

        var isNew = category is null;
        category ??= new AssetCategory { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        category.Code = code.Value;
        category.Name = name.Value;
        category.DefaultMethod = method.Value;
        category.DefaultLifeMonths = request.DefaultLifeMonths;
        category.DefaultSalvagePct = request.DefaultSalvagePct;
        category.DefaultRatePct = request.DefaultRatePct;
        category.Convention = convention.Value;
        category.CostAccountCode = Clean(request.CostAccountCode);
        category.AccumulatedDepreciationAccountCode = Clean(request.AccumulatedDepreciationAccountCode);
        category.DepreciationExpenseAccountCode = Clean(request.DepreciationExpenseAccountCode);
        category.IsActive = request.IsActive;
        category.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Categories.Add(category);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, category.Id, category.Code, isNew ? AuditActions.Created : AuditActions.Updated, After: new { category.Code, category.DefaultMethod, category.DefaultLifeMonths, category.Convention, category.IsActive }), cancellationToken);
        return Map(category);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static AssetCategorySummary Map(AssetCategory c) => new(c.Id, c.Code, c.Name.Values, c.DefaultMethod, c.DefaultLifeMonths, c.DefaultSalvagePct, c.DefaultRatePct, c.Convention, c.CostAccountCode, c.AccumulatedDepreciationAccountCode, c.DepreciationExpenseAccountCode, c.IsActive, c.UpdatedAt);
}

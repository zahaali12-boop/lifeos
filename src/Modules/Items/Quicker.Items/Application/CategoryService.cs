using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Domain;
using Quicker.Items.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Items.Application;

/// <summary>Item categories as a tree with a materialised path, so a subtree is one prefix query and a move rewrites its paths.</summary>
public sealed class CategoryService(ItemsDbContext db, IPostingGroupDirectory postingGroups, IStockActivity stock, IClock clock)
{
    public async Task<IReadOnlyList<ItemCategorySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories.OrderBy(static c => c.Path).ToListAsync(cancellationToken);
        var counts = await db.Items.Where(static i => i.CategoryId != null).GroupBy(static i => i.CategoryId!.Value)
            .Select(static g => new { CategoryId = g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.CategoryId, static g => g.Count, cancellationToken);
        var byId = categories.ToDictionary(static c => c.Id);
        return categories.Select(c => Map(c, c.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent.Code : null, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<ItemCategorySummary?> GetAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return null;
        }

        var parentCode = category.ParentId is { } p ? await db.Categories.Where(c => c.Id == p).Select(static c => c.Code).SingleOrDefaultAsync(cancellationToken) : null;
        var count = await db.Items.CountAsync(i => i.CategoryId == categoryId, cancellationToken);
        return Map(category, parentCode, count);
    }

    public async Task<Result<ItemCategorySummary>> CreateAsync(SaveItemCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = await ValidateAsync(request, null, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, parent) = validated.Value;
        if (await db.Categories.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict("category.code_taken", $"A category with code '{code}' already exists.");
        }

        var now = clock.UtcNow;
        var category = new ItemCategory { Id = Guid.CreateVersion7(), Code = code, CreatedAt = now, UpdatedAt = now };
        Apply(category, request, parent);
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return Map(category, parent?.Code, 0);
    }

    public async Task<Result<ItemCategorySummary>> UpdateAsync(Guid categoryId, SaveItemCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return Error.NotFound("category", categoryId);
        }

        var validated = await ValidateAsync(request, category, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, parent) = validated.Value;
        if (code != category.Code && await db.Categories.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict("category.code_taken", $"A category with code '{code}' already exists.");
        }

        // The category's costing method applies to its items (ADR-0008); once one of them has moved it stays.
        var costing = string.IsNullOrWhiteSpace(request.CostingMethodOverride) ? null : request.CostingMethodOverride.Trim();
        if (!string.Equals(costing, category.CostingMethodOverride, StringComparison.Ordinal))
        {
            var itemIds = await db.Items.Where(i => i.CategoryId == categoryId).Select(static i => i.Id).ToListAsync(cancellationToken);
            var moved = await stock.ItemsWithMovementsAsync(itemIds, null, cancellationToken);
            if (moved.Count > 0)
            {
                return Error.Conflict("category.costing_locked", "Items of the category have moved stock under its costing method; it cannot change now.").WithWhy(("itemsWithStockMovements", moved.Count));
            }
        }

        var oldPath = category.Path;
        category.Code = code;
        Apply(category, request, parent);
        category.UpdatedAt = clock.UtcNow;
        if (category.Path != oldPath)
        {
            // Every descendant keeps its place under the moved or renamed node.
            var descendants = await db.Categories.Where(c => c.Path.StartsWith(oldPath) && c.Id != category.Id).ToListAsync(cancellationToken);
            var levelDelta = category.Level - (oldPath.Count(static ch => ch == '/') - 2);
            foreach (var d in descendants)
            {
                d.Path = string.Concat(category.Path, d.Path.AsSpan(oldPath.Length));
                d.Level += levelDelta;
                d.UpdatedAt = category.UpdatedAt;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var count = await db.Items.CountAsync(i => i.CategoryId == categoryId, cancellationToken);
        return Map(category, parent?.Code, count);
    }

    public async Task<Result> DeleteAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return Error.NotFound("category", categoryId);
        }

        if (await db.Categories.AnyAsync(c => c.ParentId == categoryId, cancellationToken))
        {
            return Error.Conflict("category.has_children", "Move or delete the child categories first.");
        }

        if (await db.Items.AnyAsync(i => i.CategoryId == categoryId, cancellationToken))
        {
            return Error.Conflict("category.has_items", "Reassign the category's items first.");
        }

        db.Categories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<(string Code, ItemCategory? Parent)>> ValidateAsync(SaveItemCategoryRequest request, ItemCategory? existing, CancellationToken cancellationToken)
    {
        var code = Validation.UpperCode(request.Code, "category");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "category");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var costing = Validation.OptionalOneOf(request.CostingMethodOverride, "category.costing_method_override", Validation.CostingMethods);
        if (costing.IsFailure)
        {
            return costing.Error!;
        }

        if (request.ItemPostingGroupId is { } groupId)
        {
            var group = await postingGroups.FindAsync(groupId, cancellationToken);
            if (group is null || group.Kind != "item")
            {
                return Error.Validation("category.posting_group_invalid", "The posting group must be an item posting group.").WithWhy(("itemPostingGroupId", groupId));
            }
        }

        ItemCategory? parent = null;
        if (request.ParentId is { } parentId || !string.IsNullOrWhiteSpace(request.ParentCode))
        {
            var parentCode = request.ParentCode?.Trim().ToUpperInvariant();
            parent = request.ParentId is { } pid
                ? await db.Categories.SingleOrDefaultAsync(c => c.Id == pid, cancellationToken)
                : await db.Categories.SingleOrDefaultAsync(c => c.Code == parentCode, cancellationToken);
            if (parent is null)
            {
                return Error.Validation("category.parent_unknown", "The parent category does not exist.").WithWhy(("parentId", request.ParentId), ("parentCode", request.ParentCode));
            }

            if (existing is not null && (parent.Id == existing.Id || parent.Path.StartsWith(existing.Path, StringComparison.Ordinal)))
            {
                return Error.Validation("category.cycle", "A category cannot be moved under itself or one of its descendants.").WithWhy(("category", existing.Code), ("parent", parent.Code));
            }
        }

        return (code.Value, parent);
    }

    private static void Apply(ItemCategory category, SaveItemCategoryRequest request, ItemCategory? parent)
    {
        category.Name = Validation.Name(request.Name, "category").Value;
        category.ParentId = parent?.Id;
        category.Path = (parent?.Path ?? "/") + category.Code + "/";
        category.Level = parent is null ? 0 : parent.Level + 1;
        category.CostingMethodOverride = string.IsNullOrWhiteSpace(request.CostingMethodOverride) ? null : request.CostingMethodOverride.Trim();
        category.ItemPostingGroupId = request.ItemPostingGroupId;
        category.ItemTaxGroupId = request.ItemTaxGroupId;
        category.IsActive = request.IsActive;
    }

    private static ItemCategorySummary Map(ItemCategory c, string? parentCode, int itemCount) =>
        new(c.Id, c.ParentId, parentCode, c.Code, c.Name.Values, c.Path, c.Level, c.CostingMethodOverride, c.ItemPostingGroupId, c.ItemTaxGroupId, c.IsActive, itemCount, c.UpdatedAt);
}

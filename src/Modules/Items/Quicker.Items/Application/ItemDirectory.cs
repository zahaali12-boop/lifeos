using Microsoft.EntityFrameworkCore;
using Quicker.Items.Contracts;
using Quicker.Items.Domain;
using Quicker.Items.Persistence;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Items.Application;

/// <summary>
/// The item directory other modules read. Units and items are memoised per unit of work: a stock document with a
/// thousand lines resolves each item once, and the scenario-9 property test converts ten thousand times in seconds.
/// </summary>
public sealed class ItemDirectory(ItemsDbContext db, IUomDirectory uoms) : IItemDirectory
{
    private readonly Dictionary<Guid, ItemInfo?> _items = new();
    private readonly Dictionary<Guid, IReadOnlyList<ItemUomInfo>> _itemUoms = new();
    private IReadOnlyDictionary<Guid, UomInfo>? _uomsById;

    public async Task<ItemInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (_items.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        var info = item is null ? null : await MapAsync(item, cancellationToken);
        _items[itemId] = info;
        return info;
    }

    public async Task<ItemInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code?.Trim() ?? string.Empty;
        var item = await db.Items.SingleOrDefaultAsync(i => i.Code == normalized, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var info = await MapAsync(item, cancellationToken);
        _items[item.Id] = info;
        return info;
    }

    public async Task<ItemVariantInfo?> FindVariantAsync(Guid variantId, CancellationToken cancellationToken = default)
    {
        var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == variantId, cancellationToken);
        return variant is null ? null : new ItemVariantInfo(variant.Id, variant.ItemId, variant.Sku, variant.Name, variant.IsActive);
    }

    public async Task<IReadOnlyList<ItemUomInfo>> UomsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (_itemUoms.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var baseUomId = await db.Items.Where(i => i.Id == itemId).Select(static i => (Guid?)i.BaseUomId).SingleOrDefaultAsync(cancellationToken);
        if (baseUomId is null)
        {
            return [];
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var rows = await db.ItemUoms.Where(u => u.ItemId == itemId).ToListAsync(cancellationToken);
        var list = rows.Select(u =>
        {
            var info = byId[u.UomId];
            return new ItemUomInfo(u.Id, u.UomId, info.Code, info.Precision, ItemUomMath.Normalize(u.Numerator), ItemUomMath.Normalize(u.Denominator), u.UomId == baseUomId);
        }).OrderByDescending(static u => u.IsBase).ThenBy(static u => u.UomCode, StringComparer.Ordinal).ToList();
        _itemUoms[itemId] = list;
        return list;
    }

    public async Task<Result<BaseQuantity>> ToBaseAsync(Guid itemId, Guid uomId, decimal quantity, CancellationToken cancellationToken = default)
    {
        var units = await UomsAsync(itemId, cancellationToken);
        var baseUom = units.FirstOrDefault(static u => u.IsBase);
        if (baseUom is null)
        {
            return Error.NotFound("item", itemId);
        }

        var unit = units.FirstOrDefault(u => u.UomId == uomId);
        if (unit is null)
        {
            return Error.Validation("quantity.uom_not_item_uom", "The unit is not one of the item's units.").WithWhy(("itemId", itemId), ("uomId", uomId), ("itemUoms", units.Select(static u => u.UomCode)));
        }

        var converted = ItemUomMath.ToBase(quantity, unit.Numerator, unit.Denominator, baseUom.Precision);
        if (converted.IsFailure)
        {
            return converted.Error!.WithWhy(("itemId", itemId), ("uom", unit.UomCode), ("baseUom", baseUom.UomCode));
        }

        return new BaseQuantity(ItemUomMath.Normalize(converted.Value), baseUom.UomId, baseUom.UomCode, quantity, unit.UomId, unit.UomCode);
    }

    public async Task<Result<decimal>> FromBaseAsync(Guid itemId, Guid uomId, decimal baseQuantity, CancellationToken cancellationToken = default)
    {
        var units = await UomsAsync(itemId, cancellationToken);
        var unit = units.FirstOrDefault(u => u.UomId == uomId);
        if (unit is null)
        {
            return Error.Validation("quantity.uom_not_item_uom", "The unit is not one of the item's units.").WithWhy(("itemId", itemId), ("uomId", uomId), ("itemUoms", units.Select(static u => u.UomCode)));
        }

        var converted = ItemUomMath.FromBase(baseQuantity, unit.Numerator, unit.Denominator, unit.Precision);
        return converted.IsFailure ? converted.Error!.WithWhy(("itemId", itemId), ("uom", unit.UomCode)) : ItemUomMath.Normalize(converted.Value);
    }

    public async Task<BarcodeMatch?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var value = barcode?.Trim() ?? string.Empty;
        var match = await (from b in db.Barcodes
                           join u in db.ItemUoms on b.ItemUomId equals u.Id
                           join i in db.Items on u.ItemId equals i.Id
                           where b.Barcode == value
                           select new { b, u, i }).SingleOrDefaultAsync(cancellationToken);
        if (match is null)
        {
            return null;
        }

        var byId = await UomsByIdAsync(cancellationToken);
        return new BarcodeMatch(match.i.Id, match.i.Code, match.b.VariantId, match.u.Id, match.u.UomId, byId[match.u.UomId].Code, match.b.Barcode, match.b.Symbology);
    }

    public async Task<IReadOnlyList<ItemPlanningInfo>> PlanningParametersAsync(Guid warehouseId, CancellationToken cancellationToken = default)
    {
        var settings = await db.WarehouseSettings.Where(s => s.WarehouseId == warehouseId && (s.ReorderPoint != null || s.MinQty != null || s.MaxQty != null)).ToListAsync(cancellationToken);
        if (settings.Count == 0)
        {
            return [];
        }

        var itemIds = settings.Select(static s => s.ItemId).Distinct().ToList();
        var codes = await db.Items.Where(i => itemIds.Contains(i.Id) && i.IsActive).ToDictionaryAsync(static i => i.Id, static i => new { i.Code, i.PurchaseUomId }, cancellationToken);
        var suppliers = (await db.Suppliers.Where(s => itemIds.Contains(s.ItemId)).ToListAsync(cancellationToken))
            .GroupBy(static s => s.ItemId).ToDictionary(static g => g.Key, static g => g.OrderByDescending(static s => s.IsPreferred).ThenBy(static s => s.Id).First());
        var result = new List<ItemPlanningInfo>(settings.Count);
        foreach (var s in settings)
        {
            if (!codes.TryGetValue(s.ItemId, out var item))
            {
                continue;
            }

            var supplier = suppliers.GetValueOrDefault(s.ItemId);
            result.Add(new ItemPlanningInfo(s.ItemId, item.Code, s.WarehouseId, s.ReorderPoint, s.MinQty, s.MaxQty, s.SafetyStock, s.LeadTimeDays, supplier?.PartnerId, supplier?.LeadTimeDays, item.PurchaseUomId));
        }

        return result;
    }

    public async Task<IReadOnlyList<Guid>> ItemsForCycleCountAsync(Guid warehouseId, IReadOnlyList<string> classes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classes);
        var wanted = classes.Select(static c => c.Trim().ToUpperInvariant()).ToList();
        return await db.WarehouseSettings.Where(s => s.WarehouseId == warehouseId && s.CycleCountClass != null && wanted.Contains(s.CycleCountClass)).Select(static s => s.ItemId).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemCategoryInfo>> CategoryLineageAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return [];
        }

        // The materialised path names every ancestor by code ("/ROOT/CHILD/"); one query reads them all.
        var codes = category.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rows = await db.Categories.AsNoTracking().Where(c => codes.Contains(c.Code)).ToListAsync(cancellationToken);
        var byCode = rows.Where(c => category.Path.StartsWith(c.Path, StringComparison.Ordinal)).ToDictionary(static c => c.Code, StringComparer.Ordinal);
        return Enumerable.Reverse(codes).Where(byCode.ContainsKey).Select(code => byCode[code]).Select(static c => new ItemCategoryInfo(c.Id, c.Code, c.Name, c.ParentId, c.IsActive)).ToList();
    }

    public async Task<ItemCompanyPolicy?> CompanyPolicyAsync(Guid itemId, Guid companyId, CancellationToken cancellationToken = default)
    {
        var settings = await db.CompanySettings.SingleOrDefaultAsync(s => s.ItemId == itemId && s.CompanyId == companyId, cancellationToken);
        return settings is null ? null : new ItemCompanyPolicy(itemId, companyId, settings.CostingMethodOverride, settings.StandardCost, settings.ItemPostingGroupOverride, settings.DefaultWarehouseId, settings.AllowNegativeStock);
    }

    public async Task<IReadOnlyDictionary<Guid, CatalogRef>> DescribeAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, CatalogRef>();
        if (ids.Count == 0)
        {
            return result;
        }

        var wanted = ids.Distinct().ToArray();
        foreach (var i in await db.Items.AsNoTracking().Where(i => wanted.Contains(i.Id)).Select(static i => new { i.Id, i.Code, i.Name }).ToListAsync(cancellationToken))
        {
            result[i.Id] = new CatalogRef(i.Id, "item", i.Code, i.Name);
        }

        foreach (var v in await db.Variants.AsNoTracking().Where(v => wanted.Contains(v.Id)).Select(static v => new { v.Id, v.Sku, v.Name }).ToListAsync(cancellationToken))
        {
            result[v.Id] = new CatalogRef(v.Id, "variant", v.Sku, v.Name);
        }

        foreach (var c in await db.Categories.AsNoTracking().Where(c => wanted.Contains(c.Id)).Select(static c => new { c.Id, c.Code, c.Name }).ToListAsync(cancellationToken))
        {
            result[c.Id] = new CatalogRef(c.Id, "category", c.Code, c.Name);
        }

        foreach (var b in await db.Brands.AsNoTracking().Where(b => wanted.Contains(b.Id)).Select(static b => new { b.Id, b.Code, b.Name }).ToListAsync(cancellationToken))
        {
            result[b.Id] = new CatalogRef(b.Id, "brand", b.Code, b.Name);
        }

        return result;
    }

    /// <summary>Forgets memoised items and units; the item service calls it after it changes an item.</summary>
    public void Forget(Guid itemId)
    {
        _items.Remove(itemId);
        _itemUoms.Remove(itemId);
    }

    private async Task<IReadOnlyDictionary<Guid, UomInfo>> UomsByIdAsync(CancellationToken cancellationToken) =>
        _uomsById ??= (await uoms.ListAsync(cancellationToken)).ToDictionary(static u => u.Id);

    private async Task<ItemInfo> MapAsync(Item item, CancellationToken cancellationToken)
    {
        var byId = await UomsByIdAsync(cancellationToken);
        var baseUom = byId[item.BaseUomId];
        var costing = item.CostingMethodOverrideOrNull();
        var taxGroup = item.ItemTaxGroupId;
        if ((costing is null || taxGroup is null) && item.CategoryId is { } categoryId)
        {
            var category = await db.Categories.Where(c => c.Id == categoryId).Select(static c => new { c.CostingMethodOverride, c.ItemTaxGroupId }).SingleOrDefaultAsync(cancellationToken);
            costing ??= category?.CostingMethodOverride;
            taxGroup ??= category?.ItemTaxGroupId;
        }

        return new ItemInfo(item.Id, item.Code, item.Name, item.Type, item.Tracking, item.ExpiryRequired, item.ShelfLifeDays, item.Fefo, item.BaseUomId, baseUom.Code, baseUom.Precision,
            item.SalesUomId, item.PurchaseUomId, item.CategoryId, item.ItemPostingGroupId, taxGroup, costing, item.HasVariants, item.IsActive, item.WeightKg, item.VolumeM3,
            item.BrandId, item.ListPrice, item.ListPriceCurrency);
    }
}

internal static class ItemExtensions
{
    /// <summary>Items have no costing override of their own today; the category's applies, then the company's method (ADR-0008).</summary>
    public static string? CostingMethodOverrideOrNull(this Item item) => null;
}

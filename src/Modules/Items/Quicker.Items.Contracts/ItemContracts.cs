using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Items.Contracts;

/// <summary>What the rest of the system needs to know about an item to move, price or cost it.</summary>
public sealed record ItemInfo(
    Guid Id,
    string Code,
    LocalizedText Name,
    string Type,
    string Tracking,
    bool ExpiryRequired,
    int? ShelfLifeDays,
    bool Fefo,
    Guid BaseUomId,
    string BaseUomCode,
    int BasePrecision,
    Guid? SalesUomId,
    Guid? PurchaseUomId,
    Guid? CategoryId,
    Guid? ItemPostingGroupId,
    Guid? ItemTaxGroupId,
    string? CostingMethodOverride,
    bool HasVariants,
    bool IsActive)
{
    public bool IsStockItem => Type is "stock" or "kit" or "assembly";
}

public sealed record ItemVariantInfo(Guid Id, Guid ItemId, string Sku, LocalizedText Name, bool IsActive);

/// <summary>One unit an item is handled in: 1 unit = Numerator / Denominator base units, exactly.</summary>
public sealed record ItemUomInfo(Guid ItemUomId, Guid UomId, string UomCode, int Precision, decimal Numerator, decimal Denominator, bool IsBase);

/// <summary>A quantity converted to the item's base unit, keeping what was entered for display and printing.</summary>
public sealed record BaseQuantity(decimal Quantity, Guid BaseUomId, string BaseUomCode, decimal EnteredQuantity, Guid EnteredUomId, string EnteredUomCode);

public sealed record BarcodeMatch(Guid ItemId, string ItemCode, Guid? VariantId, Guid ItemUomId, Guid UomId, string UomCode, string Barcode, string Symbology);

/// <summary>The item's settings for one company, each null when the company's own policy applies.</summary>
public sealed record ItemCompanyPolicy(Guid ItemId, Guid CompanyId, string? CostingMethodOverride, decimal? StandardCost, Guid? ItemPostingGroupOverride, Guid? DefaultWarehouseId, bool? AllowNegativeStock);

/// <summary>One direct component of an assembly's active bill, for a given output quantity, in the component's base unit (exact) with the scrap allowance.</summary>
public sealed record BomComponentInfo(Guid ComponentItemId, string ComponentCode, Guid? ComponentVariantId, Guid BaseUomId, string BaseUomCode, decimal Quantity, decimal QuantityWithScrap, int Position);

/// <summary>The active assembly bill of an item and what building a quantity of it consumes (direct components only; sub-assemblies are built separately).</summary>
public sealed record BomBuildInfo(Guid BomId, Guid ItemId, string ItemCode, int Version, decimal OutputQuantity, IReadOnlyList<BomComponentInfo> Components);

public interface IBomDirectory
{
    /// <summary>The components to build <paramref name="outputQuantity"/> (base unit) of the item from its active assembly bill, or null when the item has none.</summary>
    Task<Result<BomBuildInfo?>> BuildAsync(Guid itemId, decimal outputQuantity, CancellationToken cancellationToken = default);
}

/// <summary>An item's planning parameters in one warehouse (its warehouse settings) with its preferred supplier and lead time.</summary>
public sealed record ItemPlanningInfo(Guid ItemId, string ItemCode, Guid WarehouseId, decimal? ReorderPoint, decimal? MinQty, decimal? MaxQty, decimal? SafetyStock, int? LeadTimeDays, Guid? PreferredSupplierId, int? SupplierLeadTimeDays, Guid? PurchaseUomId);

/// <summary>Read access to the item master for the modules that move, buy, sell and cost items.</summary>
public interface IItemDirectory
{
    Task<ItemInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<ItemInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<ItemVariantInfo?> FindVariantAsync(Guid variantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ItemUomInfo>> UomsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A quantity entered in one of the item's units, in the base unit. Exact or refused: a quantity that is not a whole
    /// number of the base unit's precision (<c>quantity.not_exact_in_base</c>) never rounds silently (hard scenario 9).
    /// </summary>
    Task<Result<BaseQuantity>> ToBaseAsync(Guid itemId, Guid uomId, decimal quantity, CancellationToken cancellationToken = default);

    /// <summary>A base quantity expressed in one of the item's units; exact or <c>quantity.not_exact_in_uom</c>.</summary>
    Task<Result<decimal>> FromBaseAsync(Guid itemId, Guid uomId, decimal baseQuantity, CancellationToken cancellationToken = default);

    Task<BarcodeMatch?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default);

    Task<ItemCompanyPolicy?> CompanyPolicyAsync(Guid itemId, Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Every active item with planning parameters (a reorder point, a minimum or a maximum) in the warehouse.</summary>
    Task<IReadOnlyList<ItemPlanningInfo>> PlanningParametersAsync(Guid warehouseId, CancellationToken cancellationToken = default);

    /// <summary>The items whose warehouse settings put them in one of the cycle-count classes (A, B, C) for the warehouse.</summary>
    Task<IReadOnlyList<Guid>> ItemsForCycleCountAsync(Guid warehouseId, IReadOnlyList<string> classes, CancellationToken cancellationToken = default);
}

/// <summary>
/// The exact arithmetic of item units: rational factors, never floating point, never a silent rounding. A quantity
/// converts to the base unit as quantity × numerator ÷ denominator and back as quantity × denominator ÷ numerator; a
/// result is accepted only when it is a whole number of the target unit's precision, so a carton of 24 pieces bought
/// by the carton, stocked by the piece and sold by the dozen never drifts (hard scenario 9).
/// </summary>
public static class ItemUomMath
{
    public const int MaxPrecision = 9;

    public static Result<decimal> ToBase(decimal quantity, decimal numerator, decimal denominator, int basePrecision)
    {
        var raw = quantity * numerator / denominator;
        return IsExact(raw, basePrecision)
            ? raw
            : Error.Validation("quantity.not_exact_in_base", "The quantity is not a whole number of the base unit at its precision.")
                .WithWhy(("quantity", quantity), ("numerator", numerator), ("denominator", denominator), ("basePrecision", basePrecision), ("result", raw));
    }

    public static Result<decimal> FromBase(decimal baseQuantity, decimal numerator, decimal denominator, int precision)
    {
        var raw = baseQuantity * denominator / numerator;
        return IsExact(raw, precision)
            ? raw
            : Error.Validation("quantity.not_exact_in_uom", "The base quantity is not a whole number of the unit at its precision.")
                .WithWhy(("baseQuantity", baseQuantity), ("numerator", numerator), ("denominator", denominator), ("precision", precision), ("result", raw));
    }

    /// <summary>True when the value has no digits beyond <paramref name="precision"/> decimal places.</summary>
    public static bool IsExact(decimal value, int precision)
    {
        if (precision is < 0 or > MaxPrecision)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Precision is 0 to 9 decimal places.");
        }

        var scaled = value;
        for (var i = 0; i < precision; i++)
        {
            scaled *= 10m;
        }

        return decimal.Remainder(scaled, 1m) == 0m;
    }

    /// <summary>A decimal with trailing zeros removed, so "24.000000000000" and "24" compare and print alike.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}

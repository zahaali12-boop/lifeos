using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Items.Domain;

public sealed class Brand : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemCategory : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    /// <summary>Materialised path of codes from the root: "/ROOT/CHILD/" so a subtree is one prefix query.</summary>
    public string Path { get; set; } = string.Empty;

    public int Level { get; set; }

    public string? CostingMethodOverride { get; set; }

    public Guid? ItemPostingGroupId { get; set; }

    public Guid? ItemTaxGroupId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemAttribute : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ItemAttributeValue> Values { get; } = [];
}

public sealed class ItemAttributeValue : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid AttributeId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public int SortOrder { get; set; }
}

public sealed class Item : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    public Guid? CategoryId { get; set; }

    public Guid? BrandId { get; set; }

    /// <summary>stock, non_stock, service, kit, assembly.</summary>
    public string Type { get; set; } = "stock";

    public Guid BaseUomId { get; set; }

    public Guid? SalesUomId { get; set; }

    public Guid? PurchaseUomId { get; set; }

    /// <summary>none, lot, serial, lot_and_serial.</summary>
    public string Tracking { get; set; } = "none";

    public bool ExpiryRequired { get; set; }

    public int? ShelfLifeDays { get; set; }

    public bool Fefo { get; set; }

    public Guid? ItemPostingGroupId { get; set; }

    public Guid? ItemTaxGroupId { get; set; }

    public decimal? ListPrice { get; set; }

    public string? ListPriceCurrency { get; set; }

    public decimal? WeightKg { get; set; }

    public decimal? VolumeM3 { get; set; }

    public string? HsCode { get; set; }

    public string? CountryOfOrigin { get; set; }

    public bool HasVariants { get; set; }

    public Guid? ImageAttachmentId { get; set; }

    public bool IsActive { get; set; } = true;

    public string CustomFields { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ItemUom> Uoms { get; } = [];

    public List<ItemVariant> Variants { get; } = [];

    public List<ItemSupplier> Suppliers { get; } = [];
}

public sealed class ItemVariant : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    /// <summary>Attribute code → attribute value id.</summary>
    public Dictionary<string, Guid> AttributeValues { get; set; } = new(StringComparer.Ordinal);

    public Guid? ImageAttachmentId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemUom : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    public Guid UomId { get; set; }

    /// <summary>1 unit = Numerator / Denominator base units.</summary>
    public decimal Numerator { get; set; } = 1m;

    public decimal Denominator { get; set; } = 1m;

    public decimal? WeightKg { get; set; }

    /// <summary>Free JSON such as {"length_cm": 40, "width_cm": 30, "height_cm": 25}.</summary>
    public string Dimensions { get; set; } = "{}";

    public bool IsPurchaseDefault { get; set; }

    public bool IsSalesDefault { get; set; }

    public List<ItemBarcode> Barcodes { get; } = [];
}

public sealed class ItemBarcode : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ItemUomId { get; set; }

    public Guid? VariantId { get; set; }

    public string Barcode { get; set; } = string.Empty;

    public string Symbology { get; set; } = "EAN13";
}

public sealed class ItemSupplier : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    public Guid PartnerId { get; set; }

    public string? SupplierItemCode { get; set; }

    public Guid? UomId { get; set; }

    public int? LeadTimeDays { get; set; }

    public decimal? LastPrice { get; set; }

    public string? LastPriceCurrency { get; set; }

    public bool IsPreferred { get; set; }
}

public sealed class ItemCompanySettings : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid CompanyId { get; set; }

    public string? CostingMethodOverride { get; set; }

    public decimal? StandardCost { get; set; }

    public Guid? ItemPostingGroupOverride { get; set; }

    public Guid? DefaultWarehouseId { get; set; }

    public bool? AllowNegativeStock { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ItemWarehouseSettings : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WarehouseId { get; set; }

    public decimal? ReorderPoint { get; set; }

    public decimal? MinQty { get; set; }

    public decimal? MaxQty { get; set; }

    public decimal? SafetyStock { get; set; }

    public int? LeadTimeDays { get; set; }

    public Guid? DefaultBinId { get; set; }

    public string? CycleCountClass { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Bom : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>kit (components issued at shipment, no build) or assembly (built into stock).</summary>
    public string Kind { get; set; } = "assembly";

    public int Version { get; set; } = 1;

    public decimal OutputQty { get; set; } = 1m;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<BomLine> Lines { get; } = [];
}

public sealed class BomLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid BomId { get; set; }

    public int Position { get; set; }

    public Guid ComponentItemId { get; set; }

    public Guid? ComponentVariantId { get; set; }

    public decimal Quantity { get; set; }

    public Guid UomId { get; set; }

    public decimal ScrapPct { get; set; }
}

public sealed class Substitute : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid ItemId { get; set; }

    public Guid SubstituteItemId { get; set; }

    public int Priority { get; set; } = 1;
}

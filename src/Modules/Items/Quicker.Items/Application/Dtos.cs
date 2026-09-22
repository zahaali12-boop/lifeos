using System.Text.Json;

namespace Quicker.Items.Application;

// ------------------------------------------------------------------ brands, categories, attributes

public sealed record SaveBrandRequest(string Code, IReadOnlyDictionary<string, string> Name, bool IsActive = true);

public sealed record BrandSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveCategoryRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid? ParentId = null,
    string? ParentCode = null,
    string? CostingMethodOverride = null,
    Guid? ItemPostingGroupId = null,
    Guid? ItemTaxGroupId = null,
    bool IsActive = true);

public sealed record CategorySummary(
    Guid Id,
    Guid? ParentId,
    string? ParentCode,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Path,
    int Level,
    string? CostingMethodOverride,
    Guid? ItemPostingGroupId,
    Guid? ItemTaxGroupId,
    bool IsActive,
    int ItemCount,
    DateTimeOffset UpdatedAt);

public sealed record SaveAttributeRequest(string Code, IReadOnlyDictionary<string, string> Name, int SortOrder = 0, IReadOnlyList<SaveAttributeValueRequest>? Values = null);

public sealed record SaveAttributeValueRequest(string Code, IReadOnlyDictionary<string, string> Name, int SortOrder = 0);

public sealed record AttributeSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, int SortOrder, IReadOnlyList<AttributeValueSummary> Values, DateTimeOffset UpdatedAt);

public sealed record AttributeValueSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, int SortOrder);

// ------------------------------------------------------------------ items

public sealed record SaveItemRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Type = "stock",
    string? BaseUom = null,
    Guid? BaseUomId = null,
    IReadOnlyDictionary<string, string>? Description = null,
    string? CategoryCode = null,
    Guid? CategoryId = null,
    string? BrandCode = null,
    Guid? BrandId = null,
    string? SalesUom = null,
    string? PurchaseUom = null,
    string Tracking = "none",
    bool ExpiryRequired = false,
    int? ShelfLifeDays = null,
    bool Fefo = false,
    Guid? ItemPostingGroupId = null,
    string? ItemPostingGroupCode = null,
    Guid? ItemTaxGroupId = null,
    decimal? ListPrice = null,
    string? ListPriceCurrency = null,
    decimal? WeightKg = null,
    decimal? VolumeM3 = null,
    string? HsCode = null,
    string? CountryOfOrigin = null,
    bool IsActive = true,
    JsonElement? CustomFields = null,
    IReadOnlyList<SaveItemUomRequest>? Uoms = null,
    IReadOnlyList<SaveBarcodeRequest>? Barcodes = null);

/// <summary>1 <paramref name="Uom"/> = Numerator / Denominator base units (24 pieces per carton: 24/1; 1 piece = 1/24 carton is never stored, the inverse is derived).</summary>
public sealed record SaveItemUomRequest(
    string? Uom = null,
    Guid? UomId = null,
    decimal Numerator = 1m,
    decimal Denominator = 1m,
    decimal? WeightKg = null,
    JsonElement? Dimensions = null,
    bool IsPurchaseDefault = false,
    bool IsSalesDefault = false);

public sealed record SaveBarcodeRequest(string Barcode, string? Uom = null, Guid? UomId = null, string Symbology = "EAN13", Guid? VariantId = null, string? VariantSku = null);

public sealed record ItemUomSummary(Guid Id, Guid UomId, string UomCode, int Precision, decimal Numerator, decimal Denominator, bool IsBase, decimal? WeightKg, JsonElement Dimensions, bool IsPurchaseDefault, bool IsSalesDefault, IReadOnlyList<BarcodeSummary> Barcodes);

public sealed record BarcodeSummary(Guid Id, string Barcode, string Symbology, Guid ItemUomId, string UomCode, Guid? VariantId, string? VariantSku);

public sealed record ItemSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    string Type,
    Guid? CategoryId,
    string? CategoryCode,
    Guid? BrandId,
    string? BrandCode,
    Guid BaseUomId,
    string BaseUom,
    int BasePrecision,
    Guid? SalesUomId,
    string? SalesUom,
    Guid? PurchaseUomId,
    string? PurchaseUom,
    string Tracking,
    bool ExpiryRequired,
    int? ShelfLifeDays,
    bool Fefo,
    Guid? ItemPostingGroupId,
    Guid? ItemTaxGroupId,
    decimal? ListPrice,
    string? ListPriceCurrency,
    decimal? WeightKg,
    decimal? VolumeM3,
    string? HsCode,
    string? CountryOfOrigin,
    bool HasVariants,
    Guid? ImageAttachmentId,
    bool IsActive,
    JsonElement CustomFields,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ItemUomSummary>? Uoms = null,
    IReadOnlyList<VariantSummary>? Variants = null,
    IReadOnlyList<ItemSupplierSummary>? Suppliers = null,
    IReadOnlyList<SubstituteSummary>? Substitutes = null);

public sealed record SaveVariantRequest(string Sku, IReadOnlyDictionary<string, string>? Name = null, IReadOnlyDictionary<string, string>? AttributeValues = null, bool IsActive = true);

public sealed record VariantSummary(Guid Id, Guid ItemId, string Sku, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, VariantAttributeSummary> AttributeValues, Guid? ImageAttachmentId, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record VariantAttributeSummary(Guid ValueId, string ValueCode, IReadOnlyDictionary<string, string> ValueName);

public sealed record SaveItemSupplierRequest(Guid PartnerId, string? SupplierItemCode = null, string? Uom = null, Guid? UomId = null, int? LeadTimeDays = null, decimal? LastPrice = null, string? LastPriceCurrency = null, bool IsPreferred = false);

public sealed record ItemSupplierSummary(Guid Id, Guid PartnerId, string? SupplierItemCode, Guid? UomId, string? UomCode, int? LeadTimeDays, decimal? LastPrice, string? LastPriceCurrency, bool IsPreferred);

public sealed record SaveCompanySettingsRequest(string? CostingMethodOverride = null, decimal? StandardCost = null, Guid? ItemPostingGroupOverride = null, Guid? DefaultWarehouseId = null, bool? AllowNegativeStock = null);

public sealed record CompanySettingsSummary(Guid ItemId, Guid CompanyId, string? CostingMethodOverride, decimal? StandardCost, Guid? ItemPostingGroupOverride, Guid? DefaultWarehouseId, bool? AllowNegativeStock, DateTimeOffset UpdatedAt);

public sealed record SaveWarehouseSettingsRequest(decimal? ReorderPoint = null, decimal? MinQty = null, decimal? MaxQty = null, decimal? SafetyStock = null, int? LeadTimeDays = null, Guid? DefaultBinId = null, string? CycleCountClass = null);

public sealed record WarehouseSettingsSummary(Guid ItemId, Guid WarehouseId, decimal? ReorderPoint, decimal? MinQty, decimal? MaxQty, decimal? SafetyStock, int? LeadTimeDays, Guid? DefaultBinId, string? CycleCountClass, DateTimeOffset UpdatedAt);

public sealed record SaveSubstitutesRequest(IReadOnlyList<SubstituteRequest> Substitutes);

public sealed record SubstituteRequest(Guid? ItemId = null, string? ItemCode = null, int Priority = 1);

public sealed record SubstituteSummary(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> Name, int Priority);

public sealed record SetImageRequest(Guid? AttachmentId);

public sealed record ConversionResult(Guid ItemId, decimal Quantity, Guid FromUomId, string FromUom, decimal BaseQuantity, Guid BaseUomId, string BaseUom, Guid ToUomId, string ToUom, decimal Result);

public sealed record ItemImportRequest(IReadOnlyList<SaveItemRequest> Items);

public sealed record ItemImportResult(int Created, int Updated, int Total);

// ------------------------------------------------------------------ bills of material

public sealed record SaveBomRequest(string Kind, IReadOnlyList<SaveBomLineRequest> Lines, decimal OutputQty = 1m, bool Activate = true);

public sealed record SaveBomLineRequest(Guid? ComponentItemId = null, string? ComponentItemCode = null, Guid? ComponentVariantId = null, decimal Quantity = 1m, string? Uom = null, Guid? UomId = null, decimal ScrapPct = 0m);

public sealed record BomSummary(Guid Id, Guid ItemId, string ItemCode, string Kind, int Version, decimal OutputQty, bool IsActive, IReadOnlyList<BomLineSummary> Lines, DateTimeOffset UpdatedAt);

public sealed record BomLineSummary(Guid Id, int Position, Guid ComponentItemId, string ComponentItemCode, IReadOnlyDictionary<string, string> ComponentName, Guid? ComponentVariantId, string? ComponentVariantSku, decimal Quantity, Guid UomId, string UomCode, decimal BaseQuantity, string BaseUom, decimal ScrapPct);

/// <summary>A component requirement for a quantity of output, in the component's base unit, exact before scrap and after it.</summary>
public sealed record BomRequirement(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> Name, Guid? VariantId, string? VariantSku, Guid BaseUomId, string BaseUom, int BasePrecision, decimal Quantity, decimal QuantityWithScrap, int Depth, IReadOnlyList<string> Route);

public sealed record BomExplosion(Guid BomId, Guid ItemId, string ItemCode, decimal OutputQuantity, IReadOnlyList<BomRequirement> Requirements);

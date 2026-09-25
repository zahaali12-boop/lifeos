using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Items.Domain;
using Quicker.Items.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tax.Contracts;
using Quicker.Web;

namespace Quicker.Items.Application;

/// <summary>
/// The item master: items with their units (exact rational factors to the base unit, hard scenario 9), barcodes,
/// variants, suppliers, settings per company and per warehouse, substitutes, images, conversions, import and export.
/// </summary>
public sealed class ItemService(
    ItemsDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    IUomDirectory uoms,
    ICompanyDirectory companies,
    IPostingGroupDirectory postingGroups,
    IWarehouseDirectory warehouses,
    ICustomFieldValidator customFields,
    IStockActivity stock,
    ITaxGroupDirectory taxGroups,
    IAuditSink audit,
    IClock clock)
{
    public static readonly FilterSpec<Item> Filter = new FilterSpec<Item>()
        .Field("code", static i => i.Code)
        .Field("type", static i => i.Type)
        .Field("tracking", static i => i.Tracking)
        .Field("categoryId", static i => i.CategoryId)
        .Field("brandId", static i => i.BrandId)
        .Field("itemPostingGroupId", static i => i.ItemPostingGroupId)
        .Field("hasVariants", static i => i.HasVariants)
        .Field("isActive", static i => i.IsActive)
        .Field("updatedAt", static i => i.UpdatedAt)
        .CustomFields(static i => i.CustomFields);

    private IReadOnlyDictionary<Guid, UomInfo>? _uomsById;

    // ------------------------------------------------------------------ items

    /// <summary>Items newest first by id; <paramref name="q"/> matches the code or a name in any language, <paramref name="filter"/> is the filter language.</summary>
    public async Task<Result<Page<ItemSummary>>> ListAsync(string? filter, string? q, PageRequest page, CancellationToken cancellationToken)
    {
        IQueryable<Item> query = db.Items;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            query = db.Items.FromSqlInterpolated($"SELECT * FROM app.itm_items WHERE code ILIKE {pattern} OR name_i18n->>'en' ILIKE {pattern} OR name_i18n->>'ar' ILIKE {pattern}");
        }

        var filtered = Filter.Apply(query, filter);
        if (filtered.IsFailure)
        {
            return filtered.Error!;
        }

        var paged = await KeysetPaging.ByIdDescendingAsync(filtered.Value, static i => i.Id, page, cancellationToken);
        if (paged.IsFailure)
        {
            return paged.Error!;
        }

        var lookups = await LookupsAsync(paged.Value.Items, cancellationToken);
        return paged.Value.Map(i => Map(i, lookups));
    }

    public async Task<ItemSummary?> GetAsync(Guid itemId, string? expand, CancellationToken cancellationToken)
    {
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        return item is null ? null : await ExpandAsync(item, expand ?? "uoms,variants,suppliers,substitutes", cancellationToken);
    }

    public async Task<ItemSummary?> GetByCodeAsync(string code, string? expand, CancellationToken cancellationToken)
    {
        var normalized = code?.Trim() ?? string.Empty;
        var item = await db.Items.SingleOrDefaultAsync(i => i.Code == normalized, cancellationToken);
        return item is null ? null : await ExpandAsync(item, expand ?? "uoms,variants,suppliers,substitutes", cancellationToken);
    }

    public async Task<Result<ItemSummary>> CreateAsync(SaveItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var saved = await SaveAsync(null, request, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ExpandAsync(saved.Value, "uoms", cancellationToken);
    }

    public async Task<Result<ItemSummary>> UpdateAsync(Guid itemId, SaveItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.Include(static i => i.Uoms).ThenInclude(static u => u.Barcodes).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var saved = await SaveAsync(item, request, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ExpandAsync(saved.Value, "uoms", cancellationToken);
    }

    /// <summary>Creates or updates one item from a request; the caller saves. Used by create, update and import.</summary>
    private async Task<Result<Item>> SaveAsync(Item? item, SaveItemRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.Code(request.Code, "item");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "item");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var type = Validation.OneOf(request.Type, "item.type", Validation.ItemTypes);
        if (type.IsFailure)
        {
            return type.Error!;
        }

        var tracking = Validation.OneOf(request.Tracking, "item.tracking", Validation.Trackings);
        if (tracking.IsFailure)
        {
            return tracking.Error!;
        }

        if ((request.ExpiryRequired || request.Fefo) && tracking.Value is not ("lot" or "lot_and_serial"))
        {
            return Error.Validation("item.expiry_needs_lots", "Expiry dates and FEFO need lot tracking.").WithWhy(("tracking", tracking.Value));
        }

        if (request.ShelfLifeDays is <= 0)
        {
            return Error.Validation("item.shelf_life_invalid", "Shelf life is a positive number of days.");
        }

        foreach (var check in new[] { Validation.NonNegative(request.ListPrice, "item.list_price"), Validation.NonNegative(request.WeightKg, "item.weight_kg"), Validation.NonNegative(request.VolumeM3, "item.volume_m3") })
        {
            if (check.IsFailure)
            {
                return check.Error!;
            }
        }

        var currency = Validation.Currency(request.ListPriceCurrency, "item.list_price");
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (request.ListPrice is not null && currency.Value is null)
        {
            return Error.Validation("item.list_price_currency_required", "A list price names its currency.");
        }

        var isNew = item is null;
        if (await db.Items.AnyAsync(i => i.Code == code.Value && (item == null || i.Id != item.Id), cancellationToken))
        {
            return Error.Conflict("item.code_taken", $"An item with code '{code.Value}' already exists.").WithWhy(("code", code.Value));
        }

        var category = await ResolveCategoryAsync(request.CategoryId, request.CategoryCode, cancellationToken);
        if (category.IsFailure)
        {
            return category.Error!;
        }

        // The category's costing method applies to the item (ADR-0008): moving stock that has moved to a category with
        // another method would re-value its history.
        if (item is not null && item.CategoryId != category.Value?.Id)
        {
            var before = item.CategoryId is { } oldCategory ? await db.Categories.Where(c => c.Id == oldCategory).Select(static c => c.CostingMethodOverride).SingleOrDefaultAsync(cancellationToken) : null;
            var after = category.Value?.CostingMethodOverride;
            if (!string.Equals(before, after, StringComparison.Ordinal) && (await stock.ItemsWithMovementsAsync([item.Id], null, cancellationToken)).Count > 0)
            {
                return Error.Conflict("item.costing_locked", "The item's stock has moved under its category's costing method; it cannot move to a category with another method.").WithWhy(("costingMethodBefore", before), ("costingMethodAfter", after));
            }
        }

        var brand = await ResolveBrandAsync(request.BrandId, request.BrandCode, cancellationToken);
        if (brand.IsFailure)
        {
            return brand.Error!;
        }

        var postingGroup = await ResolvePostingGroupAsync(request.ItemPostingGroupId, request.ItemPostingGroupCode, cancellationToken);
        if (postingGroup.IsFailure)
        {
            return postingGroup.Error!;
        }

        if (request.ItemTaxGroupId is { } taxGroupId && await taxGroups.FindGroupAsync(taxGroupId, cancellationToken) is not { Kind: TaxGroupKinds.Item })
        {
            return Error.Validation("item.tax_group_invalid", "The tax group must be an item tax group.").WithWhy(("itemTaxGroupId", taxGroupId));
        }

        // Units: the base unit is required on create and immutable once the item has other units (their factors are
        // expressed in it); sales and purchase units must be units of the item.
        var byId = await UomsByIdAsync(cancellationToken);
        UomInfo? baseUom;
        if (request.BaseUomId is not null || !string.IsNullOrWhiteSpace(request.BaseUom))
        {
            var resolved = ResolveUom(byId, request.BaseUomId, request.BaseUom, "item.base_uom");
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            baseUom = resolved.Value;
            if (item is not null && baseUom.Id != item.BaseUomId && item.Uoms.Any(u => u.UomId != item.BaseUomId))
            {
                return Error.Conflict("item.base_uom_locked", "The base unit cannot change while the item has other units defined in it; remove them first.").WithWhy(("baseUom", byId[item.BaseUomId].Code));
            }
        }
        else if (item is null)
        {
            return Error.Validation("item.base_uom_required", "An item needs a base unit of measure.");
        }
        else
        {
            baseUom = byId[item.BaseUomId];
        }

        var customValues = request.CustomFields ?? (isNew ? null : JsonDocument.Parse(item!.CustomFields).RootElement);
        var validated = await customFields.ValidateAsync("item", customValues, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var now = clock.UtcNow;
        if (item is null)
        {
            item = new Item { Id = Guid.CreateVersion7(), CreatedAt = now };
            db.Items.Add(item);
        }

        var oldBase = item.BaseUomId;
        item.Code = code.Value;
        item.Name = name.Value;
        item.Description = Validation.OptionalText(request.Description);
        item.Type = type.Value;
        item.CategoryId = category.Value?.Id;
        item.BrandId = brand.Value?.Id;
        item.BaseUomId = baseUom.Id;
        item.Tracking = tracking.Value;
        item.ExpiryRequired = request.ExpiryRequired;
        item.ShelfLifeDays = request.ShelfLifeDays;
        item.Fefo = request.Fefo;
        item.ItemPostingGroupId = postingGroup.Value?.Id;
        item.ItemTaxGroupId = request.ItemTaxGroupId;
        item.ListPrice = request.ListPrice;
        item.ListPriceCurrency = request.ListPrice is null ? null : currency.Value;
        item.WeightKg = request.WeightKg;
        item.VolumeM3 = request.VolumeM3;
        item.HsCode = string.IsNullOrWhiteSpace(request.HsCode) ? null : request.HsCode.Trim();
        item.CountryOfOrigin = string.IsNullOrWhiteSpace(request.CountryOfOrigin) ? null : request.CountryOfOrigin.Trim().ToUpperInvariant();
        item.IsActive = request.IsActive;
        item.CustomFields = validated.Value;
        item.UpdatedAt = now;

        // The base unit always has its own row at 1/1 (so every unit of the item, base included, is one table).
        var baseRow = item.Uoms.FirstOrDefault(u => u.UomId == oldBase && !isNew) ?? item.Uoms.FirstOrDefault(u => u.UomId == baseUom.Id);
        if (baseRow is null)
        {
            baseRow = new ItemUom { Id = Guid.CreateVersion7(), ItemId = item.Id, UomId = baseUom.Id, Numerator = 1m, Denominator = 1m };
            item.Uoms.Add(baseRow);
        }
        else
        {
            baseRow.UomId = baseUom.Id;
            baseRow.Numerator = 1m;
            baseRow.Denominator = 1m;
        }

        if (request.Uoms is not null)
        {
            var applied = ApplyUoms(item, baseUom, request.Uoms, byId);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }
        }

        var salesUom = ResolveItemUom(item, byId, null, request.SalesUom, "item.sales_uom");
        if (salesUom.IsFailure)
        {
            return salesUom.Error!;
        }

        var purchaseUom = ResolveItemUom(item, byId, null, request.PurchaseUom, "item.purchase_uom");
        if (purchaseUom.IsFailure)
        {
            return purchaseUom.Error!;
        }

        item.SalesUomId = salesUom.Value?.UomId ?? item.Uoms.FirstOrDefault(static u => u.IsSalesDefault)?.UomId ?? (request.SalesUom is null && !isNew ? item.SalesUomId : null);
        item.PurchaseUomId = purchaseUom.Value?.UomId ?? item.Uoms.FirstOrDefault(static u => u.IsPurchaseDefault)?.UomId ?? (request.PurchaseUom is null && !isNew ? item.PurchaseUomId : null);
        if (item.SalesUomId is { } s && item.Uoms.All(u => u.UomId != s))
        {
            item.SalesUomId = null;
        }

        if (item.PurchaseUomId is { } p && item.Uoms.All(u => u.UomId != p))
        {
            item.PurchaseUomId = null;
        }

        if (request.Barcodes is not null)
        {
            var applied = await ApplyBarcodesAsync(item, request.Barcodes, byId, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }
        }

        return item;
    }

    private static Result ApplyUoms(Item item, UomInfo baseUom, IReadOnlyList<SaveItemUomRequest> requests, IReadOnlyDictionary<Guid, UomInfo> byId)
    {
        var wanted = new List<(UomInfo Uom, SaveItemUomRequest Request, decimal Numerator, decimal Denominator)>();
        foreach (var request in requests)
        {
            var uom = ResolveUom(byId, request.UomId, request.Uom, "item_uom");
            if (uom.IsFailure)
            {
                return uom.Error!;
            }

            if (wanted.Any(w => w.Uom.Id == uom.Value.Id))
            {
                return Error.Validation("item_uom.duplicate", "Each unit appears once per item.").WithWhy(("uom", uom.Value.Code));
            }

            var factor = Validation.Factor(request.Numerator, request.Denominator, "item_uom");
            if (factor.IsFailure)
            {
                return factor.Error!;
            }

            if (uom.Value.Id == baseUom.Id && (request.Numerator != request.Denominator))
            {
                return Error.Validation("item_uom.base_factor", "The base unit's factor is 1.").WithWhy(("uom", uom.Value.Code));
            }

            var weight = Validation.NonNegative(request.WeightKg, "item_uom.weight_kg");
            if (weight.IsFailure)
            {
                return weight.Error!;
            }

            wanted.Add((uom.Value, request, factor.Value.Numerator, factor.Value.Denominator));
        }

        if (wanted.Count(static w => w.Request.IsSalesDefault) > 1 || wanted.Count(static w => w.Request.IsPurchaseDefault) > 1)
        {
            return Error.Validation("item_uom.default_conflict", "At most one sales default and one purchase default unit.");
        }

        foreach (var existing in item.Uoms.Where(u => u.UomId != baseUom.Id && wanted.All(w => w.Uom.Id != u.UomId)).ToList())
        {
            item.Uoms.Remove(existing);
        }

        foreach (var (uom, request, numerator, denominator) in wanted)
        {
            var row = item.Uoms.FirstOrDefault(u => u.UomId == uom.Id);
            if (row is null)
            {
                row = new ItemUom { Id = Guid.CreateVersion7(), ItemId = item.Id, UomId = uom.Id };
                item.Uoms.Add(row);
            }

            row.Numerator = uom.Id == baseUom.Id ? 1m : numerator;
            row.Denominator = uom.Id == baseUom.Id ? 1m : denominator;
            row.WeightKg = request.WeightKg;
            row.Dimensions = Validation.JsonObject(request.Dimensions);
            row.IsPurchaseDefault = request.IsPurchaseDefault;
            row.IsSalesDefault = request.IsSalesDefault;
        }

        return Result.Success();
    }

    private async Task<Result> ApplyBarcodesAsync(Item item, IReadOnlyList<SaveBarcodeRequest> requests, IReadOnlyDictionary<Guid, UomInfo> byId, CancellationToken cancellationToken)
    {
        var wanted = new List<(ItemUom Row, Guid? VariantId, string Barcode, string Symbology)>();
        foreach (var request in requests)
        {
            var parsed = Validation.Barcode(request.Barcode, request.Symbology);
            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            var row = request.UomId is null && string.IsNullOrWhiteSpace(request.Uom)
                ? item.Uoms.First(u => u.UomId == item.BaseUomId)
                : (await Task.FromResult(ResolveItemUom(item, byId, request.UomId, request.Uom, "barcode.uom"))).Value;
            if (row is null)
            {
                return Error.Validation("barcode.uom_unknown", "The barcode's unit must be one of the item's units.").WithWhy(("uom", request.Uom ?? request.UomId?.ToString()));
            }

            Guid? variantId = null;
            if (request.VariantId is not null || !string.IsNullOrWhiteSpace(request.VariantSku))
            {
                var sku = request.VariantSku?.Trim();
                var variant = await db.Variants.SingleOrDefaultAsync(v => v.ItemId == item.Id && (request.VariantId != null ? v.Id == request.VariantId : v.Sku == sku), cancellationToken);
                if (variant is null)
                {
                    return Error.Validation("barcode.variant_unknown", "The barcode's variant must belong to the item.").WithWhy(("variant", request.VariantSku ?? request.VariantId?.ToString()));
                }

                variantId = variant.Id;
            }

            if (wanted.Any(w => w.Barcode == parsed.Value.Barcode))
            {
                return Error.Validation("barcode.duplicate", "Each barcode appears once.").WithWhy(("barcode", parsed.Value.Barcode));
            }

            wanted.Add((row, variantId, parsed.Value.Barcode, parsed.Value.Symbology));
        }

        var codes = wanted.Select(static w => w.Barcode).ToList();
        var itemUomIds = item.Uoms.Select(static u => u.Id).ToList();
        var taken = await db.Barcodes.Where(b => codes.Contains(b.Barcode) && !itemUomIds.Contains(b.ItemUomId)).Select(static b => b.Barcode).FirstOrDefaultAsync(cancellationToken);
        if (taken is not null)
        {
            return Error.Conflict("barcode.taken", "The barcode belongs to another item.").WithWhy(("barcode", taken));
        }

        foreach (var uom in item.Uoms)
        {
            foreach (var existing in uom.Barcodes.Where(b => wanted.All(w => w.Barcode != b.Barcode)).ToList())
            {
                uom.Barcodes.Remove(existing);
            }
        }

        foreach (var (row, variantId, barcode, symbology) in wanted)
        {
            var existing = item.Uoms.SelectMany(static u => u.Barcodes).FirstOrDefault(b => b.Barcode == barcode);
            if (existing is not null && existing.ItemUomId != row.Id)
            {
                item.Uoms.First(u => u.Id == existing.ItemUomId).Barcodes.Remove(existing);
                existing = null;
            }

            if (existing is null)
            {
                row.Barcodes.Add(new ItemBarcode { Id = Guid.CreateVersion7(), ItemUomId = row.Id, VariantId = variantId, Barcode = barcode, Symbology = symbology });
            }
            else
            {
                existing.VariantId = variantId;
                existing.Symbology = symbology;
            }
        }

        return Result.Success();
    }

    // ------------------------------------------------------------------ units and barcodes, one at a time

    public async Task<Result<ItemSummary>> SaveUomAsync(Guid itemId, SaveItemUomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.Include(static i => i.Uoms).ThenInclude(static u => u.Barcodes).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var merged = item.Uoms.Where(u => u.UomId != item.BaseUomId).Select(u => new SaveItemUomRequest(byId[u.UomId].Code, u.UomId, u.Numerator, u.Denominator, u.WeightKg, JsonDocument.Parse(u.Dimensions).RootElement, u.IsPurchaseDefault, u.IsSalesDefault)).ToList();
        var target = ResolveUom(byId, request.UomId, request.Uom, "item_uom");
        if (target.IsFailure)
        {
            return target.Error!;
        }

        merged.RemoveAll(m => m.UomId == target.Value.Id);
        merged.Add(request with { UomId = target.Value.Id, Uom = target.Value.Code });
        var applied = ApplyUoms(item, byId[item.BaseUomId], merged, byId);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (request.IsSalesDefault)
        {
            item.SalesUomId = target.Value.Id;
        }

        if (request.IsPurchaseDefault)
        {
            item.PurchaseUomId = target.Value.Id;
        }

        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await ExpandAsync(item, "uoms", cancellationToken);
    }

    public async Task<Result> DeleteUomAsync(Guid itemId, Guid itemUomId, CancellationToken cancellationToken)
    {
        var item = await db.Items.Include(static i => i.Uoms).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var row = item.Uoms.FirstOrDefault(u => u.Id == itemUomId);
        if (row is null)
        {
            return Error.NotFound("item_uom", itemUomId);
        }

        if (row.UomId == item.BaseUomId)
        {
            return Error.Conflict("item_uom.base_required", "The base unit cannot be removed.");
        }

        if (await db.BomLines.AnyAsync(l => l.ComponentItemId == itemId && l.UomId == row.UomId, cancellationToken))
        {
            return Error.Conflict("item_uom.in_use", "A bill of material uses this unit.");
        }

        item.Uoms.Remove(row);
        if (item.SalesUomId == row.UomId)
        {
            item.SalesUomId = null;
        }

        if (item.PurchaseUomId == row.UomId)
        {
            item.PurchaseUomId = null;
        }

        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<BarcodeSummary>> AddBarcodeAsync(Guid itemId, SaveBarcodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.Include(static i => i.Uoms).ThenInclude(static u => u.Barcodes).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var existing = item.Uoms.SelectMany(u => u.Barcodes.Select(b => new SaveBarcodeRequest(b.Barcode, byId[u.UomId].Code, u.UomId, b.Symbology, b.VariantId))).ToList();
        if (existing.Any(e => e.Barcode == request.Barcode?.Trim()))
        {
            return Error.Conflict("barcode.taken", "The item already carries this barcode.").WithWhy(("barcode", request.Barcode));
        }

        existing.Add(request);
        var applied = await ApplyBarcodesAsync(item, existing, byId, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var added = item.Uoms.SelectMany(static u => u.Barcodes).First(b => b.Barcode == request.Barcode!.Trim());
        var variants = await db.Variants.Where(v => v.ItemId == itemId).ToDictionaryAsync(static v => v.Id, static v => v.Sku, cancellationToken);
        var uom = item.Uoms.First(u => u.Id == added.ItemUomId);
        return new BarcodeSummary(added.Id, added.Barcode, added.Symbology, uom.Id, byId[uom.UomId].Code, added.VariantId, added.VariantId is { } v ? variants.GetValueOrDefault(v) : null);
    }

    public async Task<Result> DeleteBarcodeAsync(Guid itemId, Guid barcodeId, CancellationToken cancellationToken)
    {
        var item = await db.Items.Include(static i => i.Uoms).ThenInclude(static u => u.Barcodes).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var barcode = item.Uoms.SelectMany(static u => u.Barcodes).FirstOrDefault(b => b.Id == barcodeId);
        if (barcode is null)
        {
            return Error.NotFound("barcode", barcodeId);
        }

        item.Uoms.First(u => u.Id == barcode.ItemUomId).Barcodes.Remove(barcode);
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ variants

    public async Task<Result<IReadOnlyList<VariantSummary>>> ListVariantsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        var variants = await db.Variants.Where(v => v.ItemId == itemId).OrderBy(static v => v.Sku).ToListAsync(cancellationToken);
        var values = await AttributeValuesAsync(cancellationToken);
        return variants.Select(v => Map(v, values)).ToList();
    }

    public async Task<Result<VariantSummary>> SaveVariantAsync(Guid itemId, Guid? variantId, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var sku = Validation.Code(request.Sku, "variant", 64);
        if (sku.IsFailure)
        {
            return sku.Error!;
        }

        ItemVariant? variant = null;
        if (variantId is { } id)
        {
            variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == id && v.ItemId == itemId, cancellationToken);
            if (variant is null)
            {
                return Error.NotFound("variant", id);
            }
        }

        if (await db.Variants.AnyAsync(v => v.Sku == sku.Value && (variant == null || v.Id != variant.Id), cancellationToken))
        {
            return Error.Conflict("variant.sku_taken", $"A variant with SKU '{sku.Value}' already exists.").WithWhy(("sku", sku.Value));
        }

        var attributes = await db.Attributes.Include(static a => a.Values).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (attributeCode, valueCode) in request.AttributeValues ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!attributes.TryGetValue(attributeCode.Trim().ToUpperInvariant(), out var attribute))
            {
                return Error.Validation("variant.attribute_unknown", "Unknown attribute.").WithWhy(("attribute", attributeCode), ("known", attributes.Keys.Order(StringComparer.Ordinal)));
            }

            var wantedValue = valueCode.Trim().ToUpperInvariant();
            var value = attribute.Values.FirstOrDefault(v => v.Code == wantedValue);
            if (value is null)
            {
                return Error.Validation("variant.attribute_value_unknown", "Unknown attribute value.").WithWhy(("attribute", attribute.Code), ("value", valueCode), ("known", attribute.Values.Select(static v => v.Code).Order(StringComparer.Ordinal)));
            }

            resolved[attribute.Code] = value.Id;
        }

        if (resolved.Count > 0)
        {
            var siblings = await db.Variants.Where(v => v.ItemId == itemId && (variant == null || v.Id != variant.Id)).ToListAsync(cancellationToken);
            var duplicate = siblings.FirstOrDefault(s => s.AttributeValues.Count == resolved.Count && s.AttributeValues.All(kv => resolved.TryGetValue(kv.Key, out var v) && v == kv.Value));
            if (duplicate is not null)
            {
                return Error.Conflict("variant.combination_taken", "Another variant of the item has the same attribute values.").WithWhy(("sku", duplicate.Sku));
            }
        }

        var now = clock.UtcNow;
        if (variant is null)
        {
            variant = new ItemVariant { Id = Guid.CreateVersion7(), ItemId = itemId, CreatedAt = now };
            db.Variants.Add(variant);
            item.HasVariants = true;
            item.UpdatedAt = now;
        }

        variant.Sku = sku.Value;
        variant.Name = Validation.OptionalText(request.Name);
        variant.AttributeValues = resolved;
        variant.IsActive = request.IsActive;
        variant.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Map(variant, await AttributeValuesAsync(cancellationToken));
    }

    public async Task<Result> DeleteVariantAsync(Guid itemId, Guid variantId, CancellationToken cancellationToken)
    {
        var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == variantId && v.ItemId == itemId, cancellationToken);
        if (variant is null)
        {
            return Error.NotFound("variant", variantId);
        }

        if (await db.BomLines.AnyAsync(l => l.ComponentVariantId == variantId, cancellationToken))
        {
            return Error.Conflict("variant.in_use", "A bill of material uses this variant.");
        }

        db.Variants.Remove(variant);
        var item = await db.Items.SingleAsync(i => i.Id == itemId, cancellationToken);
        item.HasVariants = await db.Variants.AnyAsync(v => v.ItemId == itemId && v.Id != variantId, cancellationToken);
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ suppliers

    public async Task<Result<ItemSupplierSummary>> SaveSupplierAsync(Guid itemId, Guid? supplierId, SaveItemSupplierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.Include(static i => i.Uoms).Include(static i => i.Suppliers).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        if (request.PartnerId == Guid.Empty)
        {
            return Error.Validation("item_supplier.partner_required", "A supplier record names its partner.");
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var uom = ResolveItemUom(item, byId, request.UomId, request.Uom, "item_supplier.uom");
        if (uom.IsFailure)
        {
            return uom.Error!;
        }

        foreach (var check in new[] { Validation.NonNegative(request.LastPrice, "item_supplier.last_price") })
        {
            if (check.IsFailure)
            {
                return check.Error!;
            }
        }

        var lead = Validation.NonNegative(request.LeadTimeDays, "item_supplier.lead_time_days");
        if (lead.IsFailure)
        {
            return lead.Error!;
        }

        var currency = Validation.Currency(request.LastPriceCurrency, "item_supplier.last_price");
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (request.LastPrice is not null && currency.Value is null)
        {
            return Error.Validation("item_supplier.last_price_currency_required", "A price names its currency.");
        }

        var supplier = supplierId is { } id ? item.Suppliers.FirstOrDefault(s => s.Id == id) : null;
        if (supplierId is not null && supplier is null)
        {
            return Error.NotFound("item_supplier", supplierId);
        }

        if (item.Suppliers.Any(s => s.PartnerId == request.PartnerId && (supplier == null || s.Id != supplier.Id)))
        {
            return Error.Conflict("item_supplier.partner_taken", "The partner is already a supplier of this item.");
        }

        if (supplier is null)
        {
            supplier = new ItemSupplier { Id = Guid.CreateVersion7(), ItemId = itemId };
            item.Suppliers.Add(supplier);
        }

        supplier.PartnerId = request.PartnerId;
        supplier.SupplierItemCode = string.IsNullOrWhiteSpace(request.SupplierItemCode) ? null : request.SupplierItemCode.Trim();
        supplier.UomId = uom.Value?.UomId;
        supplier.LeadTimeDays = request.LeadTimeDays;
        supplier.LastPrice = request.LastPrice;
        supplier.LastPriceCurrency = request.LastPrice is null ? null : currency.Value;
        supplier.IsPreferred = request.IsPreferred;
        if (request.IsPreferred)
        {
            foreach (var other in item.Suppliers.Where(s => s.Id != supplier.Id))
            {
                other.IsPreferred = false;
            }
        }

        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(supplier, byId);
    }

    public async Task<Result> DeleteSupplierAsync(Guid itemId, Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == supplierId && s.ItemId == itemId, cancellationToken);
        if (supplier is null)
        {
            return Error.NotFound("item_supplier", supplierId);
        }

        db.Suppliers.Remove(supplier);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ settings per company and per warehouse

    public async Task<Result<IReadOnlyList<CompanySettingsSummary>>> ListCompanySettingsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        return (await db.CompanySettings.Where(s => s.ItemId == itemId).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<CompanySettingsSummary>> SaveCompanySettingsAsync(Guid itemId, Guid companyId, SaveCompanySettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var costing = Validation.OptionalOneOf(request.CostingMethodOverride, "item_settings.costing_method_override", Validation.CostingMethods);
        if (costing.IsFailure)
        {
            return costing.Error!;
        }

        var standard = Validation.NonNegative(request.StandardCost, "item_settings.standard_cost");
        if (standard.IsFailure)
        {
            return standard.Error!;
        }

        if (request.ItemPostingGroupOverride is { } groupId)
        {
            var group = await postingGroups.FindAsync(groupId, cancellationToken);
            if (group is null || group.Kind != "item")
            {
                return Error.Validation("item_settings.posting_group_invalid", "The posting group must be an item posting group.").WithWhy(("itemPostingGroupOverride", groupId));
            }
        }

        if (request.DefaultWarehouseId is { } defaultWarehouseId)
        {
            var warehouse = await warehouses.FindAsync(defaultWarehouseId, cancellationToken);
            if (warehouse is null || warehouse.CompanyId != companyId)
            {
                return Error.Validation("item_settings.default_warehouse_invalid", "The default warehouse must belong to the company.").WithWhy(("defaultWarehouseId", defaultWarehouseId), ("companyId", companyId));
            }
        }

        var settings = await db.CompanySettings.SingleOrDefaultAsync(s => s.ItemId == itemId && s.CompanyId == companyId, cancellationToken);
        if (!string.Equals(settings?.CostingMethodOverride, costing.Value, StringComparison.Ordinal) && (await stock.ItemsWithMovementsAsync([itemId], companyId, cancellationToken)).Count > 0)
        {
            return Error.Conflict("item_settings.costing_locked", "The item's stock has moved in this company under its costing method; it cannot change now.").WithWhy(("costingMethodOverride", settings?.CostingMethodOverride));
        }

        if (settings is null)
        {
            settings = new ItemCompanySettings { ItemId = itemId, CompanyId = companyId };
            db.CompanySettings.Add(settings);
        }

        settings.CostingMethodOverride = costing.Value;
        settings.StandardCost = request.StandardCost;
        settings.ItemPostingGroupOverride = request.ItemPostingGroupOverride;
        settings.DefaultWarehouseId = request.DefaultWarehouseId;
        settings.AllowNegativeStock = request.AllowNegativeStock;
        settings.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(settings);
    }

    public async Task<Result> DeleteCompanySettingsAsync(Guid itemId, Guid companyId, CancellationToken cancellationToken)
    {
        var settings = await db.CompanySettings.SingleOrDefaultAsync(s => s.ItemId == itemId && s.CompanyId == companyId, cancellationToken);
        if (settings is null)
        {
            return Error.NotFound("item_settings", companyId);
        }

        db.CompanySettings.Remove(settings);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<WarehouseSettingsSummary>>> ListWarehouseSettingsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        return (await db.WarehouseSettings.Where(s => s.ItemId == itemId).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<WarehouseSettingsSummary>> SaveWarehouseSettingsAsync(Guid itemId, Guid warehouseId, SaveWarehouseSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        if (warehouseId == Guid.Empty)
        {
            return Error.Validation("item_settings.warehouse_required", "Warehouse settings name their warehouse.");
        }

        if (await warehouses.FindAsync(warehouseId, cancellationToken) is null)
        {
            return Error.NotFound("warehouse", warehouseId);
        }

        foreach (var check in new[]
        {
            Validation.NonNegative(request.ReorderPoint, "item_settings.reorder_point"), Validation.NonNegative(request.MinQty, "item_settings.min_qty"),
            Validation.NonNegative(request.MaxQty, "item_settings.max_qty"), Validation.NonNegative(request.SafetyStock, "item_settings.safety_stock"),
        })
        {
            if (check.IsFailure)
            {
                return check.Error!;
            }
        }

        var lead = Validation.NonNegative(request.LeadTimeDays, "item_settings.lead_time_days");
        if (lead.IsFailure)
        {
            return lead.Error!;
        }

        if (request.MinQty is { } min && request.MaxQty is { } max && min > max)
        {
            return Error.Validation("item_settings.min_above_max", "The minimum cannot exceed the maximum.").WithWhy(("minQty", min), ("maxQty", max));
        }

        var cycle = Validation.OptionalOneOf(request.CycleCountClass?.ToUpperInvariant(), "item_settings.cycle_count_class", Validation.CycleCountClasses);
        if (cycle.IsFailure)
        {
            return cycle.Error!;
        }

        var settings = await db.WarehouseSettings.SingleOrDefaultAsync(s => s.ItemId == itemId && s.WarehouseId == warehouseId, cancellationToken);
        if (settings is null)
        {
            settings = new ItemWarehouseSettings { ItemId = itemId, WarehouseId = warehouseId };
            db.WarehouseSettings.Add(settings);
        }

        settings.ReorderPoint = request.ReorderPoint;
        settings.MinQty = request.MinQty;
        settings.MaxQty = request.MaxQty;
        settings.SafetyStock = request.SafetyStock;
        settings.LeadTimeDays = request.LeadTimeDays;
        settings.DefaultBinId = request.DefaultBinId;
        settings.CycleCountClass = cycle.Value;
        settings.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(settings);
    }

    public async Task<Result> DeleteWarehouseSettingsAsync(Guid itemId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var settings = await db.WarehouseSettings.SingleOrDefaultAsync(s => s.ItemId == itemId && s.WarehouseId == warehouseId, cancellationToken);
        if (settings is null)
        {
            return Error.NotFound("item_settings", warehouseId);
        }

        db.WarehouseSettings.Remove(settings);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ substitutes and images

    public async Task<Result<IReadOnlyList<SubstituteSummary>>> ReplaceSubstitutesAsync(Guid itemId, SaveSubstitutesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var wanted = new List<(Item Item, int Priority)>();
        foreach (var request1 in request.Substitutes)
        {
            var code = request1.ItemCode?.Trim();
            var substitute = request1.ItemId is { } id
                ? await db.Items.SingleOrDefaultAsync(i => i.Id == id, cancellationToken)
                : await db.Items.SingleOrDefaultAsync(i => i.Code == code, cancellationToken);
            if (substitute is null)
            {
                return Error.Validation("substitute.unknown", "The substitute item does not exist.").WithWhy(("item", request1.ItemCode ?? request1.ItemId?.ToString()));
            }

            if (substitute.Id == itemId)
            {
                return Error.Validation("substitute.self", "An item cannot substitute itself.");
            }

            if (wanted.Any(w => w.Item.Id == substitute.Id))
            {
                return Error.Validation("substitute.duplicate", "Each substitute appears once.").WithWhy(("item", substitute.Code));
            }

            if (request1.Priority < 1)
            {
                return Error.Validation("substitute.priority_invalid", "Priority starts at 1.");
            }

            wanted.Add((substitute, request1.Priority));
        }

        var existing = await db.Substitutes.Where(s => s.ItemId == itemId).ToListAsync(cancellationToken);
        db.Substitutes.RemoveRange(existing);
        await db.SaveChangesAsync(cancellationToken);
        db.Substitutes.AddRange(wanted.Select(w => new Substitute { ItemId = itemId, SubstituteItemId = w.Item.Id, Priority = w.Priority }));
        item.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return wanted.OrderBy(static w => w.Priority).ThenBy(static w => w.Item.Code, StringComparer.Ordinal).Select(static w => new SubstituteSummary(w.Item.Id, w.Item.Code, w.Item.Name.Values, w.Priority)).ToList();
    }

    public async Task<Result<ItemSummary>> SetImageAsync(Guid itemId, Guid? variantId, Guid? attachmentId, CancellationToken cancellationToken)
    {
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        if (variantId is { } vid)
        {
            var variant = await db.Variants.SingleOrDefaultAsync(v => v.Id == vid && v.ItemId == itemId, cancellationToken);
            if (variant is null)
            {
                return Error.NotFound("variant", vid);
            }

            variant.ImageAttachmentId = attachmentId;
            variant.UpdatedAt = clock.UtcNow;
        }
        else
        {
            item.ImageAttachmentId = attachmentId;
            item.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ExpandAsync(item, "variants", cancellationToken);
    }

    // ------------------------------------------------------------------ conversions

    public async Task<Result<ConversionResult>> ConvertAsync(Guid itemId, string? from, Guid? fromId, string? to, Guid? toId, decimal quantity, CancellationToken cancellationToken)
    {
        var item = await db.Items.Include(static i => i.Uoms).SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var source = ResolveItemUom(item, byId, fromId, from, "conversion.from");
        if (source.IsFailure)
        {
            return source.Error!;
        }

        var target = ResolveItemUom(item, byId, toId, to, "conversion.to");
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var sourceRow = source.Value ?? item.Uoms.First(u => u.UomId == item.BaseUomId);
        var targetRow = target.Value ?? item.Uoms.First(u => u.UomId == item.BaseUomId);
        var baseUom = byId[item.BaseUomId];
        var toBase = ItemUomMath.ToBase(quantity, sourceRow.Numerator, sourceRow.Denominator, baseUom.Precision);
        if (toBase.IsFailure)
        {
            return toBase.Error!;
        }

        var result = ItemUomMath.FromBase(toBase.Value, targetRow.Numerator, targetRow.Denominator, byId[targetRow.UomId].Precision);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return new ConversionResult(item.Id, quantity, sourceRow.UomId, byId[sourceRow.UomId].Code, ItemUomMath.Normalize(toBase.Value), baseUom.Id, baseUom.Code, targetRow.UomId, byId[targetRow.UomId].Code, ItemUomMath.Normalize(result.Value));
    }

    // ------------------------------------------------------------------ import and export

    public async Task<string> ExportCsvAsync(CancellationToken cancellationToken)
    {
        var items = await db.Items.Include(static i => i.Uoms).ThenInclude(static u => u.Barcodes).OrderBy(static i => i.Code).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(items, cancellationToken);
        var byId = await UomsByIdAsync(cancellationToken);
        return ItemCsv.Write(items.Select(i => Map(i, lookups) with { Uoms = MapUoms(i, byId, new Dictionary<Guid, string>()) }));
    }

    /// <summary>Upserts by code, all or nothing: the first failing row is named and nothing is written.</summary>
    public async Task<Result<ItemImportResult>> ImportAsync(IReadOnlyList<SaveItemRequest> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var created = 0;
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var code = row.Code?.Trim() ?? string.Empty;
            if (!seen.Add(code))
            {
                return Error.Validation("import.duplicate_code", "The same code appears twice in the import.").WithWhy(("row", i + 1), ("code", code));
            }

            var existing = await db.Items.Include(static x => x.Uoms).ThenInclude(static u => u.Barcodes).SingleOrDefaultAsync(x => x.Code == code, cancellationToken);
            var saved = await SaveAsync(existing, row, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error!.WithWhy(("row", i + 1), ("code", code));
            }

            await db.SaveChangesAsync(cancellationToken);
            if (existing is null)
            {
                created++;
            }
            else
            {
                updated++;
            }
        }

        await audit.RecordAsync(new AuditEntry("item", Guid.Empty, "import", "imported", After: new { created, updated, total = rows.Count }), cancellationToken);
        return new ItemImportResult(created, updated, rows.Count);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<IReadOnlyDictionary<Guid, UomInfo>> UomsByIdAsync(CancellationToken cancellationToken) =>
        _uomsById ??= (await uoms.ListAsync(cancellationToken)).ToDictionary(static u => u.Id);

    internal static Result<UomInfo> ResolveUom(IReadOnlyDictionary<Guid, UomInfo> byId, Guid? id, string? code, string field)
    {
        if (id is { } uomId)
        {
            return byId.TryGetValue(uomId, out var byIdMatch) ? byIdMatch : Error.Validation($"{field}_unknown", "Unknown unit of measure.").WithWhy(("uomId", uomId));
        }

        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var match = byId.Values.FirstOrDefault(u => u.Code == normalized);
        if (match is null)
        {
            return Error.Validation($"{field}_unknown", "Unknown unit of measure.").WithWhy(("uom", code));
        }

        return match;
    }

    /// <summary>One of the item's units by id or code; null when neither is given.</summary>
    private static Result<ItemUom?> ResolveItemUom(Item item, IReadOnlyDictionary<Guid, UomInfo> byId, Guid? id, string? code, string field)
    {
        if (id is null && string.IsNullOrWhiteSpace(code))
        {
            return (ItemUom?)null;
        }

        var uom = ResolveUom(byId, id, code, field);
        if (uom.IsFailure)
        {
            return uom.Error!;
        }

        var row = item.Uoms.FirstOrDefault(u => u.UomId == uom.Value.Id);
        return row is null
            ? Error.Validation($"{field}_not_item_uom", "The unit must be one of the item's units.").WithWhy(("uom", uom.Value.Code), ("itemUoms", item.Uoms.Select(u => byId[u.UomId].Code).Order(StringComparer.Ordinal)))
            : row;
    }

    private async Task<Result<ItemCategory?>> ResolveCategoryAsync(Guid? id, string? code, CancellationToken cancellationToken)
    {
        if (id is null && string.IsNullOrWhiteSpace(code))
        {
            return (ItemCategory?)null;
        }

        var normalized = code?.Trim().ToUpperInvariant();
        var category = id is { } cid ? await db.Categories.SingleOrDefaultAsync(c => c.Id == cid, cancellationToken) : await db.Categories.SingleOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        if (category is null)
        {
            return Error.Validation("item.category_unknown", "Unknown category.").WithWhy(("category", code ?? id?.ToString()));
        }

        return category;
    }

    private async Task<Result<Brand?>> ResolveBrandAsync(Guid? id, string? code, CancellationToken cancellationToken)
    {
        if (id is null && string.IsNullOrWhiteSpace(code))
        {
            return (Brand?)null;
        }

        var normalized = code?.Trim().ToUpperInvariant();
        var brand = id is { } bid ? await db.Brands.SingleOrDefaultAsync(b => b.Id == bid, cancellationToken) : await db.Brands.SingleOrDefaultAsync(b => b.Code == normalized, cancellationToken);
        if (brand is null)
        {
            return Error.Validation("item.brand_unknown", "Unknown brand.").WithWhy(("brand", code ?? id?.ToString()));
        }

        return brand;
    }

    private async Task<Result<PostingGroupInfo?>> ResolvePostingGroupAsync(Guid? id, string? code, CancellationToken cancellationToken)
    {
        if (id is null && string.IsNullOrWhiteSpace(code))
        {
            return (PostingGroupInfo?)null;
        }

        PostingGroupInfo? group;
        if (id is { } gid)
        {
            group = await postingGroups.FindAsync(gid, cancellationToken);
        }
        else
        {
            var normalized = code!.Trim().ToUpperInvariant();
            group = (await postingGroups.ListAsync("item", cancellationToken)).FirstOrDefault(g => g.Code == normalized);
        }

        return group is null || group.Kind != "item"
            ? Error.Validation("item.posting_group_invalid", "The posting group must be an existing item posting group.").WithWhy(("itemPostingGroup", code ?? id?.ToString()))
            : group;
    }

    private async Task<Dictionary<Guid, ItemAttributeValue>> AttributeValuesAsync(CancellationToken cancellationToken) =>
        await db.AttributeValues.ToDictionaryAsync(static v => v.Id, cancellationToken);

    private sealed record Lookups(IReadOnlyDictionary<Guid, string> Categories, IReadOnlyDictionary<Guid, string> Brands, IReadOnlyDictionary<Guid, UomInfo> Uoms);

    private async Task<Lookups> LookupsAsync(IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var categoryIds = items.Where(static i => i.CategoryId != null).Select(static i => i.CategoryId!.Value).Distinct().ToList();
        var brandIds = items.Where(static i => i.BrandId != null).Select(static i => i.BrandId!.Value).Distinct().ToList();
        var categories = categoryIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        var brands = brandIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Brands.Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(static b => b.Id, static b => b.Code, cancellationToken);
        return new Lookups(categories, brands, await UomsByIdAsync(cancellationToken));
    }

    private async Task<ItemSummary> ExpandAsync(Item item, string expand, CancellationToken cancellationToken)
    {
        var parts = expand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lookups = await LookupsAsync([item], cancellationToken);
        var summary = Map(item, lookups);
        if (parts.Contains("uoms"))
        {
            await db.Entry(item).Collection(static i => i.Uoms).LoadAsync(cancellationToken);
            foreach (var uom in item.Uoms)
            {
                await db.Entry(uom).Collection(static u => u.Barcodes).LoadAsync(cancellationToken);
            }

            var variantSkus = await db.Variants.Where(v => v.ItemId == item.Id).ToDictionaryAsync(static v => v.Id, static v => v.Sku, cancellationToken);
            summary = summary with { Uoms = MapUoms(item, lookups.Uoms, variantSkus) };
        }

        if (parts.Contains("variants"))
        {
            var values = await AttributeValuesAsync(cancellationToken);
            summary = summary with { Variants = (await db.Variants.Where(v => v.ItemId == item.Id).OrderBy(static v => v.Sku).ToListAsync(cancellationToken)).Select(v => Map(v, values)).ToList() };
        }

        if (parts.Contains("suppliers"))
        {
            summary = summary with { Suppliers = (await db.Suppliers.Where(s => s.ItemId == item.Id).OrderByDescending(static s => s.IsPreferred).ThenBy(static s => s.PartnerId).ToListAsync(cancellationToken)).Select(s => Map(s, lookups.Uoms)).ToList() };
        }

        if (parts.Contains("substitutes"))
        {
            var substitutes = await (from s in db.Substitutes
                                     join i in db.Items on s.SubstituteItemId equals i.Id
                                     where s.ItemId == item.Id
                                     orderby s.Priority, i.Code
                                     select new SubstituteSummary(i.Id, i.Code, i.Name.Values, s.Priority)).ToListAsync(cancellationToken);
            summary = summary with { Substitutes = substitutes };
        }

        return summary;
    }

    private static IReadOnlyList<ItemUomSummary> MapUoms(Item item, IReadOnlyDictionary<Guid, UomInfo> byId, IReadOnlyDictionary<Guid, string> variantSkus) =>
        item.Uoms.OrderByDescending(u => u.UomId == item.BaseUomId).ThenBy(u => byId[u.UomId].Code, StringComparer.Ordinal).Select(u =>
        {
            var info = byId[u.UomId];
            return new ItemUomSummary(u.Id, u.UomId, info.Code, info.Precision, ItemUomMath.Normalize(u.Numerator), ItemUomMath.Normalize(u.Denominator), u.UomId == item.BaseUomId, u.WeightKg, JsonDocument.Parse(u.Dimensions).RootElement.Clone(), u.IsPurchaseDefault, u.IsSalesDefault,
                u.Barcodes.OrderBy(static b => b.Barcode, StringComparer.Ordinal).Select(b => new BarcodeSummary(b.Id, b.Barcode, b.Symbology, u.Id, info.Code, b.VariantId, b.VariantId is { } v ? variantSkus.GetValueOrDefault(v) : null)).ToList());
        }).ToList();

    private static ItemSummary Map(Item i, Lookups lookups)
    {
        var baseUom = lookups.Uoms[i.BaseUomId];
        return new ItemSummary(i.Id, i.Code, i.Name.Values, i.Description.Values, i.Type, i.CategoryId, i.CategoryId is { } c ? lookups.Categories.GetValueOrDefault(c) : null, i.BrandId, i.BrandId is { } b ? lookups.Brands.GetValueOrDefault(b) : null,
            i.BaseUomId, baseUom.Code, baseUom.Precision, i.SalesUomId, i.SalesUomId is { } s ? lookups.Uoms.GetValueOrDefault(s)?.Code : null, i.PurchaseUomId, i.PurchaseUomId is { } p ? lookups.Uoms.GetValueOrDefault(p)?.Code : null,
            i.Tracking, i.ExpiryRequired, i.ShelfLifeDays, i.Fefo, i.ItemPostingGroupId, i.ItemTaxGroupId, i.ListPrice, i.ListPriceCurrency, i.WeightKg, i.VolumeM3, i.HsCode, i.CountryOfOrigin, i.HasVariants, i.ImageAttachmentId, i.IsActive,
            JsonDocument.Parse(i.CustomFields).RootElement.Clone(), i.UpdatedAt);
    }

    private static VariantSummary Map(ItemVariant v, IReadOnlyDictionary<Guid, ItemAttributeValue> values) =>
        new(v.Id, v.ItemId, v.Sku, v.Name.Values, v.AttributeValues.ToDictionary(static kv => kv.Key, kv => values.TryGetValue(kv.Value, out var value) ? new VariantAttributeSummary(value.Id, value.Code, value.Name.Values) : new VariantAttributeSummary(kv.Value, "?", new Dictionary<string, string>(StringComparer.Ordinal)), StringComparer.Ordinal), v.ImageAttachmentId, v.IsActive, v.UpdatedAt);

    private static ItemSupplierSummary Map(ItemSupplier s, IReadOnlyDictionary<Guid, UomInfo> byId) =>
        new(s.Id, s.PartnerId, s.SupplierItemCode, s.UomId, s.UomId is { } u ? byId.GetValueOrDefault(u)?.Code : null, s.LeadTimeDays, s.LastPrice, s.LastPriceCurrency, s.IsPreferred);

    private static CompanySettingsSummary Map(ItemCompanySettings s) => new(s.ItemId, s.CompanyId, s.CostingMethodOverride, s.StandardCost, s.ItemPostingGroupOverride, s.DefaultWarehouseId, s.AllowNegativeStock, s.UpdatedAt);

    private static WarehouseSettingsSummary Map(ItemWarehouseSettings s) => new(s.ItemId, s.WarehouseId, s.ReorderPoint, s.MinQty, s.MaxQty, s.SafetyStock, s.LeadTimeDays, s.DefaultBinId, s.CycleCountClass, s.UpdatedAt);

    /// <summary>Whether any variant of the tenant uses an attribute value (jsonb scan through the unit of work's connection).</summary>
    internal async Task<bool> AttributeValueInUseAsync(Guid valueId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        return await uow.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.itm_item_variants v, jsonb_each_text(v.attribute_values) kv WHERE kv.value = @value)",
            new { value = valueId.ToString() }, uow.Transaction, cancellationToken: cancellationToken));
    }

    internal async Task<bool> AttributeInUseAsync(string attributeCode, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        return await uow.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM app.itm_item_variants v WHERE jsonb_exists(v.attribute_values, @code))",
            new { code = attributeCode }, uow.Transaction, cancellationToken: cancellationToken));
    }
}

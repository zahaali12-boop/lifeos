using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Items.Application;
using Quicker.Items.Contracts;
using Quicker.Kernel.Results;
using Quicker.Web;

namespace Quicker.Items.Api;

/// <summary>The item master under /api/v1/items: items, units, barcodes, variants, suppliers, settings, substitutes, images, conversions, import/export, categories, brands, attributes, bills of material.</summary>
public static class ItemsEndpoints
{
    public static RouteGroupBuilder MapItemsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var items = api.MapGroup("/items").WithTags("Items").RequireAuthorization();

        // ------------------------------------------------------------------ items
        items.MapGet("/", async (string? filter, string? q, int? limit, string? cursor, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(filter, q, new PageRequest(limit, cursor), ct)))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("Items, newest first, paged: q matches the code or a name in any language; filter=type eq 'stock' and isActive eq true and cf.season eq 'summer'");
        items.MapPost("/", async (SaveItemRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static i => $"/api/v1/items/{i.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("An item with its base unit; other units carry exact factors to the base (24/1 for a carton of 24 pieces)");
        items.MapGet("/by-code/{code}", async (string code, string? expand, ItemService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetByCodeAsync(code, expand, ct), "item", code))
            .RequirePermission(ItemsPermissions.ItemRead);
        items.MapGet("/by-barcode/{barcode}", async (string barcode, IItemDirectory directory, CancellationToken ct) =>
            ApiProblems.Found(await directory.FindByBarcodeAsync(barcode, ct), "barcode", barcode))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("The item, unit and variant a scanned barcode stands for");
        items.MapGet("/export", async (ItemService service, CancellationToken ct) => Results.Text(await service.ExportCsvAsync(ct), "text/csv; charset=utf-8"))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("CSV with one row per item, units and barcodes packed (the import format)");
        items.MapPost("/import", async (HttpRequest http, ItemService service, CancellationToken ct) =>
        {
            IReadOnlyList<SaveItemRequest> rows;
            if (http.ContentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(http.Body);
                rows = ItemCsv.Read(await reader.ReadToEndAsync(ct));
            }
            else
            {
                var body = await http.ReadFromJsonAsync<ItemImportRequest>(ct);
                if (body is null)
                {
                    return ApiProblems.From(Error.Validation("import.body_required", "Send text/csv or a JSON body {items: [...]}."));
                }

                rows = body.Items;
            }

            return ApiProblems.From(await service.ImportAsync(rows, ct), static r => TypedResults.Ok(r));
        })
            .Accepts<ItemImportRequest>("application/json", "text/csv")
            .Produces<ItemImportResult>()
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Upsert by code from CSV or JSON, all or nothing: the first failing row is named");
        items.MapGet("/{itemId:guid}", async (Guid itemId, string? expand, ItemService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(itemId, expand, ct), "item", itemId))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("?expand=uoms,variants,suppliers,substitutes (all by default)");
        items.MapPut("/{itemId:guid}", async (Guid itemId, SaveItemRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(itemId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Units and barcodes given here replace the item's sets; omit them to leave them alone");
        items.MapGet("/{itemId:guid}/convert", async (Guid itemId, decimal quantity, string? from, Guid? fromId, string? to, Guid? toId, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ConvertAsync(itemId, from, fromId, to, toId, quantity, ct)))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("Exact conversion between two of the item's units through the base unit; a result that is not whole at the unit's precision is refused, never rounded");

        // ------------------------------------------------------------------ units, barcodes, variants, suppliers
        items.MapPut("/{itemId:guid}/uoms", async (Guid itemId, SaveItemUomRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveUomAsync(itemId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Adds or replaces one unit of the item");
        items.MapDelete("/{itemId:guid}/uoms/{itemUomId:guid}", async (Guid itemId, Guid itemUomId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteUomAsync(itemId, itemUomId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapPost("/{itemId:guid}/barcodes", async (Guid itemId, SaveBarcodeRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Created(await service.AddBarcodeAsync(itemId, request, ct), b => $"/api/v1/items/{itemId}/barcodes/{b.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("EAN-13, EAN-8 and UPC-A check digits are verified; a barcode belongs to one item in the tenant");
        items.MapDelete("/{itemId:guid}/barcodes/{barcodeId:guid}", async (Guid itemId, Guid barcodeId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteBarcodeAsync(itemId, barcodeId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapGet("/{itemId:guid}/variants", async (Guid itemId, ItemService service, CancellationToken ct) => ApiProblems.Ok(await service.ListVariantsAsync(itemId, ct)))
            .RequirePermission(ItemsPermissions.ItemRead);
        items.MapPost("/{itemId:guid}/variants", async (Guid itemId, SaveVariantRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveVariantAsync(itemId, null, request, ct), v => $"/api/v1/items/{itemId}/variants/{v.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("A variant by SKU with attribute values {COLOR: 'RED', SIZE: 'L'}; the combination is unique per item");
        items.MapPut("/{itemId:guid}/variants/{variantId:guid}", async (Guid itemId, Guid variantId, SaveVariantRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveVariantAsync(itemId, variantId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapDelete("/{itemId:guid}/variants/{variantId:guid}", async (Guid itemId, Guid variantId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteVariantAsync(itemId, variantId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapPost("/{itemId:guid}/suppliers", async (Guid itemId, SaveItemSupplierRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveSupplierAsync(itemId, null, request, ct), s => $"/api/v1/items/{itemId}/suppliers/{s.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("A supplier of the item (partner id, their item code, unit, lead time, last price); one preferred supplier per item");
        items.MapPut("/{itemId:guid}/suppliers/{supplierId:guid}", async (Guid itemId, Guid supplierId, SaveItemSupplierRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveSupplierAsync(itemId, supplierId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapDelete("/{itemId:guid}/suppliers/{supplierId:guid}", async (Guid itemId, Guid supplierId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteSupplierAsync(itemId, supplierId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);

        // ------------------------------------------------------------------ settings, substitutes, images
        items.MapGet("/{itemId:guid}/company-settings", async (Guid itemId, ItemService service, CancellationToken ct) => ApiProblems.Ok(await service.ListCompanySettingsAsync(itemId, ct)))
            .RequirePermission(ItemsPermissions.ItemRead);
        items.MapPut("/{itemId:guid}/company-settings/{companyId:guid}", async (Guid itemId, Guid companyId, SaveCompanySettingsRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveCompanySettingsAsync(itemId, companyId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Costing method, standard cost, posting group, default warehouse and negative-stock override for one company (null = the company's policy)");
        items.MapDelete("/{itemId:guid}/company-settings/{companyId:guid}", async (Guid itemId, Guid companyId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteCompanySettingsAsync(itemId, companyId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapGet("/{itemId:guid}/warehouse-settings", async (Guid itemId, ItemService service, CancellationToken ct) => ApiProblems.Ok(await service.ListWarehouseSettingsAsync(itemId, ct)))
            .RequirePermission(ItemsPermissions.ItemRead);
        items.MapPut("/{itemId:guid}/warehouse-settings/{warehouseId:guid}", async (Guid itemId, Guid warehouseId, SaveWarehouseSettingsRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveWarehouseSettingsAsync(itemId, warehouseId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Reorder point, min/max, safety stock, lead time, default bin and cycle-count class for one warehouse");
        items.MapDelete("/{itemId:guid}/warehouse-settings/{warehouseId:guid}", async (Guid itemId, Guid warehouseId, ItemService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteWarehouseSettingsAsync(itemId, warehouseId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        items.MapPut("/{itemId:guid}/substitutes", async (Guid itemId, SaveSubstitutesRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ReplaceSubstitutesAsync(itemId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Replaces the item's substitutes in priority order");
        items.MapPut("/{itemId:guid}/image", async (Guid itemId, SetImageRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetImageAsync(itemId, null, request.AttachmentId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("The item's picture: an attachment uploaded through /collaboration/attachments with entityType item");
        items.MapPut("/{itemId:guid}/variants/{variantId:guid}/image", async (Guid itemId, Guid variantId, SetImageRequest request, ItemService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetImageAsync(itemId, variantId, request.AttachmentId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);

        // ------------------------------------------------------------------ bills of material
        items.MapGet("/{itemId:guid}/boms", async (Guid itemId, BomService service, CancellationToken ct) => ApiProblems.Ok(await service.ListAsync(itemId, ct)))
            .RequirePermission(ItemsPermissions.BomRead);
        items.MapPost("/{itemId:guid}/boms", async (Guid itemId, SaveBomRequest request, BomService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(itemId, request, ct), static b => $"/api/v1/items/boms/{b.Id}"))
            .RequirePermission(ItemsPermissions.BomManage)
            .WithSummary("A new version of the item's bill (kit or assembly, matching the item type); activating it retires the previous version");
        var boms = items.MapGroup("/boms");
        boms.MapGet("/{bomId:guid}", async (Guid bomId, BomService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(bomId, ct), "bom", bomId))
            .RequirePermission(ItemsPermissions.BomRead);
        boms.MapPut("/{bomId:guid}", async (Guid bomId, SaveBomRequest request, BomService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(bomId, request, ct)))
            .RequirePermission(ItemsPermissions.BomManage);
        boms.MapPost("/{bomId:guid}/activate", async (Guid bomId, BomService service, CancellationToken ct) => ApiProblems.Ok(await service.ActivateAsync(bomId, ct)))
            .RequirePermission(ItemsPermissions.BomManage);
        boms.MapGet("/{bomId:guid}/explode", async (Guid bomId, decimal? quantity, BomService service, CancellationToken ct) => ApiProblems.Ok(await service.ExplodeAsync(bomId, quantity ?? 1m, ct)))
            .RequirePermission(ItemsPermissions.BomRead)
            .WithSummary("Every component through nested bills for a quantity of output, in base units, exact and with scrap");

        // ------------------------------------------------------------------ categories, brands, attributes
        var categories = items.MapGroup("/categories");
        categories.MapGet("/", async (CategoryService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .RequirePermission(ItemsPermissions.ItemRead)
            .WithSummary("The category tree in path order with level and item counts");
        categories.MapPost("/", async (SaveItemCategoryRequest request, CategoryService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static c => $"/api/v1/items/categories/{c.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage);
        categories.MapGet("/{categoryId:guid}", async (Guid categoryId, CategoryService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(categoryId, ct), "category", categoryId))
            .RequirePermission(ItemsPermissions.ItemRead);
        categories.MapPut("/{categoryId:guid}", async (Guid categoryId, SaveItemCategoryRequest request, CategoryService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(categoryId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("Renaming or moving a category rewrites the paths of its subtree; moving under a descendant is refused");
        categories.MapDelete("/{categoryId:guid}", async (Guid categoryId, CategoryService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(categoryId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);

        var brands = items.MapGroup("/brands");
        brands.MapGet("/", async (MasterDataService service, CancellationToken ct) => TypedResults.Ok(await service.ListBrandsAsync(ct)))
            .RequirePermission(ItemsPermissions.ItemRead);
        brands.MapPost("/", async (SaveBrandRequest request, MasterDataService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveBrandAsync(null, request, ct), static b => $"/api/v1/items/brands/{b.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage);
        brands.MapPut("/{brandId:guid}", async (Guid brandId, SaveBrandRequest request, MasterDataService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveBrandAsync(brandId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        brands.MapDelete("/{brandId:guid}", async (Guid brandId, MasterDataService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteBrandAsync(brandId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);

        var attributes = items.MapGroup("/attributes");
        attributes.MapGet("/", async (MasterDataService service, CancellationToken ct) => TypedResults.Ok(await service.ListAttributesAsync(ct)))
            .RequirePermission(ItemsPermissions.ItemRead);
        attributes.MapPost("/", async (SaveAttributeRequest request, MasterDataService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveAttributeAsync(null, request, ct), static a => $"/api/v1/items/attributes/{a.Id}"))
            .RequirePermission(ItemsPermissions.ItemManage)
            .WithSummary("An attribute (COLOR, SIZE) with its values; values are matched by code on update and cannot be removed while variants use them");
        attributes.MapPut("/{attributeId:guid}", async (Guid attributeId, SaveAttributeRequest request, MasterDataService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAttributeAsync(attributeId, request, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);
        attributes.MapDelete("/{attributeId:guid}", async (Guid attributeId, MasterDataService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAttributeAsync(attributeId, ct)))
            .RequirePermission(ItemsPermissions.ItemManage);

        return items;
    }
}

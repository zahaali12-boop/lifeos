using System.Net;
using System.Net.Http.Json;
using System.Text;
using Quicker.Identity.TestSupport;

namespace Quicker.Items.Tests;

/// <summary>Roadmap 3.1 through the API: categories, attributes, brands, items with units and barcodes, variants, suppliers, settings, substitutes, images, search, custom fields, import and export, audit.</summary>
[Collection(ApiCollection.Name)]
public sealed class ItemMasterTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task Categories_form_a_tree_with_materialised_paths_that_moves_and_refuses_cycles()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var beverages = await owner.PostAsync("/api/v1/items/categories", new { code = "bev", name = Name("Beverages", "مشروبات") });
        beverages.GetProperty("code").GetString().ShouldBe("BEV");
        beverages.GetProperty("path").GetString().ShouldBe("/BEV/");
        var water = await owner.PostAsync("/api/v1/items/categories", new { code = "WATER", name = Name("Water", "ماء"), parentCode = "BEV" });
        var sparkling = await owner.PostAsync("/api/v1/items/categories", new { code = "SPARK", name = Name("Sparkling", "غازي"), parentId = water.GetProperty("id").GetGuid(), costingMethodOverride = "fifo" });
        sparkling.GetProperty("path").GetString().ShouldBe("/BEV/WATER/SPARK/");
        sparkling.GetProperty("level").GetInt32().ShouldBe(2);
        var food = await owner.PostAsync("/api/v1/items/categories", new { code = "FOOD", name = Name("Food", "غذاء") });

        (await owner.PostErrorAsync("/api/v1/items/categories", new { code = "BEV", name = Name("Again", "مرة") }, HttpStatusCode.Conflict)).Code.ShouldBe("category.code_taken");
        (await owner.PostErrorAsync("/api/v1/items/categories", new { code = "X", name = Name("X", "X"), parentCode = "NOPE" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("category.parent_unknown");
        (await owner.PostErrorAsync("/api/v1/items/categories", new { code = "Y", name = Name("Y", "Y"), costingMethodOverride = "lifo" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("category.costing_method_override.invalid");

        // Moving BEV under its grandchild is a cycle; moving WATER under FOOD rewrites the subtree.
        var bevId = beverages.GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/items/categories/{bevId}", new { code = "BEV", name = Name("Beverages", "مشروبات"), parentCode = "SPARK" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("category.cycle");
        var moved = await owner.PutAsync($"/api/v1/items/categories/{water.GetProperty("id").GetGuid()}", new { code = "WATER", name = Name("Water", "ماء"), parentCode = "FOOD" });
        moved.GetProperty("path").GetString().ShouldBe("/FOOD/WATER/");
        var tree = await owner.GetOkAsync("/api/v1/items/categories");
        var paths = tree.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => (c.GetProperty("path").GetString(), c.GetProperty("level").GetInt32()));
        paths["SPARK"].ShouldBe(("/FOOD/WATER/SPARK/", 2));
        paths["BEV"].ShouldBe(("/BEV/", 0));

        // A category with children or items cannot be deleted.
        (await owner.DeleteAsync(new Uri($"/api/v1/items/categories/{food.GetProperty("id").GetGuid()}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await owner.DeleteOkAsync($"/api/v1/items/categories/{bevId}");
    }

    [Fact]
    public async Task An_item_carries_exact_units_verified_barcodes_variants_suppliers_settings_and_substitutes()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        await owner.PostAsync("/api/v1/items/brands", new { code = "aqua", name = Name("Aqua", "أكوا") });
        var color = await owner.PostAsync("/api/v1/items/attributes", new { code = "COLOR", name = Name("Colour", "اللون"), values = new object[] { new { code = "RED", name = Name("Red", "أحمر") }, new { code = "BLUE", name = Name("Blue", "أزرق") } } });
        await owner.PostAsync("/api/v1/items/attributes", new { code = "SIZE", name = Name("Size", "المقاس"), values = new object[] { new { code = "S", name = Name("Small", "صغير") }, new { code = "L", name = Name("Large", "كبير") } } });
        await owner.PostAsync("/api/v1/items/categories", new { code = "BOTTLES", name = Name("Bottles", "قوارير") });

        // The item: pieces as the base, cartons of 24 with an EAN-13, dozens; the check digit is verified.
        var request = new
        {
            code = "BTL-01",
            name = Name("Sports bottle", "قارورة رياضية"),
            type = "stock",
            baseUom = "PCS",
            categoryCode = "BOTTLES",
            brandCode = "AQUA",
            tracking = "lot",
            expiryRequired = true,
            shelfLifeDays = 365,
            listPrice = 2.5,
            listPriceCurrency = "usd",
            weightKg = 0.12,
            uoms = new object[] { new { uom = "CTN", numerator = 24, denominator = 1, isPurchaseDefault = true, weightKg = 3.1, dimensions = new { length_cm = 40, width_cm = 30 } }, new { uom = "DZ", numerator = 12, denominator = 1, isSalesDefault = true } },
            barcodes = new object[] { new { barcode = "6291041500213", uom = "PCS" }, new { barcode = "16291041500210", uom = "CTN", symbology = "CODE128" } },
        };
        var item = await owner.PostAsync("/api/v1/items", request);
        var itemId = item.GetProperty("id").GetGuid();
        item.GetProperty("categoryCode").GetString().ShouldBe("BOTTLES");
        item.GetProperty("brandCode").GetString().ShouldBe("AQUA");
        item.GetProperty("listPriceCurrency").GetString().ShouldBe("USD");
        item.GetProperty("purchaseUom").GetString().ShouldBe("CTN");
        item.GetProperty("salesUom").GetString().ShouldBe("DZ");
        var uoms = item.GetProperty("uoms").EnumerateArray().ToList();
        uoms.Count.ShouldBe(3);
        uoms[0].GetProperty("isBase").GetBoolean().ShouldBeTrue();
        uoms.Single(u => u.GetProperty("uomCode").GetString() == "CTN").GetProperty("dimensions").GetProperty("length_cm").GetInt32().ShouldBe(40);
        uoms.SelectMany(u => u.GetProperty("barcodes").EnumerateArray()).Count().ShouldBe(2);

        (await owner.PostErrorAsync("/api/v1/items", request, HttpStatusCode.Conflict)).Code.ShouldBe("item.code_taken");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-1", name = Name("x", "x"), baseUom = "PCS", barcodes = new object[] { new { barcode = "6291041500214" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("barcode.check_digit");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-2", name = Name("x", "x"), baseUom = "PCS", barcodes = new object[] { new { barcode = "6291041500213" } } }, HttpStatusCode.Conflict)).Code.ShouldBe("barcode.taken");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-3", name = Name("x", "x"), baseUom = "PCS", uoms = new object[] { new { uom = "CTN", numerator = 0, denominator = 1 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item_uom.factor_invalid");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-4", name = Name("x", "x"), baseUom = "PCS", salesUom = "CTN" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.sales_uom_not_item_uom");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-5", name = Name("x", "x"), baseUom = "PCS", expiryRequired = true }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.expiry_needs_lots");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-6", name = Name("x", "x"), baseUom = "NOPE" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.base_uom_unknown");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-7", name = Name("x", "x") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.base_uom_required");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD-8", name = Name("x", "x"), baseUom = "PCS", listPrice = 1 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.list_price_currency_required");

        // A barcode lookup answers with the item, unit and variant; the base unit cannot go while others depend on it.
        var scanned = await owner.GetOkAsync("/api/v1/items/by-barcode/16291041500210");
        scanned.GetProperty("itemCode").GetString().ShouldBe("BTL-01");
        scanned.GetProperty("uomCode").GetString().ShouldBe("CTN");
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}", new { code = "BTL-01", name = Name("Sports bottle", "قارورة رياضية"), baseUom = "DZ" }, HttpStatusCode.Conflict)).Code.ShouldBe("item.base_uom_locked");
        var baseUomRow = uoms[0].GetProperty("id").GetGuid();
        (await owner.DeleteAsync(new Uri($"/api/v1/items/{itemId}/uoms/{baseUomRow}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var withPallet = await owner.PutAsync($"/api/v1/items/{itemId}/uoms", new { uom = "PLT", numerator = 1440, denominator = 1 });
        withPallet.GetProperty("uoms").GetArrayLength().ShouldBe(4);
        var pallet = withPallet.GetProperty("uoms").EnumerateArray().Single(u => u.GetProperty("uomCode").GetString() == "PLT").GetProperty("id").GetGuid();
        await owner.DeleteOkAsync($"/api/v1/items/{itemId}/uoms/{pallet}");

        // Variants by attribute values: unique SKU and unique combination per item.
        var red = await owner.PostAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-RED-S", attributeValues = new { color = "red", size = "s" } });
        red.GetProperty("attributeValues").GetProperty("COLOR").GetProperty("valueCode").GetString().ShouldBe("RED");
        await owner.PostAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-BLUE-S", attributeValues = new { COLOR = "BLUE", SIZE = "S" } });
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-RED-S", attributeValues = new { COLOR = "RED", SIZE = "L" } }, HttpStatusCode.Conflict)).Code.ShouldBe("variant.sku_taken");
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-RED-S2", attributeValues = new { COLOR = "RED", SIZE = "S" } }, HttpStatusCode.Conflict)).Code.ShouldBe("variant.combination_taken");
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-X", attributeValues = new { FLAVOUR = "MINT" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("variant.attribute_unknown");
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/variants", new { sku = "BTL-01-Y", attributeValues = new { COLOR = "GREEN" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("variant.attribute_value_unknown");
        (await owner.GetOkAsync($"/api/v1/items/{itemId}")).GetProperty("hasVariants").GetBoolean().ShouldBeTrue();
        var variantBarcode = await owner.PostAsync($"/api/v1/items/{itemId}/barcodes", new { barcode = "4006381333931", uom = "PCS", variantSku = "BTL-01-RED-S" });
        variantBarcode.GetProperty("variantSku").GetString().ShouldBe("BTL-01-RED-S");
        (await owner.GetOkAsync("/api/v1/items/by-barcode/4006381333931")).GetProperty("variantId").GetGuid().ShouldBe(red.GetProperty("id").GetGuid());
        // The attribute value in use cannot be removed from the attribute; unused ones can.
        var colorId = color.GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/items/attributes/{colorId}", new { code = "COLOR", name = Name("Colour", "اللون"), values = new object[] { new { code = "BLUE", name = Name("Blue", "أزرق") } } }, HttpStatusCode.Conflict)).Code.ShouldBe("attribute_value.in_use");
        (await owner.PutAsync($"/api/v1/items/attributes/{colorId}", new { code = "COLOR", name = Name("Colour", "اللون"), values = new object[] { new { code = "RED", name = Name("Red", "أحمر") }, new { code = "BLUE", name = Name("Blue", "أزرق") }, new { code = "GREEN", name = Name("Green", "أخضر") } } })).GetProperty("values").GetArrayLength().ShouldBe(3);

        // Suppliers: one preferred at a time; settings per company and per warehouse; substitutes in priority order.
        var partnerA = Guid.CreateVersion7();
        var partnerB = Guid.CreateVersion7();
        var supplierA = await owner.PostAsync($"/api/v1/items/{itemId}/suppliers", new { partnerId = partnerA, supplierItemCode = "A-778", uom = "CTN", leadTimeDays = 14, lastPrice = 40, lastPriceCurrency = "USD", isPreferred = true });
        supplierA.GetProperty("uomCode").GetString().ShouldBe("CTN");
        await owner.PostAsync($"/api/v1/items/{itemId}/suppliers", new { partnerId = partnerB, isPreferred = true });
        var suppliers = (await owner.GetOkAsync($"/api/v1/items/{itemId}?expand=suppliers")).GetProperty("suppliers").EnumerateArray().ToList();
        suppliers.Count.ShouldBe(2);
        suppliers.Single(s => s.GetProperty("isPreferred").GetBoolean()).GetProperty("partnerId").GetGuid().ShouldBe(partnerB);
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/suppliers", new { partnerId = partnerA }, HttpStatusCode.Conflict)).Code.ShouldBe("item_supplier.partner_taken");
        (await owner.PostErrorAsync($"/api/v1/items/{itemId}/suppliers", new { partnerId = Guid.CreateVersion7(), uom = "PLT" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item_supplier.uom_not_item_uom");
        await owner.DeleteOkAsync($"/api/v1/items/{itemId}/suppliers/{supplierA.GetProperty("id").GetGuid()}");

        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IQT", legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        var settings = await owner.PutAsync($"/api/v1/items/{itemId}/company-settings/{companyId}", new { costingMethodOverride = "standard", standardCost = 1.2345678901, allowNegativeStock = false });
        settings.Dec("standardCost").ShouldBe(1.2345678901m);
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/company-settings/{Guid.CreateVersion7()}", new { standardCost = 1 }, HttpStatusCode.NotFound)).Code.ShouldBe("company.not_found");
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/company-settings/{companyId}", new { costingMethodOverride = "lifo" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item_settings.costing_method_override.invalid");
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/warehouse-settings/{Guid.CreateVersion7()}", new { minQty = 1 }, HttpStatusCode.NotFound)).Code.ShouldBe("warehouse.not_found");
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/company-settings/{companyId}", new { defaultWarehouseId = Guid.CreateVersion7() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item_settings.default_warehouse_invalid");
        (await owner.PutAsync($"/api/v1/items/{itemId}/company-settings/{companyId}", new { defaultWarehouseId = warehouseId })).GetProperty("defaultWarehouseId").GetGuid().ShouldBe(warehouseId);
        (await owner.PutAsync($"/api/v1/items/{itemId}/warehouse-settings/{warehouseId}", new { reorderPoint = 100, minQty = 50, maxQty = 500, safetyStock = 20, leadTimeDays = 7, cycleCountClass = "a" })).GetProperty("cycleCountClass").GetString().ShouldBe("A");
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/warehouse-settings/{warehouseId}", new { minQty = 500, maxQty = 50 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item_settings.min_above_max");
        (await owner.GetOkAsync($"/api/v1/items/{itemId}/warehouse-settings")).GetArrayLength().ShouldBe(1);

        var alt = await owner.PostAsync("/api/v1/items", new { code = "BTL-02", name = Name("Sports bottle II", "قارورة رياضية ٢"), baseUom = "PCS" });
        var subs = await owner.PutAsync($"/api/v1/items/{itemId}/substitutes", new { substitutes = new object[] { new { itemCode = "BTL-02", priority = 1 } } });
        subs.GetArrayLength().ShouldBe(1);
        (await owner.PutErrorAsync($"/api/v1/items/{itemId}/substitutes", new { substitutes = new object[] { new { itemCode = "BTL-01" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("substitute.self");
        var attachment = Guid.CreateVersion7();
        (await owner.PutAsync($"/api/v1/items/{itemId}/image", new { attachmentId = attachment })).GetProperty("imageAttachmentId").GetGuid().ShouldBe(attachment);

        // Search and filter, paged, newest first.
        var list = await owner.GetOkAsync("/api/v1/items?q=sports&limit=1");
        list.GetProperty("items").GetArrayLength().ShouldBe(1);
        list.GetProperty("items")[0].GetProperty("code").GetString().ShouldBe("BTL-02");
        list.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        var second = await owner.GetOkAsync("/api/v1/items?q=sports&limit=1&cursor=" + list.GetProperty("nextCursor").GetString());
        second.GetProperty("items")[0].GetProperty("code").GetString().ShouldBe("BTL-01");
        (await owner.GetOkAsync("/api/v1/items?q=قارورة")).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync("/api/v1/items?filter=tracking eq 'lot' and hasVariants eq true")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await owner.GetErrorAsync("/api/v1/items?filter=colour eq 'x'", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("filter.field_unknown");
        (await owner.GetOkAsync("/api/v1/items/by-code/BTL-02")).GetProperty("id").GetGuid().ShouldBe(alt.GetProperty("id").GetGuid());

        // The audit trail carries the item's life: created, then the field-level change of the update.
        var full = await owner.GetOkAsync($"/api/v1/items/{itemId}");
        await owner.PutAsync($"/api/v1/items/{itemId}", new { code = "BTL-01", name = Name("Sports bottle 750", "قارورة رياضية ٧٥٠"), tracking = "lot", expiryRequired = true, shelfLifeDays = 365, listPrice = 2.5, listPriceCurrency = "USD", weightKg = 0.12, categoryCode = "BOTTLES", brandCode = "AQUA" });
        var timeline = await owner.GetOkAsync($"/api/v1/audit/records/item/{itemId}");
        var actions = timeline.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("created");
        actions.ShouldContain("updated");
        full.GetProperty("uoms").GetArrayLength().ShouldBe(3, "units untouched by an update that does not send them");
        (await owner.GetOkAsync($"/api/v1/items/{itemId}")).GetProperty("uoms").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Items_round_trip_through_csv_and_json_import_is_all_or_nothing_with_custom_fields_validated()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        await owner.PostAsync("/api/v1/collaboration/custom-fields", new { entityType = "item", key = "season", label = Name("Season", "الموسم"), type = "select", options = new[] { new { value = "summer", label = Name("Summer", "صيف") }, new { value = "winter", label = Name("Winter", "شتاء") } }, required = false });
        await owner.PostAsync("/api/v1/items/categories", new { code = "DRINKS", name = Name("Drinks", "مشروبات") });
        var created = await owner.PostAsync("/api/v1/items", new
        {
            code = "JUICE-1L",
            name = Name("Orange juice 1 L", "عصير برتقال ١ لتر"),
            description = Name("Cold pressed", "معصور على البارد"),
            baseUom = "PCS",
            categoryCode = "DRINKS",
            customFields = new { season = "summer" },
            uoms = new object[] { new { uom = "CTN", numerator = 6, denominator = 1, isPurchaseDefault = true } },
            barcodes = new object[] { new { barcode = "5901234123457" }, new { barcode = "JUICE-CTN-1", uom = "CTN", symbology = "CODE128" } },
        });
        created.GetProperty("customFields").GetProperty("season").GetString().ShouldBe("summer");
        (await owner.PostErrorAsync("/api/v1/items", new { code = "JUICE-2L", name = Name("x", "x"), baseUom = "PCS", customFields = new { season = "spring" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("custom_field.value_invalid");

        var csv = await (await owner.GetAsync(new Uri("/api/v1/items/export", UriKind.Relative))).Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("code,name_en,name_ar");
        lines[1].ShouldContain("JUICE-1L");
        lines[1].ShouldContain("CTN:6/1");
        lines[1].ShouldContain("PCS=5901234123457|CTN=JUICE-CTN-1:CODE128");

        // Re-import the export with a change and a new row: upsert by code, units and barcodes replaced from the packed columns.
        var edited = csv.Replace("Orange juice 1 L", "Orange juice 1 litre", StringComparison.Ordinal)
            + "APPLE-1L,Apple juice 1 L,عصير تفاح,,,stock,DRINKS,,PCS,,CTN,CTN:6/1,PCS=4006381333931,none,false,,false,1.5,USD,,,,,true\n";
        var imported = await owner.PostContentAsync("/api/v1/items/import", new StringContent(edited, Encoding.UTF8, "text/csv"));
        imported.GetProperty("created").GetInt32().ShouldBe(1);
        imported.GetProperty("updated").GetInt32().ShouldBe(1);
        var juice = await owner.GetOkAsync("/api/v1/items/by-code/JUICE-1L");
        juice.GetProperty("name").GetProperty("en").GetString().ShouldBe("Orange juice 1 litre");
        juice.GetProperty("customFields").GetProperty("season").GetString().ShouldBe("summer", "an import without custom fields keeps them");
        juice.GetProperty("uoms").GetArrayLength().ShouldBe(2);
        var apple = await owner.GetOkAsync("/api/v1/items/by-code/APPLE-1L");
        apple.GetProperty("purchaseUom").GetString().ShouldBe("CTN");
        apple.Dec("listPrice").ShouldBe(1.5m);

        // JSON import: the third row fails, nothing of the batch is written, and the row is named.
        var (code, problem) = await owner.PostErrorAsync("/api/v1/items/import", new
        {
            items = new object[]
            {
                new { code = "PEAR-1L", name = Name("Pear", "كمثرى"), baseUom = "PCS" },
                new { code = "JUICE-1L", name = Name("Orange juice 1 L", "عصير برتقال"), baseUom = "PCS" },
                new { code = "GRAPE-1L", name = Name("Grape", "عنب"), baseUom = "NOPE" },
            },
        }, HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("item.base_uom_unknown");
        problem.GetProperty("why").GetProperty("row").GetInt32().ShouldBe(3);
        (await owner.GetAsync(new Uri("/api/v1/items/by-code/PEAR-1L", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the batch rolled back");
        (await owner.GetOkAsync("/api/v1/items/by-code/JUICE-1L")).GetProperty("name").GetProperty("en").GetString().ShouldBe("Orange juice 1 litre");

        // Another tenant sees none of it.
        var other = Api.ClientFor((await Api.SignupAsync()).AccessToken);
        (await other.GetOkAsync("/api/v1/items")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await other.GetAsync(new Uri("/api/v1/items/by-barcode/5901234123457", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var clerk = await InviteAsync(owner, ws.Slug, "warehouse_operator");
        (await clerk.GetOkAsync("/api/v1/items")).GetProperty("items").GetArrayLength().ShouldBe(2, "juice and apple; the refused and rolled-back rows never existed");
        (await clerk.PostAsJsonAsync("/api/v1/items", new { code = "NO", name = Name("x", "x"), baseUom = "PCS" }, ApiFixture.Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Invites a member with one role template and signs them in.</summary>
    private async Task<HttpClient> InviteAsync(HttpClient owner, string slug, string roleCode)
    {
        var roles = await owner.GetOkAsync("/api/v1/roles");
        var roleId = roles.EnumerateArray().Single(r => r.GetProperty("code").GetString() == roleCode).GetProperty("id").GetGuid();
        var email = $"{roleCode}-{slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { roleId } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "clerk-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        return Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
    }
}

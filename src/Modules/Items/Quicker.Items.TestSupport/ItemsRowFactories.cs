using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Items.TestSupport;

/// <summary>Minimal valid rows for every Items tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class ItemsRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.itm_brands", static async (c, tx, t) => new RowRef("app.itm_brands", $"id = '{await BrandAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.itm_item_categories", static async (c, tx, t) => new RowRef("app.itm_item_categories", $"id = '{await CategoryAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.itm_attributes", static async (c, tx, t) => new RowRef("app.itm_attributes", $"id = '{(await AttributeAsync(c, tx, t)).Attribute}'"));
        IsolationRegistry.Register("app.itm_attribute_values", static async (c, tx, t) => new RowRef("app.itm_attribute_values", $"id = '{(await AttributeAsync(c, tx, t)).Value}'"));
        IsolationRegistry.Register("app.itm_items", static async (c, tx, t) => new RowRef("app.itm_items", $"id = '{(await ItemAsync(c, tx, t)).Item}'"));
        IsolationRegistry.Register("app.itm_item_uoms", static async (c, tx, t) => new RowRef("app.itm_item_uoms", $"id = '{(await ItemAsync(c, tx, t)).ItemUom}'"));
        IsolationRegistry.Register("app.itm_item_variants", static async (c, tx, t) =>
        {
            var (item, _, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.itm_item_variants (tenant_id, id, item_id, sku, name_i18n) VALUES (@t, @id, @item, @sku, '{}')", new { t, id, item, sku = Suffix(id) }, tx);
            return new RowRef("app.itm_item_variants", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.itm_item_barcodes", static async (c, tx, t) =>
        {
            var (_, itemUom, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.itm_item_barcodes (tenant_id, id, item_uom_id, barcode, symbology) VALUES (@t, @id, @itemUom, @barcode, 'CODE128')", new { t, id, itemUom, barcode = "B" + id.ToString("N") }, tx);
            return new RowRef("app.itm_item_barcodes", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.itm_item_suppliers", static async (c, tx, t) =>
        {
            var (item, _, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.itm_item_suppliers (tenant_id, id, item_id, partner_id) VALUES (@t, @id, @item, @partner)", new { t, id, item, partner = Guid.CreateVersion7() }, tx);
            return new RowRef("app.itm_item_suppliers", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.itm_item_company_settings", static async (c, tx, t) =>
        {
            var (item, _, _) = await ItemAsync(c, tx, t);
            var company = await CompanyAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.itm_item_company_settings (tenant_id, item_id, company_id, standard_cost) VALUES (@t, @item, @company, 1)", new { t, item, company }, tx);
            return new RowRef("app.itm_item_company_settings", $"item_id = '{item}'");
        });
        IsolationRegistry.Register("app.itm_item_warehouse_settings", static async (c, tx, t) =>
        {
            var (item, _, _) = await ItemAsync(c, tx, t);
            var company = await CompanyAsync(c, tx, t);
            var warehouse = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.inv_warehouses (tenant_id, id, company_id, code, name_i18n) VALUES (@t, @warehouse, @company, @code, '{}')", new { t, warehouse, company, code = Suffix(warehouse) }, tx);
            await c.ExecuteAsync("INSERT INTO app.itm_item_warehouse_settings (tenant_id, item_id, warehouse_id, reorder_point) VALUES (@t, @item, @warehouse, 1)", new { t, item, warehouse }, tx);
            return new RowRef("app.itm_item_warehouse_settings", $"item_id = '{item}'");
        });
        IsolationRegistry.Register("app.itm_boms", static async (c, tx, t) => new RowRef("app.itm_boms", $"id = '{(await BomAsync(c, tx, t)).Bom}'"));
        IsolationRegistry.Register("app.itm_bom_lines", static async (c, tx, t) => new RowRef("app.itm_bom_lines", $"id = '{(await BomAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.itm_substitutes", static async (c, tx, t) =>
        {
            var (item, _, _) = await ItemAsync(c, tx, t);
            var (substitute, _, _) = await ItemAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.itm_substitutes (tenant_id, item_id, substitute_item_id) VALUES (@t, @item, @substitute)", new { t, item, substitute }, tx);
            return new RowRef("app.itm_substitutes", $"item_id = '{item}'");
        });
    }

    private static async Task<Guid> BrandAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_brands (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<Guid> CategoryAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        var code = Suffix(id);
        await c.ExecuteAsync("INSERT INTO app.itm_item_categories (tenant_id, id, code, name_i18n, path, level) VALUES (@t, @id, @code, '{}', @path, 0)", new { t, id, code, path = "/" + code + "/" }, tx);
        return id;
    }

    private static async Task<(Guid Attribute, Guid Value)> AttributeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        var value = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_attributes (tenant_id, id, code, name_i18n) VALUES (@t, @id, @code, '{}')", new { t, id, code = Suffix(id) }, tx);
        await c.ExecuteAsync("INSERT INTO app.itm_attribute_values (tenant_id, id, attribute_id, code, name_i18n) VALUES (@t, @value, @id, 'V', '{}')", new { t, value, id }, tx);
        return (id, value);
    }

    private static async Task<Guid> UomAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_uoms (tenant_id, id, code, name_i18n, family) VALUES (@t, @id, @code, '{}', 'count')", new { t, id, code = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Item, Guid ItemUom, Guid Uom)> ItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var uom = await UomAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var itemUom = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_items (tenant_id, id, code, name_i18n, type, base_uom_id) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', 'stock', @uom)", new { t, id, code = Suffix(id), uom }, tx);
        await c.ExecuteAsync("INSERT INTO app.itm_item_uoms (tenant_id, id, item_id, uom_id, numerator, denominator) VALUES (@t, @itemUom, @id, @uom, 1, 1)", new { t, itemUom, id, uom }, tx);
        return (id, itemUom, uom);
    }

    private static async Task<(Guid Bom, Guid Line)> BomAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (item, _, _) = await ItemAsync(c, tx, t);
        var (component, _, uom) = await ItemAsync(c, tx, t);
        await c.ExecuteAsync("UPDATE app.itm_items SET type = 'assembly' WHERE tenant_id = @t AND id = @item", new { t, item }, tx);
        var bom = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_boms (tenant_id, id, item_id, kind, version) VALUES (@t, @bom, @item, 'assembly', 1)", new { t, bom, item }, tx);
        await c.ExecuteAsync("INSERT INTO app.itm_bom_lines (tenant_id, id, bom_id, component_item_id, quantity, uom_id) VALUES (@t, @line, @bom, @component, 1, @uom)", new { t, line, bom, component, uom }, tx);
        return (bom, line);
    }

    private static async Task<Guid> CompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var fiscal = Guid.CreateVersion7();
        var business = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @fiscal, @code, '{}', 1, 12)", new { t, fiscal, code = Suffix(fiscal).ToLowerInvariant() }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @business, @code, '{}', ARRAY[0,1,2,3,4])", new { t, business, code = Suffix(business).ToLowerInvariant() }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();
}

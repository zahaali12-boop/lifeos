using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Pricing.TestSupport;

/// <summary>Minimal valid rows for every Pricing tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class PricingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.prc_price_lists", static async (c, tx, t) => new RowRef("app.prc_price_lists", $"id = '{(await ListAsync(c, tx, t)).List}'"));
        IsolationRegistry.Register("app.prc_price_list_assignments", static async (c, tx, t) =>
        {
            var (list, _) = await ListAsync(c, tx, t);
            var partner = await PartnerAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_price_list_assignments (tenant_id, id, price_list_id, partner_id) VALUES (@t, @id, @list, @partner)", new { t, id, list, partner }, tx);
            return new RowRef("app.prc_price_list_assignments", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prc_price_list_items", static async (c, tx, t) =>
        {
            var (list, _) = await ListAsync(c, tx, t);
            var (item, uom) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_price_list_items (tenant_id, id, price_list_id, item_id, uom_id, price) VALUES (@t, @id, @list, @item, @uom, 1000)", new { t, id, list, item, uom }, tx);
            return new RowRef("app.prc_price_list_items", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prc_customer_price_agreements", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            var partner = await PartnerAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_customer_price_agreements (tenant_id, id, company_id, partner_id, item_id, discount_pct) VALUES (@t, @id, @company, @partner, @item, 5)", new { t, id, company, partner, item }, tx);
            return new RowRef("app.prc_customer_price_agreements", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prc_discount_rules", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_discount_rules (tenant_id, id, company_id, code, level, value_type, value) VALUES (@t, @id, @company, @code, 'line', 'percentage', 5)", new { t, id, company, code = Suffix(id) }, tx);
            return new RowRef("app.prc_discount_rules", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prc_promotions", static async (c, tx, t) => new RowRef("app.prc_promotions", $"id = '{(await PromotionAsync(c, tx, t, "coupon")).Promotion}'"));
        IsolationRegistry.Register("app.prc_promotion_components", static async (c, tx, t) =>
        {
            var (promotion, _) = await PromotionAsync(c, tx, t, "bundle");
            var (item, _) = await ItemAsync(c, tx, t);
            await c.ExecuteAsync("INSERT INTO app.prc_promotion_components (tenant_id, promotion_id, item_id, quantity) VALUES (@t, @promotion, @item, 1)", new { t, promotion, item }, tx);
            return new RowRef("app.prc_promotion_components", $"promotion_id = '{promotion}' AND item_id = '{item}'");
        });
        IsolationRegistry.Register("app.prc_promotion_tiers", static async (c, tx, t) =>
        {
            var (promotion, _) = await PromotionAsync(c, tx, t, "volume_tier");
            await c.ExecuteAsync("INSERT INTO app.prc_promotion_tiers (tenant_id, promotion_id, min_quantity, discount_pct) VALUES (@t, @promotion, 10, 5)", new { t, promotion }, tx);
            return new RowRef("app.prc_promotion_tiers", $"promotion_id = '{promotion}'");
        });
        IsolationRegistry.Register("app.prc_promotion_usages", static async (c, tx, t) =>
        {
            var (promotion, _) = await PromotionAsync(c, tx, t, "coupon");
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_promotion_usages (tenant_id, id, promotion_id, document_type, document_id, used_at) VALUES (@t, @id, @promotion, 'sales_order', @doc, now())", new { t, id, promotion, doc = Guid.CreateVersion7() }, tx);
            return new RowRef("app.prc_promotion_usages", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.prc_price_floors", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            var (item, _) = await ItemAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.prc_price_floors (tenant_id, id, company_id, item_id, min_margin_pct) VALUES (@t, @id, @company, @item, 5)", new { t, id, company, item }, tx);
            return new RowRef("app.prc_price_floors", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid List, Guid Company)> ListAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.prc_price_lists (tenant_id, id, company_id, code, currency) VALUES (@t, @id, @company, @code, 'IQD')", new { t, id, company, code = Suffix(id) }, tx);
        return (id, company);
    }

    private static async Task<(Guid Promotion, Guid Company)> PromotionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t, string kind)
    {
        var company = await CompanyAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        var sql = kind switch
        {
            "bundle" => "INSERT INTO app.prc_promotions (tenant_id, id, company_id, code, kind, bundle_price, currency) VALUES (@t, @id, @company, @code, 'bundle', 100, 'IQD')",
            "volume_tier" => "INSERT INTO app.prc_promotions (tenant_id, id, company_id, code, kind, brand_id) VALUES (@t, @id, @company, @code, 'volume_tier', @brand)",
            _ => "INSERT INTO app.prc_promotions (tenant_id, id, company_id, code, kind, coupon_code, discount_pct) VALUES (@t, @id, @company, @code, 'coupon', @code, 10)",
        };
        Guid? brand = null;
        if (kind == "volume_tier")
        {
            brand = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.itm_brands (tenant_id, id, code, name_i18n) VALUES (@t, @brand, @code, '{}')", new { t, brand, code = Suffix(brand.Value) }, tx);
        }

        await c.ExecuteAsync(sql, new { t, id, company, code = Suffix(id), brand }, tx);
        return (id, company);
    }

    private static async Task<(Guid Item, Guid Uom)> ItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var uom = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_uoms (tenant_id, id, code, name_i18n, family) VALUES (@t, @uom, @code, '{}', 'count')", new { t, uom, code = Suffix(uom) }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.itm_items (tenant_id, id, code, name_i18n, type, base_uom_id) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', 'stock', @uom)", new { t, id, code = Suffix(id), uom }, tx);
        return (id, uom);
    }

    private static async Task<Guid> PartnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_customer) VALUES (@t, @id, @code, '{\"en\":\"Probe\"}', true)", new { t, id, code = Suffix(id) }, tx);
        return id;
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
}

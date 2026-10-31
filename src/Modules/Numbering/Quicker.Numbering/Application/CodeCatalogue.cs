namespace Quicker.Numbering.Application;

/// <summary>
/// A kind of master record that carries a code unique in its scope: the table and column that hold the code, the column
/// that scopes it (the company of a warehouse, the chart of an account; none when the code is unique in the tenant), an
/// optional condition that narrows the records whose pattern a new one continues (customers among the partners), and
/// the permission that lets a member read the records. <see cref="AutoFill"/> marks codes that are sequences by nature
/// (customers, items, warehouses): a new record starts with the next one. Others are words chosen by people (a unit, a
/// role, a tax code) and only get the duplicate check.
/// </summary>
public sealed record CodedRecordType(
    string Kind,
    string Table,
    string ReadPermission,
    string? ScopeColumn = null,
    string? PatternFilter = null,
    bool AutoFill = false,
    string DefaultPrefix = "",
    int DefaultWidth = 3);

/// <summary>
/// The coded records of every module (A-165). Numbering reads only their code columns, through the tenant's own
/// connection (row-level security applies), to suggest the next code and to say whether one is taken; each module still
/// enforces uniqueness with its own constraint.
/// </summary>
public static class CodeCatalogue
{
    /// <summary>The kind whose codes follow the chart's tree: a new account's code continues its siblings under the parent.</summary>
    public const string Account = "account";

    private static readonly CodedRecordType[] Types =
    [
        // Organization
        new("company", "app.org_companies", "organization.company.read"),
        new("branch", "app.org_branches", "organization.company.read", "company_id"),
        new("dimension", "app.org_dimensions", "organization.company.read"),
        new("dimension-value", "app.org_dimension_values", "organization.company.read", "dimension_id", AutoFill: true, DefaultPrefix: "", DefaultWidth: 3),
        new("uom", "app.org_uoms", "organization.company.read"),
        new("fiscal-calendar", "app.org_fiscal_calendars", "organization.company.read"),
        new("business-calendar", "app.org_business_calendars", "organization.company.read"),
        new("series", "app.num_series", "numbering.series.read"),
        // Identity
        new("role", "app.idn_roles", "identity.role.read"),
        // Accounting
        new(Account, "app.gl_accounts", "accounting.chart.read", "chart_id", AutoFill: true),
        new("posting-group", "app.gl_posting_groups", "accounting.profile.read", "kind"),
        new("recurring-template", "app.gl_recurring_templates", "accounting.journal.read", "company_id", AutoFill: true, DefaultPrefix: "REC-"),
        // Items and inventory
        new("item", "app.itm_items", "inventory.item.read", AutoFill: true, DefaultPrefix: "ITM-", DefaultWidth: 4),
        new("item-category", "app.itm_item_categories", "inventory.item.read"),
        new("brand", "app.itm_brands", "inventory.item.read"),
        new("attribute", "app.itm_attributes", "inventory.item.read"),
        new("warehouse", "app.inv_warehouses", "inventory.warehouse.read", "company_id", AutoFill: true, DefaultPrefix: "WH", DefaultWidth: 2),
        new("bin", "app.inv_bins", "inventory.warehouse.read", "warehouse_id", AutoFill: true, DefaultPrefix: "BIN-"),
        new("reason-code", "app.inv_reason_codes", "inventory.adjustment.read"),
        // Partners
        new("customer", "app.ptr_partners", "partners.customer.read", PatternFilter: "is_customer", AutoFill: true, DefaultPrefix: "CUS-", DefaultWidth: 4),
        new("supplier", "app.ptr_partners", "partners.supplier.read", PatternFilter: "is_supplier", AutoFill: true, DefaultPrefix: "SUP-", DefaultWidth: 4),
        new("payment-terms", "app.ptr_payment_terms", "partners.supplier.read"),
        new("delivery-terms", "app.ptr_delivery_terms", "partners.supplier.read"),
        new("wht-code", "app.ptr_wht_codes", "partners.supplier.read"),
        new("supplier-group", "app.ptr_supplier_groups", "partners.supplier.read"),
        new("customer-group", "app.ptr_customer_groups", "partners.customer.read"),
        new("commission-plan", "app.ptr_commission_plans", "partners.customer.read"),
        new("sales-rep", "app.ptr_sales_reps", "partners.customer.read", AutoFill: true, DefaultPrefix: "REP-"),
        new("pipeline-stage", "app.ptr_pipeline_stages", "partners.customer.read"),
        // Purchasing, banking, pricing, tax, sales
        new("charge-type", "app.pur_charge_types", "purchasing.landed_cost.read"),
        new("bank-account", "app.bnk_bank_accounts", "banking.bank_account.read", "company_id", AutoFill: true, DefaultPrefix: "BANK-", DefaultWidth: 2),
        new("price-list", "app.prc_price_lists", "pricing.price.read", "company_id", AutoFill: true, DefaultPrefix: "PL-"),
        new("discount-rule", "app.prc_discount_rules", "pricing.price.read", "company_id", AutoFill: true, DefaultPrefix: "DR-"),
        new("promotion", "app.prc_promotions", "pricing.price.read", "company_id", AutoFill: true, DefaultPrefix: "PROMO-"),
        new("tax-regime", "app.tax_regimes", "tax.setup.read"),
        new("tax-code", "app.tax_codes", "tax.setup.read", "regime_id"),
        new("tax-group", "app.tax_groups", "tax.setup.read", "kind"),
        new("recurring-invoice", "app.sls_recurring_invoices", "sales.invoice.read", "company_id", AutoFill: true, DefaultPrefix: "RI-"),
    ];

    private static readonly Dictionary<string, CodedRecordType> ByKind = Types.ToDictionary(static t => t.Kind, StringComparer.Ordinal);

    public static IReadOnlyCollection<CodedRecordType> All => Types;

    public static CodedRecordType? Find(string kind) => ByKind.GetValueOrDefault(kind);
}

using Quicker.Kernel.Text;

namespace Quicker.Reporting.Application.Semantic;

public static class FieldRoles
{
    public const string Dimension = "dimension";
    public const string Measure = "measure";
}

/// <summary>What a field holds: how the client formats it and how a filter value is read.</summary>
public static class FieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Money = "money";
    public const string Quantity = "quantity";
    public const string Percent = "percent";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Id = "id";
}

public static class Aggregations
{
    public const string Sum = "sum";
    public const string Count = "count";
    public const string CountDistinct = "count_distinct";
    public const string Avg = "avg";
    public const string Min = "min";
    public const string Max = "max";

    public static readonly IReadOnlyList<string> All = [Sum, Count, CountDistinct, Avg, Min, Max];
}

/// <summary>A table a model joins to for labels; <c>f</c> is the fact in every ON clause. A join may need another join first.</summary>
public sealed record JoinSpec(string Alias, string Table, string On, string? Requires = null);

/// <summary>
/// A field of a model: a dimension (a key expression and, when the key is an id, a label expression that may be a
/// bilingual jsonb) or a measure (an expression the compiler aggregates). The folder groups fields in the builder.
/// </summary>
public sealed record FieldSpec(
    string Key,
    LocalizedText Label,
    string Role,
    string Type,
    string Folder,
    string Sql,
    string? LabelSql = null,
    string? LabelType = null,
    string? Join = null,
    string? Aggregation = null,
    LocalizedText? Description = null);

/// <summary>A column of the line-grain drill: the lines behind a cell.</summary>
public sealed record DrillColumn(string Key, LocalizedText Label, string Type, string Sql, string? Join = null, string? LabelType = null);

/// <summary>
/// A semantic model (ADR-0034): one fact or view, the permission that reads it (any of several), the columns the
/// reader's company, branch and warehouse scopes apply to, its date, its joins, fields and drill columns, and how a
/// line names the document it came from.
/// </summary>
public sealed record ModelSpec(
    string Code,
    LocalizedText Name,
    LocalizedText Description,
    string Source,
    IReadOnlyList<string> Permissions,
    string CompanyColumn,
    string? BranchColumn,
    string? WarehouseColumn,
    string? DateColumn,
    bool HasDimensionSet,
    IReadOnlyList<JoinSpec> Joins,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyList<DrillColumn> Drill,
    string? DocumentTypeSql,
    string? DocumentIdSql,
    string? DocumentNumberSql,
    string? SideColumn = null,
    IReadOnlyDictionary<string, string>? SidePermissions = null);

/// <summary>The system models and their fields, defined in code (ADR-0034 §2); tenant dimensions are added per tenant by the catalogue service.</summary>
public static class Catalogue
{
    private static LocalizedText L(string en, string ar) => LocalizedText.Bilingual(en, ar);

    private static FieldSpec Dim(string key, string en, string ar, string folder, string sql, string type = FieldTypes.Text, string? labelSql = null, string? labelType = null, string? join = null) =>
        new(key, L(en, ar), FieldRoles.Dimension, type, folder, sql, labelSql, labelType, join);

    private static FieldSpec Measure(string key, string en, string ar, string folder, string sql, string aggregation = Aggregations.Sum, string type = FieldTypes.Money) =>
        new(key, L(en, ar), FieldRoles.Measure, type, folder, sql, Aggregation: aggregation);

    private static DrillColumn Col(string key, string en, string ar, string type, string sql, string? join = null, string? labelType = null) => new(key, L(en, ar), type, sql, join, labelType);

    private const string Time = "time";
    private const string Parties = "parties";
    private const string Products = "products";
    private const string Documents = "documents";
    private const string Accounts = "accounts";
    private const string Places = "places";
    private const string Amounts = "amounts";
    private const string Quantities = "quantities";
    private const string Counts = "counts";

    /// <summary>The period fields of a model's date column: the date itself and its year, quarter and month as text keys that sort.</summary>
    private static IEnumerable<FieldSpec> PeriodFields(string column, string en, string ar) =>
    [
        Dim(column, en, ar, Time, $"f.{column}", FieldTypes.Date),
        Dim("period_year", "Year", "السنة", Time, $"to_char(f.{column}, 'YYYY')"),
        Dim("period_quarter", "Quarter", "ربع السنة", Time, $"to_char(f.{column}, 'YYYY') || '-Q' || to_char(f.{column}, 'Q')"),
        Dim("period_month", "Month", "الشهر", Time, $"to_char(f.{column}, 'YYYY-MM')"),
    ];

    private static readonly JoinSpec Partner = new("p", "app.ptr_partners", "p.tenant_id = f.tenant_id AND p.id = f.partner_id");
    private static readonly JoinSpec Item = new("it", "app.itm_items", "it.tenant_id = f.tenant_id AND it.id = f.item_id");
    private static readonly JoinSpec ItemCategory = new("ic", "app.itm_item_categories", "ic.tenant_id = it.tenant_id AND ic.id = it.category_id", "it");
    private static readonly JoinSpec Brand = new("ibr", "app.itm_brands", "ibr.tenant_id = it.tenant_id AND ibr.id = it.brand_id", "it");
    private static readonly JoinSpec Uom = new("u", "app.org_uoms", "u.tenant_id = f.tenant_id AND u.id = f.uom_id");
    private static readonly JoinSpec Branch = new("b", "app.org_branches", "b.tenant_id = f.tenant_id AND b.id = f.branch_id");
    private static readonly JoinSpec Warehouse = new("w", "app.inv_warehouses", "w.tenant_id = f.tenant_id AND w.id = f.warehouse_id");
    private static readonly JoinSpec TaxCode = new("tc", "app.tax_codes", "tc.tenant_id = f.tenant_id AND tc.id = f.tax_code_id");
    private static readonly JoinSpec Account = new("a", "app.gl_accounts", "a.tenant_id = f.tenant_id AND a.id = f.account_id");
    private static readonly JoinSpec AccountCategory = new("ac", "app.gl_account_categories", "ac.tenant_id = a.tenant_id AND ac.id = a.category_id", "a");
    private static readonly JoinSpec SalesRep = new("r", "app.ptr_sales_reps", "r.tenant_id = f.tenant_id AND r.id = f.sales_rep_id");
    private static readonly JoinSpec CustomerAccount = new("ca", "app.ptr_customer_accounts", "ca.tenant_id = f.tenant_id AND ca.partner_id = f.partner_id AND ca.company_id = f.company_id");
    private static readonly JoinSpec CustomerGroup = new("cg", "app.ptr_customer_groups", "cg.tenant_id = ca.tenant_id AND cg.id = ca.customer_group_id", "ca");
    private static readonly JoinSpec FiscalPeriod = new("fp", "app.org_fiscal_periods", "fp.tenant_id = f.tenant_id AND fp.id = f.fiscal_period_id");
    private static readonly JoinSpec EnteredUom = new("eu", "app.org_uoms", "eu.tenant_id = f.tenant_id AND eu.id = f.entered_uom_id");
    private static readonly JoinSpec OwnerPartner = new("op", "app.ptr_partners", "op.tenant_id = f.tenant_id AND op.id = f.owner_partner_id");

    private static readonly FieldSpec[] ItemFields =
    [
        Dim("item", "Item", "الصنف", Products, "f.item_id", FieldTypes.Id, "it.name_i18n", "i18n", "it"),
        Dim("item_code", "Item code", "رمز الصنف", Products, "it.code", join: "it"),
        Dim("item_type", "Item type", "نوع الصنف", Products, "it.type", join: "it"),
        Dim("item_category", "Item category", "فئة الصنف", Products, "ic.id", FieldTypes.Id, "ic.name_i18n", "i18n", "ic"),
        Dim("brand", "Brand", "العلامة التجارية", Products, "ibr.id", FieldTypes.Id, "ibr.name_i18n", "i18n", "ibr"),
    ];

    public static readonly IReadOnlyList<ModelSpec> Models =
    [
        new("sales_lines", L("Sales lines", "بنود المبيعات"), L("Every line of a posted sales invoice or credit note, credit notes counted against, with the customer, the item and the amounts at the rate each was booked with.", "كل بند في فاتورة مبيعات أو إشعار دائن مرحّل، الإشعارات الدائنة بالسالب، مع العميل والصنف والمبالغ بسعر الصرف المسجّل."),
            "reporting.fact_sales_lines", ["sales.invoice.read"], "company_id", "branch_id", null, "posting_date", true,
            [Partner, Item, ItemCategory, Brand, Uom, Branch, TaxCode, SalesRep, CustomerAccount, CustomerGroup],
            [
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                Dim("document_date", "Document date", "تاريخ المستند", Time, "f.document_date", FieldTypes.Date),
                Dim("due_date", "Due date", "تاريخ الاستحقاق", Time, "f.due_date", FieldTypes.Date),
                Dim("customer", "Customer", "العميل", Parties, "f.partner_id", FieldTypes.Id, "p.legal_name_i18n", "i18n", "p"),
                Dim("customer_code", "Customer code", "رمز العميل", Parties, "p.code", join: "p"),
                Dim("customer_group", "Customer group", "مجموعة العملاء", Parties, "cg.id", FieldTypes.Id, "cg.name_i18n", "i18n", "cg"),
                Dim("sales_rep", "Sales representative", "مندوب المبيعات", Parties, "f.sales_rep_id", FieldTypes.Id, "r.name_i18n", "i18n", "r"),
                .. ItemFields,
                Dim("uom", "Unit", "الوحدة", Products, "u.code", join: "u"),
                Dim("branch", "Branch", "الفرع", Places, "f.branch_id", FieldTypes.Id, "b.name_i18n", "i18n", "b"),
                Dim("invoice_number", "Invoice number", "رقم الفاتورة", Documents, "f.invoice_number"),
                Dim("invoice_kind", "Kind", "النوع", Documents, "f.kind"),
                Dim("status", "Status", "الحالة", Documents, "f.status"),
                Dim("line_kind", "Line kind", "نوع البند", Documents, "f.line_kind"),
                Dim("currency", "Currency", "العملة", Documents, "f.currency"),
                Dim("tax_code", "Tax code", "رمز الضريبة", Documents, "f.tax_code_id", FieldTypes.Id, "tc.code", "text", "tc"),
                Dim("account_role", "Account role", "دور الحساب", Accounts, "f.account_role"),
                Measure("net_amount", "Net amount", "المبلغ الصافي", Amounts, "f.net_amount"),
                Measure("tax_amount", "Tax", "الضريبة", Amounts, "f.tax_amount"),
                Measure("gross_amount", "Gross amount", "المبلغ الإجمالي", Amounts, "f.gross_amount"),
                Measure("discount_amount", "Discount", "الخصم", Amounts, "f.discount_amount"),
                Measure("net_amount_fc", "Net amount (functional)", "المبلغ الصافي (بالعملة الوظيفية)", Amounts, "f.net_amount_fc"),
                Measure("quantity", "Quantity", "الكمية", Quantities, "f.quantity", type: FieldTypes.Quantity),
                Measure("average_unit_price", "Average unit price", "متوسط سعر الوحدة", Amounts, "f.unit_price", Aggregations.Avg),
                Measure("line_count", "Lines", "عدد البنود", Counts, "*", Aggregations.Count, FieldTypes.Number),
                Measure("invoice_count", "Invoices", "عدد الفواتير", Counts, "f.invoice_id", Aggregations.CountDistinct, FieldTypes.Number),
                Measure("customer_count", "Customers", "عدد العملاء", Counts, "f.partner_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("posting_date", "Posting date", "تاريخ الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("invoice_number", "Invoice", "الفاتورة", FieldTypes.Text, "f.invoice_number"),
                Col("invoice_kind", "Kind", "النوع", FieldTypes.Text, "f.kind"),
                Col("customer", "Customer", "العميل", FieldTypes.Text, "p.legal_name_i18n", "p", "i18n"),
                Col("item", "Item", "الصنف", FieldTypes.Text, "it.name_i18n", "it", "i18n"),
                Col("description", "Description", "الوصف", FieldTypes.Text, "f.description"),
                Col("quantity", "Quantity", "الكمية", FieldTypes.Quantity, "f.quantity"),
                Col("unit_price", "Unit price", "سعر الوحدة", FieldTypes.Money, "f.unit_price"),
                Col("net_amount", "Net", "الصافي", FieldTypes.Money, "f.net_amount"),
                Col("tax_amount", "Tax", "الضريبة", FieldTypes.Money, "f.tax_amount"),
                Col("gross_amount", "Gross", "الإجمالي", FieldTypes.Money, "f.gross_amount"),
                Col("currency", "Currency", "العملة", FieldTypes.Text, "f.currency"),
            ],
            "'sales_invoice'", "f.invoice_id", "f.invoice_number"),

        new("purchase_lines", L("Purchase lines", "بنود المشتريات"), L("Every line of a posted supplier invoice or credit note, credit notes counted against, with the supplier, the item and the amounts.", "كل بند في فاتورة مورد أو إشعار دائن مرحّل، الإشعارات الدائنة بالسالب، مع المورد والصنف والمبالغ."),
            "reporting.fact_purchase_lines", ["purchasing.invoice.read"], "company_id", "branch_id", null, "posting_date", true,
            [Partner, Item, ItemCategory, Brand, Uom, Branch, TaxCode],
            [
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                Dim("document_date", "Document date", "تاريخ المستند", Time, "f.document_date", FieldTypes.Date),
                Dim("due_date", "Due date", "تاريخ الاستحقاق", Time, "f.due_date", FieldTypes.Date),
                Dim("supplier", "Supplier", "المورد", Parties, "f.partner_id", FieldTypes.Id, "p.legal_name_i18n", "i18n", "p"),
                Dim("supplier_code", "Supplier code", "رمز المورد", Parties, "p.code", join: "p"),
                .. ItemFields,
                Dim("uom", "Unit", "الوحدة", Products, "u.code", join: "u"),
                Dim("branch", "Branch", "الفرع", Places, "f.branch_id", FieldTypes.Id, "b.name_i18n", "i18n", "b"),
                Dim("invoice_number", "Invoice number", "رقم الفاتورة", Documents, "f.invoice_number"),
                Dim("invoice_kind", "Kind", "النوع", Documents, "f.kind"),
                Dim("status", "Status", "الحالة", Documents, "f.status"),
                Dim("line_kind", "Line kind", "نوع البند", Documents, "f.line_kind"),
                Dim("currency", "Currency", "العملة", Documents, "f.currency"),
                Dim("tax_code", "Tax code", "رمز الضريبة", Documents, "f.tax_code_id", FieldTypes.Id, "tc.code", "text", "tc"),
                Dim("account_role", "Account role", "دور الحساب", Accounts, "f.account_role"),
                Measure("net_amount", "Net amount", "المبلغ الصافي", Amounts, "f.net_amount"),
                Measure("tax_amount", "Tax", "الضريبة", Amounts, "f.tax_amount"),
                Measure("wht_amount", "Withholding tax", "ضريبة الاستقطاع", Amounts, "f.wht_amount"),
                Measure("gross_amount", "Gross amount", "المبلغ الإجمالي", Amounts, "f.gross_amount"),
                Measure("net_amount_fc", "Net amount (functional)", "المبلغ الصافي (بالعملة الوظيفية)", Amounts, "f.net_amount_fc"),
                Measure("quantity", "Quantity", "الكمية", Quantities, "f.quantity", type: FieldTypes.Quantity),
                Measure("average_unit_price", "Average unit price", "متوسط سعر الوحدة", Amounts, "f.unit_price", Aggregations.Avg),
                Measure("line_count", "Lines", "عدد البنود", Counts, "*", Aggregations.Count, FieldTypes.Number),
                Measure("invoice_count", "Invoices", "عدد الفواتير", Counts, "f.invoice_id", Aggregations.CountDistinct, FieldTypes.Number),
                Measure("supplier_count", "Suppliers", "عدد الموردين", Counts, "f.partner_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("posting_date", "Posting date", "تاريخ الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("invoice_number", "Invoice", "الفاتورة", FieldTypes.Text, "f.invoice_number"),
                Col("invoice_kind", "Kind", "النوع", FieldTypes.Text, "f.kind"),
                Col("supplier", "Supplier", "المورد", FieldTypes.Text, "p.legal_name_i18n", "p", "i18n"),
                Col("item", "Item", "الصنف", FieldTypes.Text, "it.name_i18n", "it", "i18n"),
                Col("description", "Description", "الوصف", FieldTypes.Text, "f.description"),
                Col("quantity", "Quantity", "الكمية", FieldTypes.Quantity, "f.quantity"),
                Col("unit_price", "Unit price", "سعر الوحدة", FieldTypes.Money, "f.unit_price"),
                Col("net_amount", "Net", "الصافي", FieldTypes.Money, "f.net_amount"),
                Col("tax_amount", "Tax", "الضريبة", FieldTypes.Money, "f.tax_amount"),
                Col("gross_amount", "Gross", "الإجمالي", FieldTypes.Money, "f.gross_amount"),
                Col("currency", "Currency", "العملة", FieldTypes.Text, "f.currency"),
            ],
            "'purchase_invoice'", "f.invoice_id", "f.invoice_number"),

        new("journal_lines", L("Journal lines", "بنود القيود"), L("Every posted journal line with its account, partner, source document and amounts in transaction, functional and reporting currency.", "كل بند قيد مرحّل مع حسابه والشريك والمستند المصدر والمبالغ بعملة المعاملة والعملة الوظيفية وعملة التقارير."),
            "reporting.fact_journal_lines", ["accounting.journal.read"], "company_id", "branch_id", null, "posting_date", true,
            [Account, AccountCategory, Partner, Branch, TaxCode, FiscalPeriod],
            [
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                Dim("document_date", "Document date", "تاريخ المستند", Time, "f.document_date", FieldTypes.Date),
                Dim("fiscal_period", "Fiscal period", "الفترة المالية", Time, "f.fiscal_period_id", FieldTypes.Id, "to_char(fp.starts_on, 'YYYY-MM') || ' (' || fp.number::text || ')'", "text", "fp"),
                Dim("account", "Account", "الحساب", Accounts, "f.account_id", FieldTypes.Id, "a.name_i18n", "i18n", "a"),
                Dim("account_code", "Account code", "رمز الحساب", Accounts, "a.code", join: "a"),
                Dim("account_type", "Account type", "نوع الحساب", Accounts, "a.type", join: "a"),
                Dim("account_subtype", "Account subtype", "النوع الفرعي للحساب", Accounts, "a.subtype", join: "a"),
                Dim("account_category", "Account category", "فئة الحساب", Accounts, "ac.id", FieldTypes.Id, "ac.name_i18n", "i18n", "ac"),
                Dim("account_role", "Account role", "دور الحساب", Accounts, "f.account_role"),
                Dim("subledger_type", "Subledger", "الدفتر المساعد", Accounts, "f.subledger_type"),
                Dim("partner", "Partner", "الشريك", Parties, "f.partner_id", FieldTypes.Id, "p.legal_name_i18n", "i18n", "p"),
                Dim("branch", "Branch", "الفرع", Places, "f.branch_id", FieldTypes.Id, "b.name_i18n", "i18n", "b"),
                Dim("entry_number", "Entry number", "رقم القيد", Documents, "f.entry_number"),
                Dim("source_module", "Source module", "الوحدة المصدر", Documents, "f.source_module"),
                Dim("source_document_type", "Source document type", "نوع المستند المصدر", Documents, "f.source_document_type"),
                Dim("source_document_number", "Source document number", "رقم المستند المصدر", Documents, "f.source_document_number"),
                Dim("is_manual", "Manual journal", "قيد يدوي", Documents, "f.is_manual", FieldTypes.Boolean),
                Dim("is_reversal", "Reversal", "قيد عكسي", Documents, "f.is_reversal", FieldTypes.Boolean),
                Dim("currency_tc", "Transaction currency", "عملة المعاملة", Documents, "f.currency_tc"),
                Dim("tax_code", "Tax code", "رمز الضريبة", Documents, "f.tax_code_id", FieldTypes.Id, "tc.code", "text", "tc"),
                Measure("debit_fc", "Debit", "مدين", Amounts, "f.debit_fc"),
                Measure("credit_fc", "Credit", "دائن", Amounts, "f.credit_fc"),
                Measure("net_fc", "Net (debit − credit)", "الصافي (مدين − دائن)", Amounts, "f.debit_fc - f.credit_fc"),
                Measure("debit_tc", "Debit (transaction currency)", "مدين (عملة المعاملة)", Amounts, "f.debit_tc"),
                Measure("credit_tc", "Credit (transaction currency)", "دائن (عملة المعاملة)", Amounts, "f.credit_tc"),
                Measure("debit_rc", "Debit (reporting currency)", "مدين (عملة التقارير)", Amounts, "f.debit_rc"),
                Measure("credit_rc", "Credit (reporting currency)", "دائن (عملة التقارير)", Amounts, "f.credit_rc"),
                Measure("net_rc", "Net (reporting currency)", "الصافي (عملة التقارير)", Amounts, "f.debit_rc - f.credit_rc"),
                Measure("line_count", "Lines", "عدد البنود", Counts, "*", Aggregations.Count, FieldTypes.Number),
                Measure("entry_count", "Entries", "عدد القيود", Counts, "f.entry_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("posting_date", "Posting date", "تاريخ الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("entry_number", "Entry", "القيد", FieldTypes.Text, "f.entry_number"),
                Col("account_code", "Account", "الحساب", FieldTypes.Text, "a.code", "a"),
                Col("account", "Account name", "اسم الحساب", FieldTypes.Text, "a.name_i18n", "a", "i18n"),
                Col("description", "Description", "الوصف", FieldTypes.Text, "f.description_i18n", null, "i18n"),
                Col("partner", "Partner", "الشريك", FieldTypes.Text, "p.legal_name_i18n", "p", "i18n"),
                Col("debit_fc", "Debit", "مدين", FieldTypes.Money, "f.debit_fc"),
                Col("credit_fc", "Credit", "دائن", FieldTypes.Money, "f.credit_fc"),
                Col("source_document_type", "Source type", "نوع المصدر", FieldTypes.Text, "f.source_document_type"),
                Col("source_document_number", "Source", "المصدر", FieldTypes.Text, "f.source_document_number"),
            ],
            "'journal_entry'", "f.entry_id", "f.entry_number"),

        new("stock_movements", L("Stock movements", "حركات المخزون"), L("Every stock ledger entry: receipts, shipments, transfers, adjustments and counts, by item and warehouse, in the base unit.", "كل قيد في دفتر المخزون: استلامات وشحنات وتحويلات وتسويات وجرد، حسب الصنف والمستودع، بالوحدة الأساسية."),
            "reporting.fact_stock_movements", ["inventory.stock.read"], "company_id", null, "warehouse_id", "posting_date", false,
            [Item, ItemCategory, Brand, Warehouse, EnteredUom, OwnerPartner],
            [
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                .. ItemFields,
                Dim("warehouse", "Warehouse", "المستودع", Places, "f.warehouse_id", FieldTypes.Id, "w.name_i18n", "i18n", "w"),
                Dim("warehouse_code", "Warehouse code", "رمز المستودع", Places, "w.code", join: "w"),
                Dim("entry_type", "Movement type", "نوع الحركة", Documents, "f.entry_type"),
                Dim("source_document_type", "Source document type", "نوع المستند المصدر", Documents, "f.source_document_type"),
                Dim("entered_uom", "Entered unit", "الوحدة المدخلة", Products, "eu.code", join: "eu"),
                Dim("owner", "Owner (consignment)", "المالك (بضاعة أمانة)", Parties, "f.owner_partner_id", FieldTypes.Id, "op.legal_name_i18n", "i18n", "op"),
                Measure("quantity", "Quantity (net)", "الكمية (صافي)", Quantities, "f.quantity", type: FieldTypes.Quantity),
                Measure("quantity_in", "Quantity in", "الكمية الداخلة", Quantities, "greatest(f.quantity, 0)", type: FieldTypes.Quantity),
                Measure("quantity_out", "Quantity out", "الكمية الخارجة", Quantities, "greatest(-f.quantity, 0)", type: FieldTypes.Quantity),
                Measure("movement_count", "Movements", "عدد الحركات", Counts, "*", Aggregations.Count, FieldTypes.Number),
                Measure("item_count", "Items", "عدد الأصناف", Counts, "f.item_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("posting_date", "Posting date", "تاريخ الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("entry_type", "Movement", "الحركة", FieldTypes.Text, "f.entry_type"),
                Col("item_code", "Item", "الصنف", FieldTypes.Text, "it.code", "it"),
                Col("item", "Item name", "اسم الصنف", FieldTypes.Text, "it.name_i18n", "it", "i18n"),
                Col("warehouse_code", "Warehouse", "المستودع", FieldTypes.Text, "w.code", "w"),
                Col("quantity", "Quantity", "الكمية", FieldTypes.Quantity, "f.quantity"),
                Col("source_document_type", "Source type", "نوع المصدر", FieldTypes.Text, "f.source_document_type"),
            ],
            "f.source_document_type", "f.source_document_id", null),

        new("stock_values", L("Stock values", "قيم المخزون"), L("Every stock value entry the costing engine wrote: what each movement cost, by item, warehouse and kind of value.", "كل قيد قيمة كتبه محرك التكلفة: تكلفة كل حركة، حسب الصنف والمستودع ونوع القيمة."),
            "reporting.fact_stock_values", ["inventory.costing.read"], "company_id", null, "warehouse_id", "posting_date", false,
            [Item, ItemCategory, Brand, Warehouse],
            [
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                .. ItemFields,
                Dim("warehouse", "Warehouse", "المستودع", Places, "f.warehouse_id", FieldTypes.Id, "w.name_i18n", "i18n", "w"),
                Dim("value_type", "Value type", "نوع القيمة", Documents, "f.value_type"),
                Dim("account_role", "Account role", "دور الحساب", Accounts, "f.account_role"),
                Dim("offset_role", "Offset role", "الدور المقابل", Accounts, "f.offset_role"),
                Dim("source_document_type", "Source document type", "نوع المستند المصدر", Documents, "f.source_document_type"),
                Measure("cost_amount", "Cost amount", "مبلغ التكلفة", Amounts, "f.cost_amount"),
                Measure("valued_quantity", "Valued quantity", "الكمية المقيّمة", Quantities, "f.valued_quantity", type: FieldTypes.Quantity),
                Measure("entry_count", "Entries", "عدد القيود", Counts, "*", Aggregations.Count, FieldTypes.Number),
            ],
            [
                Col("posting_date", "Posting date", "تاريخ الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("value_type", "Value type", "نوع القيمة", FieldTypes.Text, "f.value_type"),
                Col("item_code", "Item", "الصنف", FieldTypes.Text, "it.code", "it"),
                Col("warehouse", "Warehouse", "المستودع", FieldTypes.Text, "w.name_i18n", "w", "i18n"),
                Col("valued_quantity", "Quantity", "الكمية", FieldTypes.Quantity, "f.valued_quantity"),
                Col("unit_cost", "Unit cost", "تكلفة الوحدة", FieldTypes.Money, "f.unit_cost"),
                Col("cost_amount", "Cost", "التكلفة", FieldTypes.Money, "f.cost_amount"),
            ],
            "f.source_document_type", "f.source_document_id", null),

        new("open_items", L("Open items", "البنود المفتوحة"), L("Receivables and payables as they stand now: every open item with its partner, due date, aging bucket and what remains.", "الذمم المدينة والدائنة كما هي الآن: كل بند مفتوح مع الشريك وتاريخ الاستحقاق وشريحة التقادم وما تبقى."),
            "reporting.v_open_items", ["receivables.open_item.read", "payables.open_item.read"], "company_id", "branch_id", null, "posting_date", true,
            [Partner, Branch, SalesRep],
            [
                Dim("side", "Side", "الجانب", Documents, "f.side"),
                .. PeriodFields("posting_date", "Posting date", "تاريخ الترحيل"),
                Dim("due_date", "Due date", "تاريخ الاستحقاق", Time, "f.due_date", FieldTypes.Date),
                Dim("due_month", "Due month", "شهر الاستحقاق", Time, "to_char(f.due_date, 'YYYY-MM')"),
                Dim("aging_bucket", "Aging", "التقادم", Time, "CASE WHEN f.status = 'settled' THEN 'settled' WHEN f.due_date >= current_date THEN 'not_due' WHEN f.due_date >= current_date - 30 THEN '1_30' WHEN f.due_date >= current_date - 60 THEN '31_60' WHEN f.due_date >= current_date - 90 THEN '61_90' ELSE 'over_90' END"),
                Dim("partner", "Partner", "الشريك", Parties, "f.partner_id", FieldTypes.Id, "p.legal_name_i18n", "i18n", "p"),
                Dim("partner_code", "Partner code", "رمز الشريك", Parties, "p.code", join: "p"),
                Dim("sales_rep", "Sales representative", "مندوب المبيعات", Parties, "f.sales_rep_id", FieldTypes.Id, "r.name_i18n", "i18n", "r"),
                Dim("branch", "Branch", "الفرع", Places, "f.branch_id", FieldTypes.Id, "b.name_i18n", "i18n", "b"),
                Dim("kind", "Kind", "النوع", Documents, "f.kind"),
                Dim("document_type", "Document type", "نوع المستند", Documents, "f.document_type"),
                Dim("document_number", "Document number", "رقم المستند", Documents, "f.document_number"),
                Dim("status", "Status", "الحالة", Documents, "f.status"),
                Dim("currency", "Currency", "العملة", Documents, "f.currency"),
                Dim("is_blocked", "Blocked or on hold", "محجوز أو موقوف", Documents, "f.is_blocked", FieldTypes.Boolean),
                Measure("remaining_fc", "Remaining", "المتبقي", Amounts, "f.remaining_fc"),
                Measure("remaining_tc", "Remaining (transaction currency)", "المتبقي (عملة المعاملة)", Amounts, "f.remaining_tc"),
                Measure("original_fc", "Original", "الأصلي", Amounts, "f.original_fc"),
                Measure("settled_fc", "Settled", "المسدد", Amounts, "f.settled_fc"),
                Measure("overdue_fc", "Overdue", "المتأخر", Amounts, "CASE WHEN f.due_date < current_date AND f.status IN ('open', 'partially_settled') THEN f.remaining_fc ELSE 0 END"),
                Measure("item_count", "Items", "عدد البنود", Counts, "*", Aggregations.Count, FieldTypes.Number),
                Measure("partner_count", "Partners", "عدد الشركاء", Counts, "f.partner_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("side", "Side", "الجانب", FieldTypes.Text, "f.side"),
                Col("partner", "Partner", "الشريك", FieldTypes.Text, "p.legal_name_i18n", "p", "i18n"),
                Col("document_number", "Document", "المستند", FieldTypes.Text, "f.document_number"),
                Col("document_type", "Type", "النوع", FieldTypes.Text, "f.document_type"),
                Col("posting_date", "Posted", "الترحيل", FieldTypes.Date, "f.posting_date"),
                Col("due_date", "Due", "الاستحقاق", FieldTypes.Date, "f.due_date"),
                Col("status", "Status", "الحالة", FieldTypes.Text, "f.status"),
                Col("original_fc", "Original", "الأصلي", FieldTypes.Money, "f.original_fc"),
                Col("remaining_fc", "Remaining", "المتبقي", FieldTypes.Money, "f.remaining_fc"),
            ],
            "f.document_type", "f.document_id", "f.document_number",
            "side", new Dictionary<string, string>(StringComparer.Ordinal) { ["AR"] = "receivables.open_item.read", ["AP"] = "payables.open_item.read" }),

        new("stock_on_hand", L("Stock on hand", "المخزون المتاح"), L("What is in each warehouse now, by item: on hand, reserved, on quality hold and available.", "ما هو موجود في كل مستودع الآن، حسب الصنف: الموجود والمحجوز والموقوف للجودة والمتاح."),
            "reporting.v_stock_on_hand", ["inventory.stock.read"], "company_id", null, "warehouse_id", null, false,
            [Item, ItemCategory, Brand, Warehouse],
            [
                .. ItemFields,
                Dim("warehouse", "Warehouse", "المستودع", Places, "f.warehouse_id", FieldTypes.Id, "w.name_i18n", "i18n", "w"),
                Dim("warehouse_code", "Warehouse code", "رمز المستودع", Places, "w.code", join: "w"),
                Measure("on_hand", "On hand", "الموجود", Quantities, "f.on_hand", type: FieldTypes.Quantity),
                Measure("reserved", "Reserved", "المحجوز", Quantities, "f.reserved", type: FieldTypes.Quantity),
                Measure("quality_hold", "On quality hold", "موقوف للجودة", Quantities, "f.quality_hold", type: FieldTypes.Quantity),
                Measure("available", "Available", "المتاح", Quantities, "f.on_hand - f.reserved - f.quality_hold", type: FieldTypes.Quantity),
                Measure("item_count", "Items", "عدد الأصناف", Counts, "f.item_id", Aggregations.CountDistinct, FieldTypes.Number),
            ],
            [
                Col("item_code", "Item", "الصنف", FieldTypes.Text, "it.code", "it"),
                Col("item", "Item name", "اسم الصنف", FieldTypes.Text, "it.name_i18n", "it", "i18n"),
                Col("warehouse_code", "Warehouse", "المستودع", FieldTypes.Text, "w.code", "w"),
                Col("on_hand", "On hand", "الموجود", FieldTypes.Quantity, "f.on_hand"),
                Col("reserved", "Reserved", "المحجوز", FieldTypes.Quantity, "f.reserved"),
                Col("quality_hold", "Quality hold", "موقوف للجودة", FieldTypes.Quantity, "f.quality_hold"),
            ],
            null, null, null),
    ];

    public static ModelSpec? Find(string code) => Models.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.Ordinal));

    /// <summary>The tenant's own dimensions as fields of a model that carries a dimension set: key <c>dim_&lt;code&gt;</c>, keyed by the value, labelled by its name.</summary>
    public static IReadOnlyList<(FieldSpec Field, JoinSpec Join)> DimensionFields(IEnumerable<(string Code, LocalizedText Name)> dimensions)
    {
        var result = new List<(FieldSpec, JoinSpec)>();
        foreach (var (code, name) in dimensions)
        {
            var safe = new string(code.ToLowerInvariant().Where(static c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
            if (safe.Length == 0)
            {
                continue;
            }

            var alias = "dv_" + safe;
            var literal = code.Replace("'", "''", StringComparison.Ordinal);
            var join = new JoinSpec(alias, "app.org_dimension_values", $"{alias}.tenant_id = ds.tenant_id AND {alias}.id = (ds.values->>'{literal}')::uuid", "ds");
            result.Add((new FieldSpec("dim_" + safe, name, FieldRoles.Dimension, FieldTypes.Id, "dimensions", $"(ds.values->>'{literal}')::uuid", $"{alias}.name_i18n", "i18n", alias), join));
        }

        return result;
    }

    public static readonly JoinSpec DimensionSet = new("ds", "app.org_dimension_sets", "ds.tenant_id = f.tenant_id AND ds.id = f.dimension_set_id");
}

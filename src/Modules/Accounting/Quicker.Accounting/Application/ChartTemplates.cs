using Quicker.Accounting.Contracts;
using Quicker.Kernel.Text;

namespace Quicker.Accounting.Application;

/// <summary>One account of a template: <see cref="Parent"/> is the parent's code; headers carry no role.</summary>
public sealed record TemplateAccount(
    string Code,
    string? Parent,
    LocalizedText Name,
    string Type,
    string Subtype,
    string? Category,
    bool IsHeader,
    bool IsControl,
    string? Subledger,
    string? Role,
    string? CashFlow,
    bool RevalueFx,
    bool AllowManualPosting);

public sealed record TemplateCategory(string Code, LocalizedText Name, string Statement, int SortOrder);

/// <summary>A chart template: accounts, categories and, when the template targets a statutory chart, the mapping per account code.</summary>
public sealed record ChartTemplate(
    string Code,
    LocalizedText Name,
    LocalizedText Description,
    string AccountCodeFormat,
    IReadOnlyList<TemplateCategory> Categories,
    IReadOnlyList<TemplateAccount> Accounts,
    string? StatutoryChartCode,
    IReadOnlyDictionary<string, string> StatutoryMapping)
{
    public IReadOnlyList<string> Roles => Accounts.Where(static a => a.Role is not null).Select(static a => a.Role!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// The charts a new tenant starts from (ASSUMPTIONS A-003, A-088): an IFRS for SMEs layout covering every posting
/// role, a GCC variant with VAT and Zakat accounts, and the same layout mapped to the Iraqi Unified Accounting
/// System groups. Codes are four digits: thousands = type, hundreds = group, tens/units = account.
/// </summary>
public static class ChartTemplates
{
    public const string IfrsSme = "IFRS_SME";
    public const string Gcc = "GCC";
    public const string IraqUas = "IRAQ_UAS";

    public static readonly IReadOnlyList<TemplateCategory> Categories =
    [
        new("current_assets", LocalizedText.Bilingual("Current assets", "الموجودات المتداولة"), "bs", 10),
        new("non_current_assets", LocalizedText.Bilingual("Non-current assets", "الموجودات غير المتداولة"), "bs", 20),
        new("current_liabilities", LocalizedText.Bilingual("Current liabilities", "المطلوبات المتداولة"), "bs", 30),
        new("non_current_liabilities", LocalizedText.Bilingual("Non-current liabilities", "المطلوبات غير المتداولة"), "bs", 40),
        new("equity", LocalizedText.Bilingual("Equity", "حقوق الملكية"), "bs", 50),
        new("suspense", LocalizedText.Bilingual("Suspense and clearing", "حسابات معلقة ووسيطة"), "bs", 60),
        new("revenue", LocalizedText.Bilingual("Revenue", "الإيرادات"), "pl", 70),
        new("cost_of_sales", LocalizedText.Bilingual("Cost of sales", "كلفة المبيعات"), "pl", 80),
        new("operating_expenses", LocalizedText.Bilingual("Operating expenses", "المصروفات التشغيلية"), "pl", 90),
        new("other_income", LocalizedText.Bilingual("Other income", "إيرادات أخرى"), "pl", 100),
        new("other_expenses", LocalizedText.Bilingual("Finance costs and other expenses", "تكاليف التمويل ومصروفات أخرى"), "pl", 110),
        new("income_tax", LocalizedText.Bilingual("Income tax", "ضريبة الدخل"), "pl", 120),
    ];

    private static readonly IReadOnlyList<TemplateAccount> Base = BuildBase();

    public static readonly IReadOnlyList<ChartTemplate> All =
    [
        new(IfrsSme, LocalizedText.Bilingual("IFRS for SMEs", "المعايير الدولية للمنشآت الصغيرة والمتوسطة"),
            LocalizedText.Bilingual("Trading and distribution chart with control accounts for every subledger and a default account per posting role.", "دليل حسابات للتجارة والتوزيع مع حسابات مراقبة لكل دفتر مساعد وحساب افتراضي لكل دور ترحيل."),
            "####", Categories, Base, null, new Dictionary<string, string>(StringComparer.Ordinal)),
        new(Gcc, LocalizedText.Bilingual("GCC (VAT and Zakat)", "دول الخليج (ضريبة القيمة المضافة والزكاة)"),
            LocalizedText.Bilingual("The IFRS layout with VAT input/output accounts, Zakat payable and Zakat expense as used in Saudi Arabia and the UAE.", "التخطيط الدولي مع حسابات ضريبة القيمة المضافة والزكاة كما هو معمول به في السعودية والإمارات."),
            "####", Categories, BuildGcc(), null, new Dictionary<string, string>(StringComparer.Ordinal)),
        new(IraqUas, LocalizedText.Bilingual("Iraq (mapped to the Unified Accounting System)", "العراق (مربوط بالنظام المحاسبي الموحد)"),
            LocalizedText.Bilingual("The IFRS layout with every postable account mapped to a group of the Iraqi Unified Accounting System for statutory reports.", "التخطيط الدولي مع ربط كل حساب قابل للترحيل بمجموعة من النظام المحاسبي الموحد العراقي للتقارير النظامية."),
            "####", Categories, Base, "IRAQ_UAS", BuildIraqMapping(Base)),
    ];

    public static ChartTemplate? Find(string code) => All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

    private static TemplateAccount H(string code, string? parent, string en, string ar, string type, string? category = null) =>
        new(code, parent, LocalizedText.Bilingual(en, ar), type, string.Empty, category, IsHeader: true, IsControl: false, null, null, null, RevalueFx: false, AllowManualPosting: false);

    private static TemplateAccount A(string code, string parent, string en, string ar, string type, string subtype, string? category = null, string? role = null, string? subledger = null, string? cashFlow = null, bool revalue = false, bool? manual = null) =>
        new(code, parent, LocalizedText.Bilingual(en, ar), type, subtype, category, IsHeader: false, IsControl: subledger is not null, subledger, role, cashFlow, revalue, manual ?? subledger is null);

    private static List<TemplateAccount> BuildBase()
    {
        const string asset = AccountTypes.Asset;
        const string liability = AccountTypes.Liability;
        const string equity = AccountTypes.Equity;
        const string revenue = AccountTypes.Revenue;
        const string expense = AccountTypes.Expense;
        return
        [
            H("1000", null, "Assets", "الموجودات", asset),
            H("1100", "1000", "Current assets", "الموجودات المتداولة", asset, "current_assets"),
            H("1110", "1100", "Cash and cash equivalents", "النقد وما في حكمه", asset, "current_assets"),
            A("1111", "1110", "Cash on hand", "النقد في الصندوق", asset, "cash", "current_assets", AccountRoles.Cash, SubledgerTypes.Bank, CashFlowCategories.Cash),
            A("1112", "1110", "Petty cash", "الصندوق النثري", asset, "cash", "current_assets", AccountRoles.PettyCash, SubledgerTypes.Bank, CashFlowCategories.Cash),
            A("1121", "1110", "Bank – current accounts", "البنك – الحسابات الجارية", asset, "bank", "current_assets", AccountRoles.Bank, SubledgerTypes.Bank, CashFlowCategories.Cash, revalue: true),
            A("1122", "1110", "Bank – deposits", "البنك – الودائع", asset, "bank", "current_assets", null, SubledgerTypes.Bank, CashFlowCategories.Cash, revalue: true),
            A("1131", "1110", "Cheques under collection", "شيكات برسم التحصيل", asset, "cheques", "current_assets", AccountRoles.ChequesUnderCollection, SubledgerTypes.PostDatedCheques, CashFlowCategories.Operating),
            A("1132", "1110", "Post-dated cheques receivable", "شيكات مؤجلة مقبوضة", asset, "cheques", "current_assets", AccountRoles.PdcReceivable, SubledgerTypes.PostDatedCheques, CashFlowCategories.Operating),
            H("1200", "1100", "Receivables", "الذمم المدينة", asset, "current_assets"),
            A("1210", "1200", "Trade receivables", "ذمم العملاء", asset, "receivable", "current_assets", AccountRoles.AR, SubledgerTypes.Receivables, CashFlowCategories.Operating, revalue: true),
            A("1220", "1200", "Intercompany receivables", "ذمم الشركات الشقيقة المدينة", asset, "receivable", "current_assets", AccountRoles.IcReceivable, SubledgerTypes.Intercompany, CashFlowCategories.Operating, revalue: true),
            A("1230", "1200", "Advances to suppliers", "دفعات مقدمة للموردين", asset, "advance", "current_assets", AccountRoles.SupplierAdvances, SubledgerTypes.Payables, CashFlowCategories.Operating, revalue: true),
            A("1240", "1200", "Employee advances", "سلف الموظفين", asset, "advance", "current_assets", cashFlow: CashFlowCategories.Operating),
            A("1250", "1200", "Other receivables", "ذمم مدينة أخرى", asset, "receivable", "current_assets", cashFlow: CashFlowCategories.Operating),
            A("1260", "1200", "Allowance for doubtful debts", "مخصص الديون المشكوك في تحصيلها", asset, "contra_asset", "current_assets", cashFlow: CashFlowCategories.Operating),
            A("1270", "1200", "Withholding tax receivable", "ضريبة الاستقطاع المستحقة القبض", asset, "tax", "current_assets", AccountRoles.WhtReceivable, cashFlow: CashFlowCategories.Operating),
            A("1280", "1200", "Input tax", "ضريبة المدخلات", asset, "tax", "current_assets", AccountRoles.InputTax, cashFlow: CashFlowCategories.Operating),
            H("1300", "1100", "Inventories", "المخزون", asset, "current_assets"),
            A("1310", "1300", "Inventory – goods for resale", "المخزون – بضاعة للبيع", asset, "inventory", "current_assets", AccountRoles.Inventory, SubledgerTypes.Inventory, CashFlowCategories.Operating),
            A("1320", "1300", "Inventory in transit", "بضاعة في الطريق", asset, "inventory", "current_assets", AccountRoles.InventoryInTransit, SubledgerTypes.Inventory, CashFlowCategories.Operating),
            A("1330", "1300", "Consigned stock at customers", "بضاعة أمانة لدى العملاء", asset, "inventory", "current_assets", AccountRoles.InventoryConsignedOut, SubledgerTypes.Inventory, CashFlowCategories.Operating),
            H("1400", "1100", "Prepayments and other current assets", "المصروفات المدفوعة مقدماً وموجودات متداولة أخرى", asset, "current_assets"),
            A("1410", "1400", "Prepaid expenses", "مصروفات مدفوعة مقدماً", asset, "prepayment", "current_assets", AccountRoles.Prepayments, cashFlow: CashFlowCategories.Operating),
            A("1420", "1400", "Unbilled revenue", "إيرادات غير مفوترة", asset, "accrued_income", "current_assets", AccountRoles.UnbilledRevenue, cashFlow: CashFlowCategories.Operating),
            A("1430", "1400", "Deposits and guarantees paid", "تأمينات وضمانات مدفوعة", asset, "deposit", "current_assets", cashFlow: CashFlowCategories.Operating),
            H("1500", "1000", "Non-current assets", "الموجودات غير المتداولة", asset, "non_current_assets"),
            A("1510", "1500", "Property, plant and equipment – cost", "الممتلكات والمعدات – الكلفة", asset, "fixed_asset", "non_current_assets", AccountRoles.FaCost, SubledgerTypes.FixedAssets, CashFlowCategories.Investing),
            A("1520", "1500", "Accumulated depreciation", "مجمع الإهلاك", asset, "contra_asset", "non_current_assets", AccountRoles.FaAccDep, SubledgerTypes.FixedAssets, CashFlowCategories.Investing),
            A("1530", "1500", "Accumulated impairment", "مجمع انخفاض القيمة", asset, "contra_asset", "non_current_assets", AccountRoles.FaAccImpairment, SubledgerTypes.FixedAssets, CashFlowCategories.Investing),
            A("1540", "1500", "Fixed asset clearing", "حساب وسيط للموجودات الثابتة", asset, "clearing", "non_current_assets", AccountRoles.FaClearing, cashFlow: CashFlowCategories.Investing),
            A("1550", "1500", "Intangible assets", "الموجودات غير الملموسة", asset, "intangible", "non_current_assets", cashFlow: CashFlowCategories.Investing),
            A("1560", "1500", "Investments in subsidiaries", "استثمارات في شركات تابعة", asset, "investment", "non_current_assets", cashFlow: CashFlowCategories.Investing),

            H("2000", null, "Liabilities", "المطلوبات", liability),
            H("2100", "2000", "Current liabilities", "المطلوبات المتداولة", liability, "current_liabilities"),
            A("2110", "2100", "Trade payables", "ذمم الموردين", liability, "payable", "current_liabilities", AccountRoles.AP, SubledgerTypes.Payables, CashFlowCategories.Operating, revalue: true),
            A("2120", "2100", "Goods received not invoiced", "بضاعة مستلمة غير مفوترة", liability, "accrual", "current_liabilities", AccountRoles.GRNI, SubledgerTypes.GoodsReceivedNotInvoiced, CashFlowCategories.Operating),
            A("2130", "2100", "Landed cost clearing", "حساب وسيط لتكاليف الاستيراد", liability, "accrual", "current_liabilities", AccountRoles.LandedCostClearing, SubledgerTypes.GoodsReceivedNotInvoiced, CashFlowCategories.Operating),
            A("2140", "2100", "Intercompany payables", "ذمم الشركات الشقيقة الدائنة", liability, "payable", "current_liabilities", AccountRoles.IcPayable, SubledgerTypes.Intercompany, CashFlowCategories.Operating, revalue: true),
            A("2150", "2100", "Post-dated cheques payable", "شيكات مؤجلة مدفوعة", liability, "cheques", "current_liabilities", AccountRoles.PdcPayable, SubledgerTypes.PostDatedCheques, CashFlowCategories.Operating),
            A("2160", "2100", "Customer deposits and advances", "دفعات مقدمة من العملاء", liability, "deposit", "current_liabilities", AccountRoles.CustomerDeposits, cashFlow: CashFlowCategories.Operating, revalue: true),
            A("2170", "2100", "Accrued expenses", "مصروفات مستحقة", liability, "accrual", "current_liabilities", AccountRoles.AccruedExpenses, cashFlow: CashFlowCategories.Operating),
            A("2180", "2100", "Deferred revenue", "إيرادات مؤجلة", liability, "deferral", "current_liabilities", AccountRoles.DeferredRevenue, cashFlow: CashFlowCategories.Operating),
            A("2190", "2100", "Employee payables", "ذمم الموظفين الدائنة", liability, "payable", "current_liabilities", AccountRoles.EmployeePayable, cashFlow: CashFlowCategories.Operating),
            H("2200", "2100", "Taxes payable", "الضرائب المستحقة", liability, "current_liabilities"),
            A("2210", "2200", "Output tax", "ضريبة المخرجات", liability, "tax", "current_liabilities", AccountRoles.OutputTax, cashFlow: CashFlowCategories.Operating),
            A("2220", "2200", "Withholding tax payable", "ضريبة الاستقطاع المستحقة الدفع", liability, "tax", "current_liabilities", AccountRoles.WhtPayable, cashFlow: CashFlowCategories.Operating),
            A("2230", "2200", "Income tax payable", "ضريبة الدخل المستحقة", liability, "tax", "current_liabilities", cashFlow: CashFlowCategories.Operating),
            A("2240", "2200", "Other taxes payable", "ضرائب أخرى مستحقة", liability, "tax", "current_liabilities", cashFlow: CashFlowCategories.Operating),
            A("2250", "2100", "Commissions payable", "عمولات مستحقة الدفع", liability, "payable", "current_liabilities", AccountRoles.CommissionPayable, cashFlow: CashFlowCategories.Operating),
            A("2260", "2100", "Short-term loans", "قروض قصيرة الأجل", liability, "loan", "current_liabilities", cashFlow: CashFlowCategories.Financing, revalue: true),
            A("2270", "2100", "Dividends payable", "أرباح موزعة مستحقة الدفع", liability, "payable", "current_liabilities", cashFlow: CashFlowCategories.Financing),
            H("2500", "2000", "Non-current liabilities", "المطلوبات غير المتداولة", liability, "non_current_liabilities"),
            A("2510", "2500", "Long-term loans", "قروض طويلة الأجل", liability, "loan", "non_current_liabilities", cashFlow: CashFlowCategories.Financing, revalue: true),
            A("2520", "2500", "Provisions", "المخصصات", liability, "provision", "non_current_liabilities", cashFlow: CashFlowCategories.Operating),
            A("2530", "2500", "Employee end-of-service benefits", "مكافأة نهاية الخدمة", liability, "provision", "non_current_liabilities", cashFlow: CashFlowCategories.Operating),

            H("3000", null, "Equity", "حقوق الملكية", equity, "equity"),
            A("3100", "3000", "Share capital", "رأس المال", equity, "capital", "equity", cashFlow: CashFlowCategories.Financing),
            A("3200", "3000", "Retained earnings", "الأرباح المحتجزة", equity, "retained_earnings", "equity", AccountRoles.RetainedEarnings),
            A("3300", "3000", "Current year earnings", "أرباح السنة الحالية", equity, "retained_earnings", "equity", AccountRoles.CurrentYearEarnings),
            A("3400", "3000", "Opening balance equity", "حقوق ملكية الأرصدة الافتتاحية", equity, "opening", "equity", AccountRoles.OpeningBalanceEquity),
            A("3500", "3000", "Revaluation surplus", "فائض إعادة التقييم", equity, "oci", "equity", AccountRoles.RevaluationSurplus),
            A("3600", "3000", "Dividends declared", "أرباح موزعة", equity, "distribution", "equity", cashFlow: CashFlowCategories.Financing),
            A("3700", "3000", "Legal reserve", "الاحتياطي القانوني", equity, "reserve", "equity"),

            H("4000", null, "Revenue", "الإيرادات", revenue, "revenue"),
            A("4100", "4000", "Sales – goods", "مبيعات البضائع", revenue, "sales", "revenue", AccountRoles.Revenue),
            A("4200", "4000", "Sales – services", "إيرادات الخدمات", revenue, "sales", "revenue"),
            A("4300", "4000", "Sales returns and allowances", "مردودات ومسموحات المبيعات", revenue, "contra_revenue", "revenue", AccountRoles.SalesReturns),
            A("4400", "4000", "Settlement discounts given", "خصومات تسوية ممنوحة", revenue, "contra_revenue", "revenue", AccountRoles.DiscountGiven),
            H("4500", "4000", "Other income", "إيرادات أخرى", revenue, "other_income"),
            A("4510", "4500", "Settlement discounts taken", "خصومات تسوية مكتسبة", revenue, "other_income", "other_income", AccountRoles.DiscountTaken),
            A("4520", "4500", "Interest income", "إيرادات الفوائد", revenue, "other_income", "other_income", AccountRoles.InterestIncome),
            A("4530", "4500", "Realized foreign exchange gains", "أرباح فروقات عملة محققة", revenue, "fx", "other_income", AccountRoles.FxGainRealized),
            A("4540", "4500", "Unrealized foreign exchange gains and losses", "أرباح وخسائر فروقات عملة غير محققة", revenue, "fx", "other_income", AccountRoles.FxUnrealized),
            A("4550", "4500", "Gain or loss on disposal of assets", "أرباح أو خسائر بيع الموجودات", revenue, "other_income", "other_income", AccountRoles.GainLossOnDisposal),
            A("4560", "4500", "Miscellaneous income", "إيرادات متنوعة", revenue, "other_income", "other_income"),

            H("5000", null, "Cost of sales", "كلفة المبيعات", expense, "cost_of_sales"),
            A("5100", "5000", "Cost of goods sold", "كلفة البضاعة المباعة", expense, "cogs", "cost_of_sales", AccountRoles.Cogs),
            A("5200", "5000", "Inventory adjustments", "تسويات المخزون", expense, "cogs", "cost_of_sales", AccountRoles.InventoryAdjustment),
            A("5210", "5000", "Stock count variances", "فروقات الجرد", expense, "cogs", "cost_of_sales", AccountRoles.CountVariance),
            A("5220", "5000", "Scrap and wastage", "التالف والهدر", expense, "cogs", "cost_of_sales", AccountRoles.Scrap),
            A("5230", "5000", "Inventory write-down", "انخفاض قيمة المخزون", expense, "cogs", "cost_of_sales", AccountRoles.InventoryWriteDown),
            A("5300", "5000", "Purchase price variance", "فروقات أسعار الشراء", expense, "variance", "cost_of_sales", AccountRoles.PurchasePriceVariance),
            A("5310", "5000", "Assembly variance", "فروقات التجميع", expense, "variance", "cost_of_sales", AccountRoles.AssemblyVariance),
            A("5400", "5000", "Freight and handling expensed", "مصاريف الشحن والمناولة", expense, "cogs", "cost_of_sales"),

            H("6000", null, "Operating expenses", "المصروفات التشغيلية", expense, "operating_expenses"),
            A("6100", "6000", "Salaries and wages", "الرواتب والأجور", expense, "opex", "operating_expenses"),
            A("6110", "6000", "Rent", "الإيجار", expense, "opex", "operating_expenses"),
            A("6120", "6000", "Utilities", "الخدمات (كهرباء وماء)", expense, "opex", "operating_expenses"),
            A("6130", "6000", "Marketing and advertising", "التسويق والإعلان", expense, "opex", "operating_expenses"),
            A("6140", "6000", "Travel and transport", "السفر والتنقل", expense, "opex", "operating_expenses"),
            A("6155", "6000", "Purchased services and supplies", "خدمات ومستلزمات مشتراة", expense, "opex", "operating_expenses", AccountRoles.PurchaseExpense),
            A("6150", "6000", "Office supplies", "اللوازم المكتبية", expense, "opex", "operating_expenses"),
            A("6160", "6000", "Professional fees", "الأتعاب المهنية", expense, "opex", "operating_expenses"),
            A("6170", "6000", "Insurance", "التأمين", expense, "opex", "operating_expenses"),
            A("6180", "6000", "Repairs and maintenance", "الصيانة والإصلاح", expense, "opex", "operating_expenses"),
            A("6190", "6000", "Communication", "الاتصالات", expense, "opex", "operating_expenses"),
            A("6200", "6000", "Depreciation expense", "مصروف الإهلاك", expense, "depreciation", "operating_expenses", AccountRoles.DepreciationExpense),
            A("6210", "6000", "Impairment loss", "خسائر انخفاض القيمة", expense, "depreciation", "operating_expenses", AccountRoles.ImpairmentLoss),
            A("6300", "6000", "Bad debt expense", "مصروف الديون المعدومة", expense, "opex", "operating_expenses", AccountRoles.BadDebt),
            A("6310", "6000", "Write-offs", "شطب الأرصدة", expense, "opex", "operating_expenses", AccountRoles.WriteOff),
            A("6400", "6000", "Commission expense", "مصروف العمولات", expense, "opex", "operating_expenses", AccountRoles.CommissionExpense),
            H("6500", "6000", "Finance costs", "تكاليف التمويل", expense, "other_expenses"),
            A("6510", "6500", "Bank charges", "عمولات ومصاريف بنكية", expense, "finance", "other_expenses", AccountRoles.BankCharges),
            A("6520", "6500", "Interest expense", "مصروف الفوائد", expense, "finance", "other_expenses", AccountRoles.InterestExpense),
            A("6530", "6500", "Realized foreign exchange losses", "خسائر فروقات عملة محققة", expense, "fx", "other_expenses", AccountRoles.FxLossRealized),
            A("6600", "6000", "Other operating expenses", "مصروفات تشغيلية أخرى", expense, "opex", "operating_expenses"),
            A("6900", "6000", "Rounding differences", "فروقات التقريب", expense, "rounding", "other_expenses", AccountRoles.RoundingDifferences),

            H("7000", null, "Income tax", "ضريبة الدخل", expense, "income_tax"),
            A("7100", "7000", "Income tax expense", "مصروف ضريبة الدخل", expense, "income_tax", "income_tax"),

            H("9000", null, "Suspense and clearing", "حسابات معلقة ووسيطة", liability, "suspense"),
            A("9100", "9000", "Suspense account", "الحساب المعلق", liability, "suspense", "suspense", AccountRoles.Suspense),
        ];
    }

    private static List<TemplateAccount> BuildGcc()
    {
        var accounts = Base.Select(static a => a.Code switch
        {
            "1280" => a with { Name = LocalizedText.Bilingual("Input VAT", "ضريبة القيمة المضافة – المدخلات") },
            "2210" => a with { Name = LocalizedText.Bilingual("Output VAT", "ضريبة القيمة المضافة – المخرجات") },
            "7000" => a with { Name = LocalizedText.Bilingual("Zakat and income tax", "الزكاة وضريبة الدخل") },
            "7100" => a with { Name = LocalizedText.Bilingual("Corporate income tax expense", "مصروف ضريبة دخل الشركات") },
            _ => a,
        }).ToList();
        var zakatPayable = A("2235", "2200", "Zakat payable", "الزكاة المستحقة", AccountTypes.Liability, "tax", "current_liabilities", cashFlow: CashFlowCategories.Operating);
        var zakatExpense = A("7200", "7000", "Zakat expense", "مصروف الزكاة", AccountTypes.Expense, "income_tax", "income_tax");
        accounts.Insert(accounts.FindIndex(static a => a.Code == "2240"), zakatPayable);
        accounts.Add(zakatExpense);
        return accounts;
    }

    /// <summary>Postable accounts to the Unified Accounting System groups: by type and, for assets and liabilities, by group.</summary>
    private static Dictionary<string, string> BuildIraqMapping(IReadOnlyList<TemplateAccount> accounts)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in accounts.Where(static a => !a.IsHeader))
        {
            var group = account.Type switch
            {
                AccountTypes.Asset when account.Code.StartsWith("15", StringComparison.Ordinal) => "1",
                AccountTypes.Asset when account.Code.StartsWith("13", StringComparison.Ordinal) => "2",
                AccountTypes.Asset => "3",
                AccountTypes.Liability when account.Code.StartsWith("25", StringComparison.Ordinal) => "4",
                AccountTypes.Liability when account.Code.StartsWith('9') => null,
                AccountTypes.Liability => "5",
                AccountTypes.Equity => "4",
                AccountTypes.Revenue => "7",
                AccountTypes.Expense => "6",
                _ => null,
            };
            if (group is not null)
            {
                mapping[account.Code] = group;
            }
        }

        return mapping;
    }
}

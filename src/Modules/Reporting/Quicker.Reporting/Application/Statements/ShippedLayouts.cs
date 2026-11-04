using Quicker.Accounting.Contracts;
using Quicker.Reporting.Domain;

namespace Quicker.Reporting.Application.Statements;

/// <summary>
/// The statements every tenant has from day one (ADR-0034 §5): a profit and loss, a balance sheet and a trial
/// balance, written against account types, subtypes and categories so they fit every shipped chart and any chart
/// built on the same vocabulary. Every posting account lands in exactly one row of each statement (the catch-all
/// rows take what the named rows do not), which is what makes the figures tie to the ledger.
/// </summary>
public static class ShippedLayouts
{
    public const string ProfitAndLoss = "PL";
    public const string BalanceSheet = "BS";
    public const string TrialBalance = "TB";

    private static readonly IReadOnlyDictionary<string, string> NoText = new Dictionary<string, string>(StringComparer.Ordinal);

    public static readonly IReadOnlyList<LayoutSummary> All =
    [
        new(null, ProfitAndLoss, LayoutKinds.ProfitAndLoss, L("Profit and loss", "قائمة الأرباح والخسائر"),
            L("Revenue less cost of sales, operating expenses, finance costs and tax, for the period, the year to date and against the prior year.", "الإيرادات ناقص كلفة المبيعات والمصروفات التشغيلية وتكاليف التمويل والضريبة، للفترة ومنذ بداية السنة ومقارنة بالسنة السابقة."),
            null, null, true, true, true, 10,
            new LayoutDefinition(
                [
                    Header("REV_HEAD", "Revenue", "الإيرادات"),
                    Accounts("SALES", "Sales", "المبيعات", new AccountFilter(Types: [AccountTypes.Revenue], Subtypes: ["sales"]), flip: true, indent: 1, showAccounts: true),
                    Accounts("CONTRA", "Returns and discounts", "المردودات والخصومات", new AccountFilter(Types: [AccountTypes.Revenue], Subtypes: ["contra_revenue"]), flip: true, indent: 1, showAccounts: true, hideWhenZero: true),
                    Formula("NET_REVENUE", "Net revenue", "صافي الإيرادات", "SALES + CONTRA", bold: true),
                    Accounts("COGS", "Cost of sales", "كلفة المبيعات", new AccountFilter(Types: [AccountTypes.Expense], Subtypes: ["cogs", "variance"]), showAccounts: true),
                    Formula("GROSS", "Gross profit", "مجمل الربح", "NET_REVENUE - COGS", bold: true),
                    Accounts("OTHER_INCOME", "Other income", "إيرادات أخرى", new AccountFilter(Types: [AccountTypes.Revenue], ExcludeSubtypes: ["sales", "contra_revenue"]), flip: true, showAccounts: true, hideWhenZero: true),
                    Accounts("OPEX", "Operating expenses", "المصروفات التشغيلية", new AccountFilter(Types: [AccountTypes.Expense], ExcludeSubtypes: ["cogs", "variance", "finance", "fx", "income_tax"]), showAccounts: true),
                    Accounts("FINANCE", "Finance costs and exchange losses", "تكاليف التمويل وخسائر الصرف", new AccountFilter(Types: [AccountTypes.Expense], Subtypes: ["finance", "fx"]), showAccounts: true, hideWhenZero: true),
                    Formula("EBT", "Profit before tax", "الربح قبل الضريبة", "GROSS + OTHER_INCOME - OPEX - FINANCE", bold: true),
                    Accounts("TAX", "Income tax", "ضريبة الدخل", new AccountFilter(Types: [AccountTypes.Expense], Subtypes: ["income_tax"]), hideWhenZero: true),
                    Formula("NET", "Net profit", "صافي الربح", "EBT - TAX", bold: true),
                ],
                [
                    Column("PERIOD", ColumnTypes.Period, "Period", "الفترة"),
                    Column("YTD", ColumnTypes.YearToDate, "Year to date", "منذ بداية السنة"),
                    Column("PRIOR_YEAR", ColumnTypes.PriorYear, "Prior year", "السنة السابقة"),
                    Column("VAR", ColumnTypes.Variance, "Change", "التغير", new ColumnParameters(Of: "PERIOD", Against: "PRIOR_YEAR")),
                    Column("VAR_PCT", ColumnTypes.VariancePct, "Change %", "التغير %", new ColumnParameters(Of: "PERIOD", Against: "PRIOR_YEAR")),
                ]),
            null),
        new(null, BalanceSheet, LayoutKinds.BalanceSheet, L("Balance sheet", "قائمة المركز المالي"),
            L("Assets against liabilities and equity at the period end, with the year's earnings before closing, and a year earlier.", "الموجودات مقابل المطلوبات وحقوق الملكية في نهاية الفترة، مع أرباح السنة قبل الإقفال، وقبل سنة."),
            null, null, true, true, true, 20,
            new LayoutDefinition(
                [
                    Header("ASSETS_HEAD", "Assets", "الموجودات"),
                    Accounts("CURRENT_ASSETS", "Current assets", "الموجودات المتداولة", new AccountFilter(Types: [AccountTypes.Asset], Categories: ["current_assets"]), indent: 1, showAccounts: true),
                    Accounts("NON_CURRENT_ASSETS", "Non-current assets", "الموجودات غير المتداولة", new AccountFilter(Types: [AccountTypes.Asset], Categories: ["non_current_assets"]), indent: 1, showAccounts: true),
                    Accounts("OTHER_ASSETS", "Other assets", "موجودات أخرى", new AccountFilter(Types: [AccountTypes.Asset], ExcludeCategories: ["current_assets", "non_current_assets"]), indent: 1, showAccounts: true, hideWhenZero: true),
                    Formula("TOTAL_ASSETS", "Total assets", "مجموع الموجودات", "CURRENT_ASSETS + NON_CURRENT_ASSETS + OTHER_ASSETS", bold: true),
                    Blank("B1"),
                    Header("LIAB_HEAD", "Liabilities", "المطلوبات"),
                    Accounts("CURRENT_LIABILITIES", "Current liabilities", "المطلوبات المتداولة", new AccountFilter(Types: [AccountTypes.Liability], Categories: ["current_liabilities"]), flip: true, indent: 1, showAccounts: true),
                    Accounts("NON_CURRENT_LIABILITIES", "Non-current liabilities", "المطلوبات غير المتداولة", new AccountFilter(Types: [AccountTypes.Liability], Categories: ["non_current_liabilities"]), flip: true, indent: 1, showAccounts: true),
                    Accounts("OTHER_LIABILITIES", "Other liabilities", "مطلوبات أخرى", new AccountFilter(Types: [AccountTypes.Liability], ExcludeCategories: ["current_liabilities", "non_current_liabilities"]), flip: true, indent: 1, showAccounts: true, hideWhenZero: true),
                    Formula("TOTAL_LIABILITIES", "Total liabilities", "مجموع المطلوبات", "CURRENT_LIABILITIES + NON_CURRENT_LIABILITIES + OTHER_LIABILITIES", bold: true),
                    Header("EQUITY_HEAD", "Equity", "حقوق الملكية"),
                    Accounts("EQUITY", "Equity", "حقوق الملكية", new AccountFilter(Types: [AccountTypes.Equity]), flip: true, indent: 1, showAccounts: true),
                    Accounts("CURRENT_EARNINGS", "Earnings not yet closed", "أرباح لم تُقفل بعد", new AccountFilter(Types: [AccountTypes.Revenue, AccountTypes.Expense]), flip: true, indent: 1),
                    Formula("TOTAL_EQUITY", "Total equity", "مجموع حقوق الملكية", "EQUITY + CURRENT_EARNINGS", bold: true),
                    Formula("TOTAL_LIAB_EQUITY", "Total liabilities and equity", "مجموع المطلوبات وحقوق الملكية", "TOTAL_LIABILITIES + TOTAL_EQUITY", bold: true),
                    Formula("CHECK", "Difference (should be zero)", "الفرق (يجب أن يكون صفرًا)", "TOTAL_ASSETS - TOTAL_LIAB_EQUITY", hideWhenZero: true),
                ],
                [
                    Column("PERIOD", ColumnTypes.Period, "As of the period end", "في نهاية الفترة"),
                    Column("PRIOR_YEAR", ColumnTypes.PriorYear, "A year earlier", "قبل سنة"),
                    Column("VAR", ColumnTypes.Variance, "Change", "التغير", new ColumnParameters(Of: "PERIOD", Against: "PRIOR_YEAR")),
                    Column("VAR_PCT", ColumnTypes.VariancePct, "Change %", "التغير %", new ColumnParameters(Of: "PERIOD", Against: "PRIOR_YEAR")),
                ]),
            null),
        new(null, TrialBalance, LayoutKinds.TrialBalance, L("Trial balance", "ميزان المراجعة"),
            L("Every account with its opening balance, the period's debits and credits and its closing balance; the totals balance.", "كل حساب مع رصيده الافتتاحي ومدين ودائن الفترة ورصيده الختامي؛ والمجاميع متوازنة."),
            null, null, true, true, true, 30,
            new LayoutDefinition(
                [
                    Accounts("ASSETS", "Assets", "الموجودات", new AccountFilter(Types: [AccountTypes.Asset]), showAccounts: true, bold: true),
                    Accounts("LIABILITIES", "Liabilities", "المطلوبات", new AccountFilter(Types: [AccountTypes.Liability]), showAccounts: true, bold: true),
                    Accounts("EQUITY", "Equity", "حقوق الملكية", new AccountFilter(Types: [AccountTypes.Equity]), showAccounts: true, bold: true),
                    Accounts("REVENUE", "Revenue", "الإيرادات", new AccountFilter(Types: [AccountTypes.Revenue]), showAccounts: true, bold: true),
                    Accounts("EXPENSES", "Expenses", "المصروفات", new AccountFilter(Types: [AccountTypes.Expense]), showAccounts: true, bold: true),
                    Formula("TOTAL", "Total", "المجموع", "ASSETS + LIABILITIES + EQUITY + REVENUE + EXPENSES", bold: true),
                ],
                [
                    Column("OPENING", ColumnTypes.Opening, "Opening", "الرصيد الافتتاحي"),
                    Column("DEBITS", ColumnTypes.Debits, "Debits", "مدين"),
                    Column("CREDITS", ColumnTypes.Credits, "Credits", "دائن"),
                    Column("CLOSING", ColumnTypes.Closing, "Closing", "الرصيد الختامي"),
                ]),
            null),
    ];

    public static LayoutSummary? Find(string code) => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> L(string en, string ar) => new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static LayoutRowDefinition Header(string code, string en, string ar) => new(code, RowTypes.Header, L(en, ar), Bold: true);

    private static LayoutRowDefinition Blank(string code) => new(code, RowTypes.Blank, NoText);

    private static LayoutRowDefinition Accounts(string code, string en, string ar, AccountFilter filter, bool flip = false, int indent = 0, bool bold = false, bool showAccounts = false, bool hideWhenZero = false) =>
        new(code, RowTypes.Accounts, L(en, ar), filter, null, null, flip, indent, bold, showAccounts, hideWhenZero);

    private static LayoutRowDefinition Formula(string code, string en, string ar, string formula, bool bold = false, bool hideWhenZero = false) =>
        new(code, RowTypes.Formula, L(en, ar), null, null, formula, false, 0, bold, false, hideWhenZero);

    private static LayoutColumnDefinition Column(string code, string type, string en, string ar, ColumnParameters? parameters = null) => new(code, type, L(en, ar), parameters);
}

using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Accounting.Contracts;

public static class AccountTypes
{
    public const string Asset = "asset";
    public const string Liability = "liability";
    public const string Equity = "equity";
    public const string Revenue = "revenue";
    public const string Expense = "expense";

    public static readonly IReadOnlyList<string> All = [Asset, Liability, Equity, Revenue, Expense];

    /// <summary>Balance-sheet types carry their balance across years; the others close to retained earnings.</summary>
    public static bool IsBalanceSheet(string type) => type is Asset or Liability or Equity;
}

/// <summary>Subledgers a control account reconciles to (ADR-0006).</summary>
public static class SubledgerTypes
{
    public const string Receivables = "AR";
    public const string Payables = "AP";
    public const string Inventory = "INV";
    public const string FixedAssets = "FA";
    public const string Bank = "BANK";
    public const string PostDatedCheques = "PDC";
    public const string GoodsReceivedNotInvoiced = "GRNI";
    public const string Intercompany = "IC";
    public const string Withholding = "WHT";

    public static readonly IReadOnlyList<string> All = [Receivables, Payables, Inventory, FixedAssets, Bank, PostDatedCheques, GoodsReceivedNotInvoiced, Intercompany, Withholding];
}

public static class DimensionRules
{
    public const string Required = "required";
    public const string Optional = "optional";
    public const string Blocked = "blocked";

    public static readonly IReadOnlyList<string> All = [Required, Optional, Blocked];
}

public static class CashFlowCategories
{
    public const string Cash = "cash";
    public const string Operating = "operating";
    public const string Investing = "investing";
    public const string Financing = "financing";

    public static readonly IReadOnlyList<string> All = [Cash, Operating, Investing, Financing];
}

/// <summary>
/// Account roles of the posting rules matrix (POSTING_RULES §1). Modules post by role; the company's posting
/// profile maps a role and its keys to an account. A chart template names a default account per role so a
/// new company can post on day one.
/// </summary>
public static class AccountRoles
{
    public const string AR = "AR";
    public const string AP = "AP";
    public const string Inventory = "Inventory";
    public const string InventoryInTransit = "InventoryInTransit";
    public const string InventoryConsignedOut = "InventoryConsignedOut";
    public const string GRNI = "GRNI";
    public const string LandedCostClearing = "LandedCostClearing";
    public const string Cogs = "Cogs";
    public const string Revenue = "Revenue";
    public const string SalesReturns = "SalesReturns";
    public const string DiscountGiven = "DiscountGiven";
    public const string DiscountTaken = "DiscountTaken";
    public const string OutputTax = "OutputTax";
    public const string InputTax = "InputTax";
    public const string WhtPayable = "WhtPayable";
    public const string WhtReceivable = "WhtReceivable";
    public const string Bank = "Bank";
    public const string Cash = "Cash";
    public const string PettyCash = "PettyCash";
    public const string ChequesUnderCollection = "ChequesUnderCollection";
    public const string PdcReceivable = "PdcReceivable";
    public const string PdcPayable = "PdcPayable";
    public const string BankCharges = "BankCharges";
    public const string InterestIncome = "InterestIncome";
    public const string InterestExpense = "InterestExpense";
    public const string FxGainRealized = "FxGainRealized";
    public const string FxLossRealized = "FxLossRealized";
    public const string FxUnrealized = "FxUnrealized";
    public const string CustomerDeposits = "CustomerDeposits";
    public const string SupplierAdvances = "SupplierAdvances";
    public const string UnbilledRevenue = "UnbilledRevenue";
    public const string InventoryAdjustment = "InventoryAdjustment";
    public const string CountVariance = "CountVariance";
    public const string Scrap = "Scrap";
    public const string InventoryWriteDown = "InventoryWriteDown";
    public const string PurchasePriceVariance = "PurchasePriceVariance";
    public const string AssemblyVariance = "AssemblyVariance";
    public const string EmployeePayable = "EmployeePayable";
    public const string CommissionExpense = "CommissionExpense";
    public const string CommissionPayable = "CommissionPayable";
    public const string BadDebt = "BadDebt";
    public const string WriteOff = "WriteOff";
    public const string RoundingDifferences = "RoundingDifferences";
    public const string FaCost = "FaCost";
    public const string FaAccDep = "FaAccDep";
    public const string FaAccImpairment = "FaAccImpairment";
    public const string DepreciationExpense = "DepreciationExpense";
    public const string ImpairmentLoss = "ImpairmentLoss";
    public const string RevaluationSurplus = "RevaluationSurplus";
    public const string GainLossOnDisposal = "GainLossOnDisposal";
    public const string FaClearing = "FaClearing";
    public const string AccruedExpenses = "AccruedExpenses";
    public const string Prepayments = "Prepayments";
    public const string DeferredRevenue = "DeferredRevenue";
    public const string IcReceivable = "IcReceivable";
    public const string IcPayable = "IcPayable";
    public const string RetainedEarnings = "RetainedEarnings";
    public const string CurrentYearEarnings = "CurrentYearEarnings";
    public const string OpeningBalanceEquity = "OpeningBalanceEquity";
    public const string Suspense = "Suspense";

    public static readonly IReadOnlyList<string> All =
    [
        AR, AP, Inventory, InventoryInTransit, InventoryConsignedOut, GRNI, LandedCostClearing, Cogs, Revenue, SalesReturns,
        DiscountGiven, DiscountTaken, OutputTax, InputTax, WhtPayable, WhtReceivable, Bank, Cash, PettyCash, ChequesUnderCollection,
        PdcReceivable, PdcPayable, BankCharges, InterestIncome, InterestExpense, FxGainRealized, FxLossRealized, FxUnrealized,
        CustomerDeposits, SupplierAdvances, UnbilledRevenue, InventoryAdjustment, CountVariance, Scrap, InventoryWriteDown,
        PurchasePriceVariance, AssemblyVariance, EmployeePayable, CommissionExpense, CommissionPayable, BadDebt, WriteOff,
        RoundingDifferences, FaCost, FaAccDep, FaAccImpairment, DepreciationExpense, ImpairmentLoss, RevaluationSurplus,
        GainLossOnDisposal, FaClearing, AccruedExpenses, Prepayments, DeferredRevenue, IcReceivable, IcPayable, RetainedEarnings,
        CurrentYearEarnings, OpeningBalanceEquity, Suspense,
    ];

    /// <summary>Roles whose lines carry a subledger reference, and the subledger they reconcile to.</summary>
    public static readonly IReadOnlyDictionary<string, string> ControlSubledgers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AR] = SubledgerTypes.Receivables,
        [AP] = SubledgerTypes.Payables,
        [Inventory] = SubledgerTypes.Inventory,
        [InventoryInTransit] = SubledgerTypes.Inventory,
        [InventoryConsignedOut] = SubledgerTypes.Inventory,
        [GRNI] = SubledgerTypes.GoodsReceivedNotInvoiced,
        [LandedCostClearing] = SubledgerTypes.GoodsReceivedNotInvoiced,
        [Bank] = SubledgerTypes.Bank,
        [Cash] = SubledgerTypes.Bank,
        [PettyCash] = SubledgerTypes.Bank,
        [ChequesUnderCollection] = SubledgerTypes.PostDatedCheques,
        [PdcReceivable] = SubledgerTypes.PostDatedCheques,
        [PdcPayable] = SubledgerTypes.PostDatedCheques,
        [FaCost] = SubledgerTypes.FixedAssets,
        [FaAccDep] = SubledgerTypes.FixedAssets,
        [FaAccImpairment] = SubledgerTypes.FixedAssets,
        [IcReceivable] = SubledgerTypes.Intercompany,
        [IcPayable] = SubledgerTypes.Intercompany,
    };
}

public sealed record AccountInfo(
    Guid Id,
    Guid ChartId,
    string Code,
    LocalizedText Name,
    string Type,
    string Subtype,
    bool IsHeader,
    bool IsControl,
    string? SubledgerType,
    string? CurrencyRestriction,
    bool AllowManualPosting,
    bool RevalueFx,
    string? DefaultRole,
    Guid? CompanyId,
    bool IsActive);

/// <summary>A line the chart accepts: the account and the dimension values with the account's defaults applied.</summary>
public sealed record LineCheck(AccountInfo Account, IReadOnlyDictionary<string, Guid> Dimensions);

/// <summary>What the posting engine and manual journals ask the chart before a line is written.</summary>
public interface IChartOfAccounts
{
    Task<Guid?> ChartForCompanyAsync(CompanyId companyId, CancellationToken cancellationToken = default);

    Task<AccountInfo?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task<AccountInfo?> FindAccountByCodeAsync(Guid chartId, string code, CancellationToken cancellationToken = default);

    /// <summary>Accounts flagged as the chart's default for a role (the seed of a posting profile).</summary>
    Task<IReadOnlyList<AccountInfo>> AccountsForRoleAsync(Guid chartId, string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses a line the account cannot take (inactive, header, other company, other chart, restricted currency,
    /// manual posting blocked, a required dimension missing or a blocked one present) and fills dimension defaults.
    /// </summary>
    Task<Result<LineCheck>> CheckLineAsync(Guid accountId, CompanyId companyId, IReadOnlyDictionary<string, Guid>? dimensions, string? currency, bool manual, CancellationToken cancellationToken = default);
}

using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Quantities;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Organization.Contracts;

/// <summary>What other modules need to know about a company to post, price and print.</summary>
public sealed record CompanyInfo(
    CompanyId Id,
    string Code,
    LocalizedText LegalName,
    string Country,
    Currency FunctionalCurrency,
    Currency? ReportingCurrency,
    string TimeZone,
    string DefaultLanguage,
    RoundingMode RoundingMode,
    string CostingMethod,
    string CostingScope,
    string TaxRoundingMode,
    string NegativeStockPolicy,
    string BankRevaluationMode,
    Guid FiscalCalendarId,
    Guid BusinessCalendarId,
    bool IsActive,
    Guid? ChartId = null,
    Guid? PostingProfileId = null);

public sealed record BranchInfo(BranchId Id, CompanyId CompanyId, string Code, LocalizedText Name, Guid DimensionValueId, bool IsActive);

public interface ICompanyDirectory
{
    Task<CompanyInfo?> FindAsync(CompanyId id, CancellationToken cancellationToken = default);

    /// <summary>Sets the chart of accounts a company posts to (accounting validates the chart first); null detaches it.</summary>
    Task<Result> AssignChartAsync(CompanyId id, Guid? chartId, CancellationToken cancellationToken = default);

    /// <summary>Sets the company's current posting profile (accounting activates the profile first).</summary>
    Task<Result> AssignPostingProfileAsync(CompanyId id, Guid? profileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanyInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<BranchInfo?> FindBranchAsync(BranchId id, CancellationToken cancellationToken = default);

    /// <summary>ISO 4217 reference: the currency with its minor units, or null for an unknown code.</summary>
    Task<Currency?> FindCurrencyAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>The resolved rate and how it was derived: <c>identity</c>, <c>direct</c>, <c>inverse</c> or <c>cross</c> (through the functional currency).</summary>
public sealed record ResolvedRate(ExchangeRate Rate, string Method, DateOnly EffectiveFrom, IReadOnlyList<Guid> RateIds);

public interface IExchangeRateResolver
{
    /// <summary>
    /// Rate for the date and type such that 1 <paramref name="fromCurrency"/> = rate <paramref name="toCurrency"/>
    /// (ADR-0017): the latest rate whose valid_from is on or before the date; a direct rate wins over its inverse, and
    /// both win over a cross rate through the company's functional currency.
    /// </summary>
    Task<Result<ResolvedRate>> ResolveAsync(CompanyId companyId, string fromCurrency, string toCurrency, DateOnly date, string rateType = RateTypes.Spot, CancellationToken cancellationToken = default);
}

public static class RateTypes
{
    public const string Spot = "spot";
    public const string Closing = "closing";
    public const string Average = "average";
    public const string Budget = "budget";
}

/// <summary>Modules that carry a period state of their own (ADR-0026).</summary>
public static class PostingModules
{
    public const string GeneralLedger = "GL";
    public const string Receivables = "AR";
    public const string Payables = "AP";
    public const string Inventory = "INV";
    public const string FixedAssets = "FA";
    public const string Bank = "BANK";
    public const string Tax = "TAX";

    public static readonly IReadOnlyList<string> All = [GeneralLedger, Receivables, Payables, Inventory, FixedAssets, Bank, Tax];

    public static bool IsValid(string module) => All.Contains(module, StringComparer.Ordinal);
}

public static class PeriodStates
{
    public const string Open = "open";
    public const string SoftClosed = "soft_closed";
    public const string HardClosed = "hard_closed";
    public const string NeverOpened = "never_opened";
}

public sealed record PeriodInfo(Guid PeriodId, Guid FiscalYearId, string FiscalYearCode, int Number, DateOnly StartsOn, DateOnly EndsOn, bool IsAdjustment, string YearStatus);

/// <summary>The period a posting date falls in for a company and the state of one module in it.</summary>
public sealed record PeriodState(PeriodInfo Period, string Module, string State)
{
    public bool AllowsPosting(bool mayPostInSoftClosed) => State == PeriodStates.Open || (State == PeriodStates.SoftClosed && mayPostInSoftClosed);
}

public interface IFiscalPeriodResolver
{
    /// <summary>The first period of the company on or after the date whose state for the module allows posting (open), or an error when none does.</summary>
    Task<Result<PeriodInfo>> FirstOpenPeriodAsync(CompanyId companyId, DateOnly fromDate, string module, CancellationToken cancellationToken = default);

    /// <summary>Period for (company, posting date, module) with its effective state; fails with <c>period.no_fiscal_year</c> when no year covers the date.</summary>
    Task<Result<PeriodState>> ResolveAsync(CompanyId companyId, DateOnly postingDate, string module, CancellationToken cancellationToken = default);
}

/// <summary>Working-day arithmetic on a company's business calendar (A-005): due dates, escalations, schedules.</summary>
public interface IWorkingDayCalendar
{
    Task<bool> IsWorkingDayAsync(CompanyId companyId, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The date itself when it is a working day, otherwise the next working day after it.</summary>
    Task<DateOnly> NextWorkingDayAsync(CompanyId companyId, DateOnly date, CancellationToken cancellationToken = default);

    Task<DateOnly> AddWorkingDaysAsync(CompanyId companyId, DateOnly date, int workingDays, CancellationToken cancellationToken = default);

    /// <summary>Calendar days after <paramref name="from"/>, moved forward to the next working day when it lands on a weekend or holiday.</summary>
    Task<DateOnly> DueDateAsync(CompanyId companyId, DateOnly from, int calendarDays, CancellationToken cancellationToken = default);
}

/// <summary>Deduplicated dimension combinations: journal lines, balances and budgets reference one id per combination.</summary>
public interface IDimensionSets
{
    /// <summary>Validates every value against its dimension and returns the set id, creating the set when it is new.</summary>
    Task<Result<Guid>> GetOrCreateAsync(IReadOnlyDictionary<string, Guid> valuesByDimensionCode, CancellationToken cancellationToken = default);

    /// <summary>The values of a set by dimension code, or null when the set does not exist in this tenant.</summary>
    Task<IReadOnlyDictionary<string, Guid>?> GetAsync(Guid setId, CancellationToken cancellationToken = default);
}

/// <summary>Reads tenant and company settings (a company value overrides the tenant value for the same key).</summary>
public interface ICompanySettings
{
    Task<System.Text.Json.JsonElement?> GetAsync(Guid? companyId, string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// A company's "allow posting from/to" dates (ADR-0026): for everyone (<see cref="RoleId"/> null) or for one role.
/// Either bound may be open.
/// </summary>
public sealed record PostingWindowInfo(Guid CompanyId, Guid? RoleId, DateOnly? AllowFrom, DateOnly? AllowTo)
{
    public bool Allows(DateOnly date) => (AllowFrom is null || date >= AllowFrom) && (AllowTo is null || date <= AllowTo);
}

public interface IPostingWindows
{
    /// <summary>
    /// The window that applies to an actor holding the roles: the widest of the role windows when any role has one,
    /// else the company's window for everyone, else null (no restriction beyond the period states).
    /// </summary>
    Task<PostingWindowInfo?> EffectiveAsync(CompanyId companyId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default);
}

public sealed record DimensionInfo(Guid Id, string Code, LocalizedText Name, bool IsActive);

public sealed record DimensionValueInfo(Guid Id, Guid DimensionId, string Code, LocalizedText Name, bool IsActive);

/// <summary>Read access to the dimension master for modules that attach rules to dimensions (account dimension rules, budgets).</summary>
public interface IDimensionDirectory
{
    Task<IReadOnlyList<DimensionInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<DimensionValueInfo?> FindValueAsync(Guid valueId, CancellationToken cancellationToken = default);
}

public sealed record UomInfo(Guid Id, string Code, LocalizedText Name, string Family, int Precision, bool IsActive);

/// <summary>Read access to the tenant's units of measure for the modules that measure things in them (items, stock, documents).</summary>
public interface IUomDirectory
{
    Task<IReadOnlyList<UomInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<UomInfo?> FindAsync(Guid uomId, CancellationToken cancellationToken = default);

    Task<UomInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);
}

public interface IUomConversions
{
    /// <summary>A conversion from one unit to another: direct, inverse, or through one intermediate unit of the same family.</summary>
    Task<Result<UomConversion>> ResolveAsync(Guid fromUomId, Guid toUomId, CancellationToken cancellationToken = default);
}

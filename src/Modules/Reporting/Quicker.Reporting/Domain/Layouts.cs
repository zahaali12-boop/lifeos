using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Reporting.Domain;

/// <summary>A financial statement layout of the tenant (DOMAIN_MODEL §21, ADR-0034 §5): rows by columns over the journal facts.</summary>
public sealed class FinancialLayout : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Kind { get; set; } = LayoutKinds.Management;

    public LocalizedText Name { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    /// <summary>The shipped layout this one was copied from, when it was.</summary>
    public string? TemplateCode { get; set; }

    public bool IsPublished { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<LayoutRow> Rows { get; } = [];

    public List<LayoutColumn> Columns { get; } = [];
}

public sealed class LayoutRow : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LayoutId { get; set; }

    public int SortOrder { get; set; }

    public string Code { get; set; } = string.Empty;

    public string RowType { get; set; } = RowTypes.Accounts;

    public LocalizedText Label { get; set; } = new();

    /// <summary>JSON of the shape of <c>AccountFilter</c>.</summary>
    public string AccountFilter { get; set; } = "{}";

    /// <summary>JSON: dimension code to value id.</summary>
    public string DimensionFilter { get; set; } = "{}";

    public string? Formula { get; set; }

    public bool FlipSign { get; set; }

    public int Indent { get; set; }

    public bool Bold { get; set; }

    public bool ShowAccounts { get; set; }

    public bool HideWhenZero { get; set; }
}

public sealed class LayoutColumn : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LayoutId { get; set; }

    public int SortOrder { get; set; }

    public string Code { get; set; } = string.Empty;

    public string ColumnType { get; set; } = ColumnTypes.Period;

    public LocalizedText Label { get; set; } = new();

    /// <summary>JSON of the shape of <c>ColumnParameters</c>.</summary>
    public string Parameters { get; set; } = "{}";

    public string CurrencyMode { get; set; } = CurrencyModes.Functional;
}

public static class LayoutKinds
{
    public const string ProfitAndLoss = "pl";
    public const string BalanceSheet = "bs";
    public const string TrialBalance = "tb";
    public const string CashFlow = "cashflow";
    public const string Management = "management";

    public static readonly IReadOnlyList<string> All = [ProfitAndLoss, BalanceSheet, TrialBalance, CashFlow, Management];
}

public static class RowTypes
{
    public const string Header = "header";
    public const string Accounts = "accounts";
    public const string Formula = "formula";
    public const string Blank = "blank";

    public static readonly IReadOnlyList<string> All = [Header, Accounts, Formula, Blank];
}

public static class ColumnTypes
{
    public const string Period = "period";
    public const string YearToDate = "ytd";
    public const string PriorPeriod = "prior_period";
    public const string PriorYear = "prior_year";
    public const string CustomRange = "custom_range";
    public const string Opening = "opening";
    public const string Debits = "debits";
    public const string Credits = "credits";
    public const string Closing = "closing";
    public const string Variance = "variance";
    public const string VariancePct = "variance_pct";

    public static readonly IReadOnlyList<string> All = [Period, YearToDate, PriorPeriod, PriorYear, CustomRange, Opening, Debits, Credits, Closing, Variance, VariancePct];

    /// <summary>Columns computed from two other columns rather than from the facts.</summary>
    public static bool IsDerived(string type) => type is Variance or VariancePct;
}

public static class CurrencyModes
{
    public const string Functional = "functional";
    public const string Reporting = "reporting";

    public static readonly IReadOnlyList<string> All = [Functional, Reporting];
}

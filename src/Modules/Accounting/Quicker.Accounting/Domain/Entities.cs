using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Accounting.Domain;

public sealed class Chart : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string? TemplateCode { get; set; }

    /// <summary>Pattern for account codes: '#' digit, 'A' letter, '?' letter or digit, other characters literal; empty = free.</summary>
    public string AccountCodeFormat { get; set; } = string.Empty;

    /// <summary>Null: any company of the tenant may use the chart.</summary>
    public Guid? CompanyId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AccountCategory : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    /// <summary>bs (statement of financial position), pl (profit or loss), ocf (other comprehensive income / equity movements).</summary>
    public string Statement { get; set; } = "bs";

    public int SortOrder { get; set; }

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Account : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ChartId { get; set; }

    public Guid? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Type { get; set; } = string.Empty;

    public string Subtype { get; set; } = string.Empty;

    public Guid? CategoryId { get; set; }

    public bool IsHeader { get; set; }

    public bool IsControl { get; set; }

    public string? SubledgerType { get; set; }

    public string? CurrencyRestriction { get; set; }

    public bool AllowManualPosting { get; set; } = true;

    public bool RevalueFx { get; set; }

    public string? CashFlowCategory { get; set; }

    public string? DefaultRole { get; set; }

    /// <summary>Null: every company on the chart may post to the account.</summary>
    public Guid? CompanyId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AccountDimensionRule> DimensionRules { get; } = [];
}

public sealed class AccountDimensionRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid AccountId { get; set; }

    public Guid DimensionId { get; set; }

    public string Rule { get; set; } = "optional";

    public Guid? DefaultValueId { get; set; }
}

public sealed class AccountMapping : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid AccountId { get; set; }

    public string StatutoryChartCode { get; set; } = string.Empty;

    public string StatutoryCode { get; set; } = string.Empty;
}

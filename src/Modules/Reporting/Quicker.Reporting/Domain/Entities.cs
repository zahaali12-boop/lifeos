using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Reporting.Domain;

/// <summary>A saved report (DOMAIN_MODEL §21): a query definition under a name, its kind and chart, its owner and the shares that show it to others.</summary>
public sealed class Report : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? CompanyId { get; set; }

    public string Model { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    public Guid OwnerMembershipId { get; set; }

    /// <summary>The query definition as JSON (the shape of <c>QueryDefinition</c>).</summary>
    public string Definition { get; set; } = "{}";

    public string Kind { get; set; } = ReportKinds.Pivot;

    /// <summary>Chart options as JSON (the shape of <c>ChartOptions</c>).</summary>
    public string Chart { get; set; } = "{}";

    public bool IsTemplate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ReportShare> Shares { get; } = [];
}

public sealed class ReportShare : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ReportId { get; set; }

    public string ShareWith { get; set; } = ShareTargets.Tenant;

    public Guid? TargetId { get; set; }

    public string Access { get; set; } = ShareAccess.View;

    public DateTimeOffset CreatedAt { get; set; }
}

public static class ReportKinds
{
    public const string Table = "table";
    public const string Pivot = "pivot";
    public const string Chart = "chart";

    public static readonly IReadOnlyList<string> All = [Table, Pivot, Chart];
}

public static class ShareTargets
{
    public const string Tenant = "tenant";
    public const string Role = "role";
    public const string Membership = "membership";

    public static readonly IReadOnlyList<string> All = [Tenant, Role, Membership];
}

public static class ShareAccess
{
    public const string View = "view";
    public const string Edit = "edit";

    public static readonly IReadOnlyList<string> All = [View, Edit];
}

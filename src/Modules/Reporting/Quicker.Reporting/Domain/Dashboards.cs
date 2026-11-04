using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Reporting.Domain;

/// <summary>A dashboard (DOMAIN_MODEL §21): widgets over saved reports and statement layouts, run as whoever opens it; private to its owner or shared with the workspace.</summary>
public sealed class Dashboard : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? CompanyId { get; set; }

    public LocalizedText Name { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    public Guid OwnerMembershipId { get; set; }

    public string Sharing { get; set; } = DashboardSharing.Private;

    public bool ShowOnHome { get; set; }

    public bool IsTemplate { get; set; }

    public string DefaultPeriod { get; set; } = RelativePeriods.ThisMonth;

    public int? RefreshSeconds { get; set; }

    /// <summary>Free layout hints as JSON (reserved; the widgets' order and widths are columns).</summary>
    public string Layout { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<DashboardWidget> Widgets { get; } = [];
}

public sealed class DashboardWidget : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid DashboardId { get; set; }

    public int SortOrder { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Title { get; set; } = new();

    public string Source { get; set; } = WidgetSources.Report;

    public Guid? ReportId { get; set; }

    public Guid? LayoutId { get; set; }

    public string? LayoutCode { get; set; }

    public string Viz { get; set; } = WidgetViz.Bar;

    /// <summary>A relative period of its own, or <c>dashboard</c> to follow the dashboard's.</summary>
    public string Period { get; set; } = RelativePeriods.Dashboard;

    public int Width { get; set; } = 2;

    /// <summary>Widget options as JSON (the shape of <c>WidgetOptions</c>).</summary>
    public string Options { get; set; } = "{}";
}

public static class DashboardSharing
{
    public const string Private = "private";
    public const string Tenant = "tenant";

    public static readonly IReadOnlyList<string> All = [Private, Tenant];
}

public static class WidgetSources
{
    public const string Report = "report";
    public const string Statement = "statement";

    public static readonly IReadOnlyList<string> All = [Report, Statement];
}

public static class WidgetViz
{
    public const string Kpi = "kpi";
    public const string Bar = "bar";
    public const string Line = "line";
    public const string Area = "area";
    public const string Table = "table";

    public static readonly IReadOnlyList<string> All = [Kpi, Bar, Line, Area, Table];
}

/// <summary>The relative periods a widget or a schedule resolves at run time, in the company's calendar and time zone (A-185).</summary>
public static class RelativePeriods
{
    public const string Dashboard = "dashboard";
    public const string Today = "today";
    public const string Yesterday = "yesterday";
    public const string ThisWeek = "this_week";
    public const string LastWeek = "last_week";
    public const string ThisMonth = "this_month";
    public const string LastMonth = "last_month";
    public const string ThisQuarter = "this_quarter";
    public const string LastQuarter = "last_quarter";
    public const string ThisYear = "this_year";
    public const string LastYear = "last_year";
    public const string Last7Days = "last_7_days";
    public const string Last30Days = "last_30_days";
    public const string YearToDate = "year_to_date";
    public const string Custom = "custom";

    /// <summary>The periods a dashboard or a schedule may name (a widget may also say <see cref="Dashboard"/>).</summary>
    public static readonly IReadOnlyList<string> All = [Today, Yesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisQuarter, LastQuarter, ThisYear, LastYear, Last7Days, Last30Days, YearToDate, Custom];

    /// <summary>The window a period names on a day; weeks start on Monday; <see cref="Custom"/> and <see cref="Dashboard"/> resolve to nothing here.</summary>
    public static (DateOnly From, DateOnly To)? Resolve(string period, DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
        var yearStart = new DateOnly(today.Year, 1, 1);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return period switch
        {
            Today => (today, today),
            Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            ThisWeek => (weekStart, weekStart.AddDays(6)),
            LastWeek => (weekStart.AddDays(-7), weekStart.AddDays(-1)),
            ThisMonth => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
            LastMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            ThisQuarter => (quarterStart, quarterStart.AddMonths(3).AddDays(-1)),
            LastQuarter => (quarterStart.AddMonths(-3), quarterStart.AddDays(-1)),
            ThisYear => (yearStart, yearStart.AddYears(1).AddDays(-1)),
            LastYear => (yearStart.AddYears(-1), yearStart.AddDays(-1)),
            Last7Days => (today.AddDays(-6), today),
            Last30Days => (today.AddDays(-29), today),
            YearToDate => (yearStart, today),
            _ => null,
        };
    }
}

using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;

namespace Quicker.Reporting.Application.Dashboards;

/// <summary>How a widget reads its source: the chart options of a chart widget, the measure of a KPI over a report, the row and column of a KPI over a statement, the closing-entry choice of a statement.</summary>
public sealed record WidgetOptions(
    ChartOptions? Chart = null,
    string? Measure = null,
    string? RowCode = null,
    string? ColumnCode = null,
    bool? IncludeClosing = null,
    bool ShowComparison = false);

/// <summary>A widget as saved: its source (a saved report by id, or a statement layout by id or shipped code), how it is drawn, its period and width (of four columns).</summary>
public sealed record WidgetDefinition(
    string Source,
    string Viz,
    IReadOnlyDictionary<string, string>? Title = null,
    string? Code = null,
    Guid? ReportId = null,
    Guid? LayoutId = null,
    string? LayoutCode = null,
    string Period = "dashboard",
    int Width = 2,
    WidgetOptions? Options = null);

public sealed record SaveDashboardRequest(
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyList<WidgetDefinition> Widgets,
    IReadOnlyDictionary<string, string>? Description = null,
    Guid? CompanyId = null,
    string Sharing = "private",
    bool ShowOnHome = false,
    string DefaultPeriod = "this_month",
    int? RefreshSeconds = null);

public sealed record DashboardWidgetSummary(
    Guid Id,
    string Code,
    string Source,
    string Viz,
    IReadOnlyDictionary<string, string> Title,
    Guid? ReportId,
    Guid? LayoutId,
    string? LayoutCode,
    string Period,
    int Width,
    WidgetOptions Options);

public sealed record DashboardSummary(
    Guid Id,
    Guid? CompanyId,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    Guid OwnerMembershipId,
    bool IsOwner,
    bool CanEdit,
    string Sharing,
    bool ShowOnHome,
    string DefaultPeriod,
    int? RefreshSeconds,
    IReadOnlyList<DashboardWidgetSummary> Widgets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Runs a dashboard for a company over a period: the dashboard's default period, another named one, or fixed dates (<c>custom</c>).</summary>
public sealed record RunDashboardRequest(Guid CompanyId, string? Period = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>A KPI: one figure, the comparable figure of the prior window when the source carries one, and how to format it.</summary>
public sealed record WidgetKpi(decimal? Value, decimal? Prior, string Type, string Currency, IReadOnlyDictionary<string, string> Label);

public sealed record WidgetResult(
    Guid WidgetId,
    string Code,
    string Source,
    string Viz,
    IReadOnlyDictionary<string, string> Title,
    int Width,
    DateOnly From,
    DateOnly To,
    QueryResult? Query,
    StatementResult? Statement,
    WidgetKpi? Kpi,
    ChartOptions? Chart,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record DashboardResult(Guid DashboardId, Guid CompanyId, string Period, DateOnly From, DateOnly To, IReadOnlyList<WidgetResult> Widgets, int DurationMs);

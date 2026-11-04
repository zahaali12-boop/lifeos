using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;

namespace Quicker.Reporting.Application.Dashboards;

/// <summary>
/// Dashboards (founder programme item 9, slice 9d; ADR-0034 §4): widgets over saved reports and statement layouts,
/// each run as whoever opens the dashboard, with their scopes, so a shared dashboard never widens access. A KPI is one
/// figure of its source (a measure summed over the result, or a cell of a statement) with the prior window's figure
/// when the source compares; a chart or a table shows the result as the builder would; a statement widget shows the
/// statement's rows. Periods are relative and resolved on the company's calendar at run time (A-185).
/// </summary>
public sealed class DashboardService(ReportingDbContext db, ReportService reports, LayoutService layouts, StatementService statements, ICompanyDirectory companies, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public const string AuditEntity = "dashboard";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(Guid? companyId, bool homeOnly, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var mine = who.MembershipId.Value;
        var query = db.Dashboards.AsNoTracking().Include(static d => d.Widgets)
            .Where(d => d.OwnerMembershipId == mine || d.Sharing == DashboardSharing.Tenant);
        if (companyId is { } company)
        {
            query = query.Where(d => d.CompanyId == null || d.CompanyId == company);
        }

        if (homeOnly)
        {
            query = query.Where(static d => d.ShowOnHome);
        }

        var list = await query.OrderBy(static d => d.CreatedAt).ToListAsync(cancellationToken);
        return list.Select(d => Map(d, who)).ToList();
    }

    public async Task<Result<DashboardSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var dashboard = await db.Dashboards.AsNoTracking().Include(static d => d.Widgets).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return dashboard is null || !CanSee(dashboard, who) ? Error.NotFound(AuditEntity, id) : Map(dashboard, who);
    }

    public async Task<Result<DashboardSummary>> SaveAsync(Guid? id, SaveDashboardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var who = principal.Required;
        var name = new LocalizedText(request.Name);
        if (string.IsNullOrWhiteSpace(name.Resolve("en", "ar")))
        {
            return Error.Validation("dashboard.name_required", "A dashboard needs a name.");
        }

        if (!DashboardSharing.All.Contains(request.Sharing, StringComparer.Ordinal))
        {
            return Error.Validation("dashboard.sharing_invalid", "A dashboard is private or shared with the workspace.").WithWhy(("sharing", request.Sharing));
        }

        if (!RelativePeriods.All.Contains(request.DefaultPeriod, StringComparer.Ordinal) || request.DefaultPeriod == RelativePeriods.Custom)
        {
            return Error.Validation("dashboard.period_invalid", "The default period is a named period such as this_month.").WithWhy(("period", request.DefaultPeriod), ("known", RelativePeriods.All));
        }

        if (request.RefreshSeconds is { } refresh && refresh is < 30 or > 86_400)
        {
            return Error.Validation("dashboard.refresh_invalid", "A dashboard refreshes every 30 seconds to every day.");
        }

        if (request.CompanyId is { } companyId && await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var widgets = new List<DashboardWidget>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        foreach (var definition in request.Widgets)
        {
            var checkedWidget = await ValidateWidgetAsync(definition, order, codes, cancellationToken);
            if (checkedWidget.IsFailure)
            {
                return checkedWidget.Error!;
            }

            widgets.Add(checkedWidget.Value);
            order++;
        }

        Dashboard dashboard;
        object? before = null;
        if (id is { } existing)
        {
            var found = await db.Dashboards.Include(static d => d.Widgets).SingleOrDefaultAsync(d => d.Id == existing, cancellationToken);
            if (found is null || !CanSee(found, who))
            {
                return Error.NotFound(AuditEntity, existing);
            }

            if (!CanEdit(found, who))
            {
                return Error.Forbidden("dashboard.not_owner", "Only the owner changes a dashboard.");
            }

            before = Snapshot(found);
            dashboard = found;
            db.Widgets.RemoveRange(dashboard.Widgets);
            dashboard.Widgets.Clear();
        }
        else
        {
            dashboard = new Dashboard { Id = Guid.CreateVersion7(), OwnerMembershipId = who.MembershipId.Value, CreatedAt = clock.UtcNow };
            db.Dashboards.Add(dashboard);
        }

        dashboard.Name = name;
        dashboard.Description = new LocalizedText(request.Description ?? new Dictionary<string, string>(StringComparer.Ordinal));
        dashboard.CompanyId = request.CompanyId;
        dashboard.Sharing = request.Sharing;
        dashboard.ShowOnHome = request.ShowOnHome;
        dashboard.DefaultPeriod = request.DefaultPeriod;
        dashboard.RefreshSeconds = request.RefreshSeconds;
        dashboard.UpdatedAt = clock.UtcNow;
        foreach (var widget in widgets)
        {
            widget.DashboardId = dashboard.Id;
            dashboard.Widgets.Add(widget);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, dashboard.Id, dashboard.Name.Resolve("en", "ar"), id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(dashboard), CompanyId: dashboard.CompanyId), cancellationToken);
        return Map(dashboard, who);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var dashboard = await db.Dashboards.Include(static d => d.Widgets).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null || !CanSee(dashboard, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (!CanEdit(dashboard, who))
        {
            return Error.Forbidden("dashboard.not_owner", "Only the owner deletes a dashboard.");
        }

        db.Dashboards.Remove(dashboard);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, dashboard.Id, dashboard.Name.Resolve("en", "ar"), AuditActions.Deleted, Before: Snapshot(dashboard), CompanyId: dashboard.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Runs every widget as the caller; a widget that fails (a report deleted, a permission gone) reports its error in place while the others show.</summary>
    public async Task<Result<DashboardResult>> RunAsync(Guid id, RunDashboardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var who = principal.Required;
        var dashboard = await db.Dashboards.AsNoTracking().Include(static d => d.Widgets).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (dashboard is null || !CanSee(dashboard, who))
        {
            return Error.NotFound(AuditEntity, id);
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var today = clock.TodayIn(company.TimeZone);
        var period = request.Period ?? dashboard.DefaultPeriod;
        var window = ResolveWindow(period, request.From, request.To, today);
        if (window.IsFailure)
        {
            return window.Error!;
        }

        var watch = Stopwatch.StartNew();
        var results = new List<WidgetResult>();
        foreach (var widget in dashboard.Widgets.OrderBy(static w => w.SortOrder))
        {
            var own = widget.Period == RelativePeriods.Dashboard ? window.Value : RelativePeriods.Resolve(widget.Period, today) ?? window.Value;
            results.Add(await RunWidgetAsync(widget, company.Id.Value, own.From, own.To, cancellationToken));
        }

        return new DashboardResult(dashboard.Id, company.Id.Value, period, window.Value.From, window.Value.To, results, (int)watch.ElapsedMilliseconds);
    }

    /// <summary>The window a request names: fixed dates for <c>custom</c>, else the named period on the company's calendar.</summary>
    public static Result<(DateOnly From, DateOnly To)> ResolveWindow(string period, DateOnly? from, DateOnly? to, DateOnly today)
    {
        if (period == RelativePeriods.Custom)
        {
            if (from is null || to is null)
            {
                return Error.Validation("dashboard.range_required", "A custom period needs both dates.");
            }

            return from > to ? Error.Validation("dashboard.range_invalid", "The period starts after it ends.").WithWhy(("from", from), ("to", to)) : (from.Value, to.Value);
        }

        return RelativePeriods.Resolve(period, today) is { } window
            ? window
            : Error.Validation("dashboard.period_invalid", $"'{period}' is not a period.").WithWhy(("period", period), ("known", RelativePeriods.All));
    }

    private async Task<WidgetResult> RunWidgetAsync(DashboardWidget widget, Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var options = Options(widget);
        WidgetResult Failed(Error error) => new(widget.Id, widget.Code, widget.Source, widget.Viz, widget.Title.Values, widget.Width, from, to, null, null, null, options.Chart, error.Code, error.Message);
        if (widget.Source == WidgetSources.Report)
        {
            if (widget.ReportId is not { } reportId)
            {
                return Failed(Error.Validation("dashboard.widget_source_missing", "The widget names no report."));
            }

            var run = await reports.RunAsync(reportId, new RunReportRequest(companyId, from, to), cancellationToken);
            if (run.IsFailure)
            {
                return Failed(run.Error!);
            }

            var kpi = widget.Viz == WidgetViz.Kpi ? Kpi(run.Value, options.Measure) : null;
            return new WidgetResult(widget.Id, widget.Code, widget.Source, widget.Viz, widget.Title.Values, widget.Width, from, to, widget.Viz == WidgetViz.Kpi ? null : run.Value, null, kpi, options.Chart, null, null);
        }

        var statement = await statements.RunAsync(new StatementRequest(companyId, widget.LayoutId, widget.LayoutCode, from, to, null, options.IncludeClosing), cancellationToken);
        if (statement.IsFailure)
        {
            return Failed(statement.Error!);
        }

        if (widget.Viz == WidgetViz.Kpi)
        {
            var cell = StatementKpi(statement.Value, options.RowCode, options.ColumnCode);
            return cell.IsFailure
                ? Failed(cell.Error!)
                : new WidgetResult(widget.Id, widget.Code, widget.Source, widget.Viz, widget.Title.Values, widget.Width, from, to, null, null, cell.Value, null, null, null);
        }

        return new WidgetResult(widget.Id, widget.Code, widget.Source, widget.Viz, widget.Title.Values, widget.Width, from, to, null, statement.Value, null, null, null, null);
    }

    /// <summary>A measure summed over the rows (the first measure when none is named), with the prior window's sum when the report compares.</summary>
    private static WidgetKpi Kpi(QueryResult result, string? measure)
    {
        var field = (measure is not null ? result.Fields.FirstOrDefault(f => f.Key == measure && f.Role == FieldRoles.Measure && f.Group is null) : null)
            ?? result.Fields.FirstOrDefault(static f => f.Role == FieldRoles.Measure && f.Group is null);
        if (field is null)
        {
            return new WidgetKpi(null, null, FieldTypes.Number, result.Currency, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var prior = result.Fields.FirstOrDefault(f => f.Key == field.Key + "_prior");
        return new WidgetKpi(Sum(result.Rows, field.Key), prior is null ? null : Sum(result.Rows, prior.Key), field.Type, result.Currency, field.Label);
    }

    private static decimal? Sum(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string key)
    {
        decimal? total = null;
        foreach (var row in rows)
        {
            if (row.TryGetValue(key, out var value) && value is not null)
            {
                var n = value switch
                {
                    decimal d => d,
                    int i => i,
                    long l => l,
                    _ => decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
                };
                total = (total ?? 0m) + n;
            }
        }

        return total;
    }

    private static Result<WidgetKpi> StatementKpi(StatementResult statement, string? rowCode, string? columnCode)
    {
        var row = rowCode is null ? null : statement.Rows.FirstOrDefault(r => string.Equals(r.Code, rowCode, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return Error.Validation("dashboard.statement_row_unknown", "The KPI names a row the statement does not have.").WithWhy(("row", rowCode), ("known", statement.Rows.Select(static r => r.Code).ToList()));
        }

        var index = columnCode is null ? 0 : statement.Columns.ToList().FindIndex(c => string.Equals(c.Code, columnCode, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Error.Validation("dashboard.statement_column_unknown", "The KPI names a column the statement does not have.").WithWhy(("column", columnCode), ("known", statement.Columns.Select(static c => c.Code).ToList()));
        }

        var column = statement.Columns[index];
        var prior = statement.Columns.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.ColumnType is ColumnTypes.PriorPeriod or ColumnTypes.PriorYear);
        var priorValue = prior.c is not null && index != prior.i ? row.Values[prior.i] : null;
        return new WidgetKpi(row.Values[index], priorValue, column.IsPercent ? FieldTypes.Percent : FieldTypes.Money, column.Currency, row.Label);
    }

    private async Task<Result<DashboardWidget>> ValidateWidgetAsync(WidgetDefinition definition, int order, HashSet<string> codes, CancellationToken cancellationToken)
    {
        if (!WidgetSources.All.Contains(definition.Source, StringComparer.Ordinal))
        {
            return Error.Validation("dashboard.widget_source_invalid", "A widget reads a saved report or a statement.").WithWhy(("source", definition.Source));
        }

        if (!WidgetViz.All.Contains(definition.Viz, StringComparer.Ordinal))
        {
            return Error.Validation("dashboard.widget_viz_invalid", "A widget is a KPI, a bar, line or area chart, or a table.").WithWhy(("viz", definition.Viz), ("known", WidgetViz.All));
        }

        if (definition.Width is < 1 or > 4)
        {
            return Error.Validation("dashboard.widget_width_invalid", "A widget spans one to four columns.");
        }

        if (definition.Period != RelativePeriods.Dashboard && (!RelativePeriods.All.Contains(definition.Period, StringComparer.Ordinal) || definition.Period == RelativePeriods.Custom))
        {
            return Error.Validation("dashboard.widget_period_invalid", "A widget follows the dashboard's period or names one of its own.").WithWhy(("period", definition.Period));
        }

        var code = string.IsNullOrWhiteSpace(definition.Code) ? $"W{order + 1}" : definition.Code.Trim().ToUpperInvariant();
        if (!codes.Add(code))
        {
            return Error.Validation("dashboard.widget_code_duplicate", "Two widgets carry the same code.").WithWhy(("code", code));
        }

        var options = definition.Options ?? new WidgetOptions();
        var widget = new DashboardWidget
        {
            Id = Guid.CreateVersion7(),
            SortOrder = order,
            Code = code,
            Source = definition.Source,
            Viz = definition.Viz,
            Period = definition.Period,
            Width = definition.Width,
            Options = JsonSerializer.Serialize(options, Json),
        };

        if (definition.Source == WidgetSources.Report)
        {
            if (definition.ReportId is not { } reportId)
            {
                return Error.Validation("dashboard.widget_report_required", "A report widget names a saved report.");
            }

            var report = await reports.GetAsync(reportId, cancellationToken);
            if (report.IsFailure)
            {
                return report.Error!;
            }

            widget.ReportId = reportId;
            widget.Title = new LocalizedText(definition.Title is { Count: > 0 } ? definition.Title : report.Value.Name);
        }
        else
        {
            if (definition.LayoutId is { } layoutId)
            {
                var layout = await layouts.GetAsync(layoutId, cancellationToken);
                if (layout.IsFailure)
                {
                    return layout.Error!;
                }

                widget.LayoutId = layoutId;
                widget.Title = new LocalizedText(definition.Title is { Count: > 0 } ? definition.Title : layout.Value.Name);
            }
            else if (!string.IsNullOrWhiteSpace(definition.LayoutCode) && ShippedLayouts.Find(definition.LayoutCode) is { } shipped)
            {
                widget.LayoutCode = shipped.Code;
                widget.Title = new LocalizedText(definition.Title is { Count: > 0 } ? definition.Title : shipped.Name);
            }
            else
            {
                return Error.Validation("dashboard.widget_layout_required", "A statement widget names a layout by id or a shipped layout by code.").WithWhy(("layoutCode", definition.LayoutCode));
            }

            if (definition.Viz == WidgetViz.Kpi && string.IsNullOrWhiteSpace(options.RowCode))
            {
                return Error.Validation("dashboard.widget_row_required", "A KPI over a statement names the row it shows.");
            }

            if (definition.Viz is WidgetViz.Bar or WidgetViz.Line or WidgetViz.Area)
            {
                return Error.Validation("dashboard.widget_viz_invalid", "A statement shows as a table or a KPI.").WithWhy(("viz", definition.Viz));
            }
        }

        return widget;
    }

    private static WidgetOptions Options(DashboardWidget widget) =>
        JsonSerializer.Deserialize<WidgetOptions>(widget.Options, Json) ?? new WidgetOptions();

    private static bool CanSee(Dashboard dashboard, Principal who) =>
        dashboard.OwnerMembershipId == who.MembershipId.Value || dashboard.Sharing == DashboardSharing.Tenant;

    private static bool CanEdit(Dashboard dashboard, Principal who) => dashboard.OwnerMembershipId == who.MembershipId.Value;

    private static DashboardSummary Map(Dashboard d, Principal who) =>
        new(d.Id, d.CompanyId, d.Name.Values, d.Description.Values, d.OwnerMembershipId, CanEdit(d, who), CanEdit(d, who), d.Sharing, d.ShowOnHome, d.DefaultPeriod, d.RefreshSeconds,
            d.Widgets.OrderBy(static w => w.SortOrder).Select(static w => new DashboardWidgetSummary(w.Id, w.Code, w.Source, w.Viz, w.Title.Values, w.ReportId, w.LayoutId, w.LayoutCode, w.Period, w.Width, Options(w))).ToList(),
            d.CreatedAt, d.UpdatedAt);

    private static object Snapshot(Dashboard d) => new
    {
        name = d.Name.Values,
        d.CompanyId,
        d.Sharing,
        d.ShowOnHome,
        d.DefaultPeriod,
        widgets = d.Widgets.OrderBy(static w => w.SortOrder).Select(static w => new { w.Code, w.Source, w.Viz, w.ReportId, w.LayoutId, w.LayoutCode, w.Period, w.Width }).ToList(),
    };
}

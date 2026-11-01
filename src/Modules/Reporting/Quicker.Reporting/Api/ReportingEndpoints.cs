using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Reporting.Application;
using Quicker.Reporting.Application.Facts;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Application.Statements;
using Quicker.Web;

namespace Quicker.Reporting.Api;

/// <summary>Reporting under /api/v1/reporting: the home dashboard and the sales lines (founder programme item 1), and the semantic layer (item 9, ADR-0034): the catalogue of models, queries, their explanation and drill, and the projected facts.</summary>
public static class ReportingEndpoints
{
    public static RouteGroupBuilder MapReportingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var reporting = api.MapGroup("/reporting").WithTags("Reporting").RequireAuthorization();
        reporting.MapGet("/home", async (Guid companyId, DateOnly? asOf, HomeDashboardService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, asOf, ct)))
            .WithSummary("The home page's numbers for a company on a day (today in the company's time zone when no date is given): net sales today, this month and the month before with a thirty-day series, receivables and payables due and overdue, cash by account, documents waiting for a step; each section only for those who may read its module.");
        reporting.MapGet("/sales-lines", async (Guid companyId, DateOnly? from, DateOnly? to, string? status, SalesLinesService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, from, to, status, ct)))
            .WithSummary("Every line of the company's sales invoices and credit notes in the range (posting dates; the year to date when no range is given; posted ones unless status names others, comma-separated) with the attributes a grid groups by; capped at 20,000 lines.");

        reporting.MapGet("/models", async (QueryService service, CancellationToken ct) => TypedResults.Ok(await service.CatalogueAsync(ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The semantic models the reader may query, each with its fields (dimensions and measures, bilingual labels, folders) including the tenant's own dimensions.");
        reporting.MapPost("/query", async (QueryDefinition definition, QueryService service, CancellationToken ct) => ApiProblems.Ok(await service.RunAsync(definition, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("Runs a query: dimensions on rows and columns, measures, filters, sort, top-N, calculated fields and a prior-period or prior-year comparison; compiled with the reader's scopes over facts refreshed within the minute.");
        reporting.MapPost("/query/explain", async (QueryDefinition definition, QueryService service, CancellationToken ct) => ApiProblems.Ok(await service.ExplainAsync(definition, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The statement a query compiles to, with its parameters and with the values written in, and the scope that applies; nothing runs.");
        reporting.MapPost("/query/drill", async (DrillRequest request, QueryService service, CancellationToken ct) => ApiProblems.Ok(await service.DrillAsync(request, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The lines behind a cell: the query's filters plus the cell's dimension keys, at line grain, each with the document it came from.");

        var projections = reporting.MapGroup("/projections");
        projections.MapGet("/", async (ProjectionService service, CancellationToken ct) => TypedResults.Ok(await service.StatusAsync(ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The projected facts of the tenant: how far each is projected, how many rows it holds, when it last ran.");
        projections.MapPost("/refresh", async (ProjectionService service, CancellationToken ct) => TypedResults.Ok(await service.RefreshAsync(ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("Projects what was posted since each fact's watermark, now.");
        projections.MapPost("/rebuild", async (ProjectionService service, CancellationToken ct) => TypedResults.Ok(await service.RebuildAsync(ct)))
            .RequirePermission(ReportingPermissions.ProjectionManage)
            .WithSummary("Empties the tenant's facts and projects them again from the books; the result equals what the increments built.");

        // Saved reports (slice 9b): a definition under a name, shared with the tenant, a role or a member; run as the caller.
        var reports = reporting.MapGroup("/reports");
        reports.MapGet("/", async (Guid? companyId, ReportService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The saved reports the caller owns or that are shared with them, optionally those for one company.");
        reports.MapGet("/{reportId:guid}", async (Guid reportId, ReportService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(reportId, ct)))
            .RequirePermission(ReportingPermissions.ReportRun);
        reports.MapPost("/", async (SaveReportRequest request, ReportService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static r => $"/api/v1/reporting/reports/{r.Id}"))
            .RequirePermission(ReportingPermissions.ReportManage)
            .WithSummary("Saves a report: the definition is compiled for the author first, so a broken one is refused with the field at fault.");
        reports.MapPut("/{reportId:guid}", async (Guid reportId, SaveReportRequest request, ReportService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(reportId, request, ct)))
            .RequirePermission(ReportingPermissions.ReportManage);
        reports.MapPut("/{reportId:guid}/shares", async (Guid reportId, IReadOnlyList<ShareRequest> shares, ReportService service, CancellationToken ct) => ApiProblems.Ok(await service.ShareAsync(reportId, shares, ct)))
            .RequirePermission(ReportingPermissions.ReportManage)
            .WithSummary("Replaces who the report is shared with; only its owner may.");
        reports.MapDelete("/{reportId:guid}", async (Guid reportId, ReportService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(reportId, ct)))
            .RequirePermission(ReportingPermissions.ReportManage);
        reports.MapPost("/{reportId:guid}/run", async (Guid reportId, RunReportRequest? request, ReportService service, CancellationToken ct) => ApiProblems.Ok(await service.RunAsync(reportId, request, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("Runs the saved definition as the caller, with the company or the range changed when asked; the caller's own scopes apply.");

        // Financial statements (slice 9c): layouts of rows and columns over the journal facts, the shipped ones and the tenant's.
        var statements = reporting.MapGroup("/statements");
        statements.MapGet("/layouts", async (Guid? companyId, LayoutService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The shipped layouts and the tenant's, optionally those for one company; unpublished ones only for designers.");
        statements.MapGet("/layouts/options", async (Guid companyId, LayoutService service, CancellationToken ct) => ApiProblems.Ok(await service.OptionsAsync(companyId, ct)))
            .RequirePermission(ReportingPermissions.StatementManage)
            .WithSummary("What a designer may pick from for the company's chart: account types, subtypes, categories and posting roles.");
        statements.MapGet("/layouts/{layoutId:guid}", async (Guid layoutId, LayoutService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(layoutId, ct)))
            .RequirePermission(ReportingPermissions.ReportRun);
        statements.MapPost("/layouts", async (SaveLayoutRequest request, LayoutService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static l => $"/api/v1/reporting/statements/layouts/{l.Id}"))
            .RequirePermission(ReportingPermissions.StatementManage)
            .WithSummary("Saves a layout after checking its rows (codes, types, formulas over the other rows) and columns (types, variances over other columns, ranges).");
        statements.MapPut("/layouts/{layoutId:guid}", async (Guid layoutId, SaveLayoutRequest request, LayoutService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(layoutId, request, ct)))
            .RequirePermission(ReportingPermissions.StatementManage);
        statements.MapDelete("/layouts/{layoutId:guid}", async (Guid layoutId, LayoutService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(layoutId, ct)))
            .RequirePermission(ReportingPermissions.StatementManage);
        statements.MapPost("/run", async (StatementRequest request, StatementService service, CancellationToken ct) => ApiProblems.Ok(await service.RunAsync(request, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("Computes a statement for a company and a period from a layout (a tenant's by id or a shipped one by code) over the journal facts, with the caller's scopes.");
        statements.MapPost("/preview", async (StatementDesignPreview request, StatementService service, CancellationToken ct) => ApiProblems.Ok(await service.PreviewAsync(request, ct)))
            .RequirePermission(ReportingPermissions.StatementManage)
            .WithSummary("Computes a statement from a definition that is not saved yet, checked as a save would check it.");
        statements.MapPost("/drill", async (StatementDrillRequest request, StatementService service, CancellationToken ct) => ApiProblems.Ok(await service.DrillAsync(request, ct)))
            .RequirePermission(ReportingPermissions.ReportRun)
            .WithSummary("The accounts behind one cell of a statement, with the column's window so each opens in the ledger.");

        return api;
    }
}

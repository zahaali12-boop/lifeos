using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Reporting.Application;
using Quicker.Reporting.Application.Facts;
using Quicker.Reporting.Application.Semantic;
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

        return api;
    }
}

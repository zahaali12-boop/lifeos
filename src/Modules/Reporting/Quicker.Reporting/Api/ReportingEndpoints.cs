using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Reporting.Application;
using Quicker.Web;

namespace Quicker.Reporting.Api;

/// <summary>Read models under /api/v1/reporting: the home dashboard (founder programme item 1); the semantic layer and reports follow in M7.</summary>
public static class ReportingEndpoints
{
    public static RouteGroupBuilder MapReportingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var reporting = api.MapGroup("/reporting").WithTags("Reporting").RequireAuthorization();
        reporting.MapGet("/home", async (Guid companyId, DateOnly? asOf, HomeDashboardService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, asOf, ct)))
            .WithSummary("The home page's numbers for a company on a day (today in the company's time zone when no date is given): net sales today, this month and the month before with a thirty-day series; receivables and payables past due and due within seven days; every bank account and cash box's balance with the month's receipts and payments; quotations, orders, shipments and returns waiting for their next step. A section is left out when the member may not read that module in the company.");
        reporting.MapGet("/sales-lines", async (Guid companyId, DateOnly? from, DateOnly? to, string? status, SalesLinesService service, CancellationToken ct) => ApiProblems.Ok(await service.ReadAsync(companyId, from, to, status, ct)))
            .WithSummary("Every line of the company's sales invoices and credit notes in the range (posting dates; the year to date when no range is given; posted ones unless status names others, comma-separated) with what a grid groups by: the document, its month, quarter and year, the customer with their group, rep, branch, terms and price list, the item with its type, category, brand and unit, the tax code, the cost centre, department and project, and the amounts (credit notes negative). Capped at 20,000 lines; `truncated` says to narrow the range");
        return api;
    }
}

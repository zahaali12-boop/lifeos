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
        return api;
    }
}

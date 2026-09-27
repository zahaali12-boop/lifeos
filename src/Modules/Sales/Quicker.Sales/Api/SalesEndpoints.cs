using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Sales.Application;
using Quicker.Web;

namespace Quicker.Sales.Api;

/// <summary>Sales under /api/v1/sales: quotations, priced by the pricing engine and taxed by the tax engine (roadmap 5.4a).</summary>
public static class SalesEndpoints
{
    public static RouteGroupBuilder MapSalesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var sales = api.MapGroup("/sales").WithTags("Sales").RequireAuthorization();

        var quotations = sales.MapGroup("/quotations");
        quotations.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, QuotationService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(SalesPermissions.QuoteRead)
            .WithSummary("Quotations, newest first, filtered by company, status or customer");
        quotations.MapPost("/", async (SaveQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static q => $"/api/v1/sales/quotations/{q.Id}"))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("A draft quotation with lines priced by the pricing engine (customer agreements, price lists, promotions) and taxed for the customer on the pricing date");
        quotations.MapGet("/{quotationId:guid}", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(quotationId, ct), "quotation", quotationId))
            .RequirePermission(SalesPermissions.QuoteRead)
            .WithSummary("The quotation with its priced, taxed lines");
        quotations.MapPut("/{quotationId:guid}", async (Guid quotationId, SaveQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(quotationId, request, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Re-prices and re-taxes a draft quotation's lines; only a draft is edited");
        quotations.MapPost("/{quotationId:guid}/send", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.SendAsync(quotationId, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Marks a draft quotation sent to the customer");
        quotations.MapPost("/{quotationId:guid}/accept", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.AcceptAsync(quotationId, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Accepts a sent quotation; refused once its validity has passed (quotation.expired)");
        quotations.MapPost("/{quotationId:guid}/reject", async (Guid quotationId, RejectQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.RejectAsync(quotationId, request, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Rejects a sent quotation with a reason");

        return api;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Kernel.Ids;
using Quicker.Numbering.Application;
using Quicker.Numbering.Contracts;
using Quicker.Web;

namespace Quicker.Numbering.Api;

/// <summary>The numbering surface of /api/v1: series, counters, allocation log, gapless audit, preview and allocation for external documents.</summary>
public static class NumberingEndpoints
{
    public static RouteGroupBuilder MapNumberingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var numbering = api.MapGroup("/numbering").WithTags("Numbering").RequireAuthorization();

        var series = numbering.MapGroup("/series");
        series.MapGet("/", async (string? documentType, Guid? companyId, SeriesService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(documentType, companyId, ct)))
            .RequirePermission(NumberingPermissions.SeriesRead);
        series.MapGet("/{seriesId:guid}", async (Guid seriesId, SeriesService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(seriesId, ct), "series", seriesId))
            .RequirePermission(NumberingPermissions.SeriesRead);
        series.MapPost("/", async (SaveSeriesRequest request, SeriesService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static s => $"/api/v1/numbering/series/{s.Id}"))
            .RequirePermission(NumberingPermissions.SeriesManage)
            .WithSummary("Create a series: template with {company} {branch} {yy} {yyyy} {mm} {fy} and one {seq[:N]}, gapless or not, reset never/yearly/monthly");
        series.MapPut("/{seriesId:guid}", async (Guid seriesId, SaveSeriesRequest request, SeriesService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(seriesId, request, ct)))
            .RequirePermission(NumberingPermissions.SeriesManage);
        series.MapPut("/{seriesId:guid}/counter", async (Guid seriesId, SetCounterRequest request, SeriesService service, CurrentPrincipal current, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetCounterAsync(seriesId, request, current.Required.Has(NumberingPermissions.Reset), ct)))
            .RequirePermission(NumberingPermissions.SeriesManage)
            .WithSummary("Set the next number of a period; moving backwards needs numbering.counter.reset and a reason and never goes below an issued number");
        series.MapGet("/{seriesId:guid}/allocations", async (Guid seriesId, string? periodKey, int? limit, SeriesService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAllocationsAsync(seriesId, periodKey, limit ?? 200, ct)))
            .RequirePermission(NumberingPermissions.SeriesRead);
        series.MapGet("/{seriesId:guid}/gaps", async (Guid seriesId, SeriesService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.GapsAsync(seriesId, ct)))
            .RequirePermission(NumberingPermissions.SeriesRead)
            .WithSummary("Gapless audit: per period, the issued range and every missing number (expected none on gapless series)");

        numbering.MapPost("/preview", async (AllocateRequest request, NumberAllocator allocator, CancellationToken ct) =>
            ApiProblems.Ok(await allocator.PreviewAsync(ToRequest(request), ct)))
            .RequirePermission(NumberingPermissions.SeriesRead)
            .WithSummary("Which series a document would use and what its next number would look like (no allocation)");
        numbering.MapPost("/allocate", async (AllocateRequest request, NumberAllocator allocator, CancellationToken ct) =>
            ApiProblems.Ok(await allocator.AllocateAsync(ToRequest(request), ct)))
            .RequirePermission(NumberingPermissions.Allocate)
            .WithSummary("Allocate a number for a document managed outside Quicker (integrations); documents inside Quicker allocate in their posting transaction");
        numbering.MapGet("/drafts/{documentId:guid}", (Guid documentId) => Results.Ok(new { documentId, identifier = DraftIdentifiers.For(documentId) }))
            .RequirePermission(NumberingPermissions.SeriesRead);

        return api;
    }

    private static NumberRequest ToRequest(AllocateRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new NumberRequest(r.DocumentType, new CompanyId(r.CompanyId), r.BranchId is { } b ? new BranchId(b) : null, r.Date, r.DocumentId, r.SeriesId);
    }
}

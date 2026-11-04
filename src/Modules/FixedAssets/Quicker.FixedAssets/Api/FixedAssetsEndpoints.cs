using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.FixedAssets.Application;
using Quicker.Web;

namespace Quicker.FixedAssets.Api;

/// <summary>The fixed-assets surface of /api/v1: books and categories, the register and its postings, depreciation runs, forecast, register report and reconciliation.</summary>
public static class FixedAssetsEndpoints
{
    public static RouteGroupBuilder MapFixedAssetsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var fa = api.MapGroup("/fixed-assets").WithTags("Fixed assets").RequireAuthorization();

        var books = fa.MapGroup("/books");
        books.MapGet("/", async (Guid? companyId, BookService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("Asset books of a company: the one that posts to the ledger (created as MAIN with the first asset) and memo books such as a tax book");
        books.MapPost("/", async (SaveAssetBookRequest request, BookService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static b => $"/api/v1/fixed-assets/books/{b.Id}"))
            .RequirePermission(FixedAssetsPermissions.SetupManage)
            .WithSummary("A new book; one book per company posts to the ledger");
        books.MapPut("/{bookId:guid}", async (Guid bookId, SaveAssetBookRequest request, BookService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(bookId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.SetupManage)
            .WithSummary("Changes a book's name or activity");

        var categories = fa.MapGroup("/categories");
        categories.MapGet("/", async (bool? isActive, CategoryService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(isActive, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("Asset categories: the defaults an asset starts with (method, life, salvage share, convention, declining rate) and the accounts its postings land on");
        categories.MapPost("/", async (SaveAssetCategoryRequest request, CategoryService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static c => $"/api/v1/fixed-assets/categories/{c.Id}"))
            .RequirePermission(FixedAssetsPermissions.SetupManage)
            .WithSummary("A new asset category");
        categories.MapPut("/{categoryId:guid}", async (Guid categoryId, SaveAssetCategoryRequest request, CategoryService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(categoryId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.SetupManage)
            .WithSummary("Changes a category; assets already created keep their own values");

        var assets = fa.MapGroup("/assets");
        assets.MapGet("/", async (Guid companyId, string? status, Guid? categoryId, string? q, AssetService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, categoryId, q, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("The register of a company: every asset with its ledger book's cost, depreciation, impairment and net book value; by status, category and text");
        assets.MapPost("/", async (SaveAssetRequest request, AssetService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static a => $"/api/v1/fixed-assets/assets/{a.Id}"))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("Drafts an asset: category, cost, acquisition and in-service dates, the source of the money (supplier, bank, clearing, opening), location, custodian; a value per book from the category's defaults, overridable per book; custom fields under host 'fa_asset'");
        assets.MapGet("/{assetId:guid}", async (Guid assetId, AssetService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(assetId, ct), AssetService.DocumentType, assetId))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("One asset with its books, their plans, its transactions (the FA subledger) and its verifications");
        assets.MapPut("/{assetId:guid}", async (Guid assetId, SaveAssetRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("Changes a draft in full, an active asset's descriptive fields only");
        assets.MapDelete("/{assetId:guid}", async (Guid assetId, AssetService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(assetId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("Deletes a draft asset");
        assets.MapPost("/{assetId:guid}/activate", async (Guid assetId, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.ActivateAsync(assetId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Activates the asset: the acquisition posted from its source (Dr cost, and input tax from a supplier / Cr the payable, the bank, the clearing account or opening equity), the plan of every book generated");
        assets.MapPost("/{assetId:guid}/additions", async (Guid assetId, AdditionRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.AddAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Capital added to an active asset (an improvement): Dr cost / Cr the source; the plan continues over the remaining life");
        assets.MapPost("/{assetId:guid}/impair", async (Guid assetId, ImpairmentRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.ImpairAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Writes the asset down (IAS 36): any revaluation surplus first, then a loss; Cr accumulated impairment");
        assets.MapPost("/{assetId:guid}/revalue", async (Guid assetId, RevaluationRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.RevalueAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Restates the asset at fair value (IAS 16 revaluation model, elimination approach): earlier impairment losses reversed through profit or loss first, the rest to the revaluation surplus; a fall uses the surplus first");
        assets.MapPost("/{assetId:guid}/transfer", async (Guid assetId, TransferRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.TransferAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Moves the asset to another branch, location (dimensions) or custodian; a change of branch or dimensions moves its balances in the ledger");
        assets.MapPost("/{assetId:guid}/dispose", async (Guid assetId, DisposalRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.DisposeAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetPost)
            .WithSummary("Sells or scraps the asset, or part of it by quantity: depreciation caught up to the day by the convention, cost and accumulated amounts out, proceeds to a bank account or a receivable on a customer, the gain or loss, the surplus to retained earnings");
        assets.MapPost("/{assetId:guid}/verify", async (Guid assetId, VerificationRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.VerifyAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("Records a physical verification: when, by whom, where, in what condition");
        assets.MapPost("/{assetId:guid}/usage", async (Guid assetId, UsageRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.RecordUsageAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("Units produced in a month, for a book on the units-of-production method; the plan follows");
        assets.MapPut("/{assetId:guid}/estimates", async (Guid assetId, ChangeEstimatesRequest request, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.ChangeEstimatesAsync(assetId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetManage)
            .WithSummary("A change of estimate in one book (remaining life, salvage, method, rate, total units): prospective, from the next unposted month");

        var runs = fa.MapGroup("/depreciation-runs");
        runs.MapGet("/", async (Guid companyId, Guid? bookId, DepreciationService service, CancellationToken ct) => TypedResults.Ok(await service.RunsAsync(companyId, bookId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("Depreciation runs of a company, newest first");
        runs.MapPost("/", async (DepreciationRunRequest request, DepreciationService service, CancellationToken ct) => ApiProblems.Created(await service.PostRunAsync(request, ct), static r => $"/api/v1/fixed-assets/depreciation-runs/{r.Id}"))
            .RequirePermission(FixedAssetsPermissions.DepreciationRun)
            .WithSummary("Posts every planned month of the book through a date as one run (one journal: Dr depreciation expense per asset with its dimensions / Cr accumulated depreciation in the FA subledger); the ledger book by default");
        runs.MapGet("/{runId:guid}", async (Guid runId, DepreciationService service, CancellationToken ct) => ApiProblems.Ok(await service.GetRunAsync(runId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("One run");
        runs.MapPost("/{runId:guid}/reverse", async (Guid runId, ReverseDepreciationRequest request, DepreciationService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseRunAsync(runId, request, ct)))
            .RequirePermission(FixedAssetsPermissions.DepreciationRun)
            .WithSummary("Reverses the book's latest run: the mirror journal, the months back to planned");

        fa.MapGet("/forecast", async (Guid companyId, Guid? bookId, DateOnly from, DateOnly to, DepreciationService service, CancellationToken ct) => TypedResults.Ok(await service.ForecastAsync(companyId, bookId, from, to, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("Planned and posted depreciation by month over a range");
        fa.MapGet("/register", async (Guid companyId, Guid? bookId, DateOnly? from, DateOnly? asOf, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.RegisterAsync(companyId, bookId, from, asOf, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("The register at a date: cost, accumulated depreciation, impairment and net book value per asset from the subledger, with the depreciation of the range");
        fa.MapGet("/reconciliation", async (Guid companyId, AssetService service, CancellationToken ct) => ApiProblems.Ok(await service.ReconciliationAsync(companyId, ct)))
            .RequirePermission(FixedAssetsPermissions.AssetRead)
            .WithSummary("The subledger's totals per control role and whether the ledger agrees, asset by asset");
        return api;
    }
}

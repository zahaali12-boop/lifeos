using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Accounting.Application;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Web;

namespace Quicker.Accounting.Api;

/// <summary>The accounting surface of /api/v1: charts, accounts, dimension rules, categories, statutory mappings, import/export, company settings.</summary>
public static class AccountingEndpoints
{
    public static RouteGroupBuilder MapAccountingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var accounting = api.MapGroup("/accounting").WithTags("Accounting").RequireAuthorization();

        accounting.MapGet("/chart-templates", static () => TypedResults.Ok(ChartService.Templates()))
            .RequirePermission(AccountingPermissions.ChartRead)
            .WithSummary("The chart templates a company can start from: IFRS for SMEs, GCC (VAT and Zakat), Iraq (mapped to the Unified Accounting System)");

        var charts = accounting.MapGroup("/charts");
        charts.MapGet("/", async (ChartService service, CancellationToken ct) => TypedResults.Ok(await service.ListChartsAsync(ct)))
            .RequirePermission(AccountingPermissions.ChartRead);
        charts.MapPost("/", async (SaveChartRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateChartAsync(request, ct), static c => $"/api/v1/accounting/charts/{c.Id}"))
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("An empty chart (accounts are added one by one or imported); accountCodeFormat uses # digit, A letter, ? either");
        charts.MapPost("/from-template", async (FromTemplateRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateFromTemplateAsync(request, ct), static c => $"/api/v1/accounting/charts/{c.Id}"))
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("A complete chart from a template, optionally assigned to a company so it can post on day one");
        charts.MapGet("/{chartId:guid}", async (Guid chartId, string? expand, ChartService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetChartAsync(chartId, expand, ct), "chart", chartId))
            .RequirePermission(AccountingPermissions.ChartRead)
            .WithSummary("?expand=accounts includes the whole tree in code order with level and path");
        charts.MapPut("/{chartId:guid}", async (Guid chartId, SaveChartRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateChartAsync(chartId, request, ct)))
            .RequirePermission(AccountingPermissions.ChartManage);
        charts.MapGet("/{chartId:guid}/accounts", async (Guid chartId, ChartService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetChartAsync(chartId, null, ct) is null ? null : await service.ListAccountsAsync(chartId, ct), "chart", chartId))
            .RequirePermission(AccountingPermissions.ChartRead);
        charts.MapPost("/{chartId:guid}/accounts", async (Guid chartId, SaveAccountRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAccountAsync(chartId, request, ct), static a => $"/api/v1/accounting/accounts/{a.Id}"))
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("Parent by parentId or parentCode (a header of the same type); control accounts name their subledger");
        charts.MapGet("/{chartId:guid}/export", async (Guid chartId, ChartService service, CancellationToken ct) =>
            ApiProblems.From(await service.ExportCsvAsync(chartId, ct), static csv => Results.Text(csv, "text/csv; charset=utf-8")))
            .RequirePermission(AccountingPermissions.ChartRead)
            .WithSummary("CSV with one row per account (the import format)");
        charts.MapPost("/{chartId:guid}/import", async (Guid chartId, HttpRequest http, ChartService service, CancellationToken ct) =>
        {
            IReadOnlyList<SaveAccountRequest> rows;
            if (http.ContentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(http.Body);
                rows = AccountCsv.Read(await reader.ReadToEndAsync(ct));
            }
            else
            {
                var body = await http.ReadFromJsonAsync<ImportRequest>(ct);
                if (body is null)
                {
                    return ApiProblems.From(Error.Validation("import.body_required", "Send text/csv or a JSON body {accounts: [...]}."));
                }

                rows = body.Accounts;
            }

            return ApiProblems.From(await service.ImportAsync(chartId, rows, ct), static r => TypedResults.Ok(r));
        })
            .Accepts<ImportRequest>("application/json", "text/csv")
            .Produces<ImportResult>()
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("Upsert accounts by code from CSV (the export format) or JSON; parents may come after children; all or nothing");
        charts.MapGet("/{chartId:guid}/mappings/{statutoryChartCode}", async (Guid chartId, string statutoryChartCode, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListMappingsAsync(chartId, statutoryChartCode, ct)))
            .RequirePermission(AccountingPermissions.ChartRead)
            .WithSummary("Every postable account with its statutory code (null = unmapped)");
        charts.MapPut("/{chartId:guid}/mappings/{statutoryChartCode}", async (Guid chartId, string statutoryChartCode, IReadOnlyList<MappingRequest> request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetMappingsAsync(chartId, statutoryChartCode, request, ct)))
            .RequirePermission(AccountingPermissions.ChartManage);

        var accounts = accounting.MapGroup("/accounts");
        accounts.MapGet("/{accountId:guid}", async (Guid accountId, ChartService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAccountAsync(accountId, ct), "account", accountId))
            .RequirePermission(AccountingPermissions.ChartRead);
        accounts.MapPut("/{accountId:guid}", async (Guid accountId, SaveAccountRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAccountAsync(accountId, request, ct)))
            .RequirePermission(AccountingPermissions.ChartManage);
        accounts.MapGet("/{accountId:guid}/dimension-rules", async (Guid accountId, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListDimensionRulesAsync(accountId, ct)))
            .RequirePermission(AccountingPermissions.ChartRead);
        accounts.MapPut("/{accountId:guid}/dimension-rules", async (Guid accountId, IReadOnlyList<DimensionRuleRequest> request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetDimensionRulesAsync(accountId, request, ct)))
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("Replaces the account's rules: required, optional (with an optional default value) or blocked per dimension");
        accounts.MapPost("/{accountId:guid}/check", async (Guid accountId, CheckLineRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok((await service.CheckLineAsync(accountId, new CompanyId(request.CompanyId), request.Dimensions, request.Currency, request.Manual, ct))
                .Then(static check => new CheckLineResult(check.Account.Id, check.Account.Code, check.Dimensions))))
            .RequirePermission(AccountingPermissions.ChartRead)
            .WithSummary("Would a line on this account be accepted for the company? Returns the dimensions with defaults applied, or the problem the posting engine would raise");

        accounting.MapGet("/account-categories", async (ChartService service, CancellationToken ct) => TypedResults.Ok(await service.ListCategoriesAsync(ct)))
            .RequirePermission(AccountingPermissions.ChartRead);
        accounting.MapPut("/account-categories/{code}", async (string code, SaveCategoryRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SaveCategoryAsync(code, request, ct)))
            .RequirePermission(AccountingPermissions.ChartManage);

        accounting.MapGet("/statutory-charts", async (ChartService service, CancellationToken ct) => TypedResults.Ok(await service.ListStatutoryChartsAsync(ct)))
            .RequirePermission(AccountingPermissions.ChartRead);

        var company = accounting.MapGroup("/companies/{companyId:guid}");
        company.MapGet("/settings", async (Guid companyId, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.CompanySettingsAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.ChartRead);
        company.MapPut("/chart", async (Guid companyId, AssignChartRequest request, ChartService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.AssignToCompanyAsync(companyId, request.ChartId, ct)))
            .RequirePermission(AccountingPermissions.ChartManage)
            .WithSummary("The chart the company posts to: shared or dedicated to it; null detaches");

        accounting.MapPostingEndpoints();
        accounting.MapJournalEndpoints();
        accounting.MapInquiryEndpoints();
        return api;
    }

    private static Result<TOut> Then<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> map) => result.IsSuccess ? map(result.Value) : result.Error!;
}

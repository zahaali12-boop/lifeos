using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Quicker.Accounting.Application;
using Quicker.Kernel.Results;
using Quicker.Web;
using Quicker.Web.Exports;

namespace Quicker.Accounting.Api;

/// <summary>General ledger inquiry under /api/v1/accounting/companies/{id}/reports: trial balance, account ledger, balances by dimension, exports.</summary>
public static class InquiryEndpoints
{
    public static RouteGroupBuilder MapInquiryEndpoints(this RouteGroupBuilder accounting)
    {
        ArgumentNullException.ThrowIfNull(accounting);
        var reports = accounting.MapGroup("/companies/{companyId:guid}/reports");

        reports.MapGet("/trial-balance", async Task<Results<Ok<TrialBalance>, FileContentHttpResult, ProblemHttpResult>> (Guid companyId, HttpRequest http, DateOnly? asOf, DateOnly? from, DateOnly? compareAsOf, string? basis, string? groupBy, bool? includeClosing, string? format, InquiryService service, CancellationToken ct) =>
        {
            if (format is not null && !TabularExport.IsKnownFormat(format))
            {
                return ApiProblems.From(FormatInvalid(format));
            }

            var report = await service.TrialBalanceAsync(companyId, new InquiryQuery(asOf, from, compareAsOf, basis, groupBy, includeClosing ?? false, DimensionFilters(http)), ct);
            if (report.IsFailure)
            {
                return ApiProblems.From(report.Error!);
            }

            if (format is null)
            {
                return TypedResults.Ok(report.Value);
            }

            var file = await service.ExportTrialBalanceAsync(report.Value, format, RightToLeft(http), ct);
            return TypedResults.File(file.Bytes, file.ContentType, file.FileName);
        })
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("Trial balance at asOf (today by default) with opening/movement/closing when from is given, a comparative (compareAsOf), basis fc|rc, groupBy=DIMENSION, dimension filters d.CODE=valueId, format=csv|xlsx; every row carries its ledger drill parameters");

        reports.MapGet("/ledger", async Task<Results<Ok<AccountLedger>, FileContentHttpResult, ProblemHttpResult>> (Guid companyId, HttpRequest http, Guid? accountId, string? accountCode, DateOnly? from, DateOnly? to, string? basis, bool? includeClosing, int? limit, string? cursor, string? format, InquiryService service, CancellationToken ct) =>
        {
            if (format is not null && !TabularExport.IsKnownFormat(format))
            {
                return ApiProblems.From(FormatInvalid(format));
            }

            if (accountId is null && string.IsNullOrWhiteSpace(accountCode))
            {
                return ApiProblems.From(Error.Validation("inquiry.account_required", "Give accountId or accountCode."));
            }

            var page = format is null ? new PageRequest(limit, cursor) : new PageRequest(PageRequest.MaxLimit * 50, null);
            var ledger = await service.LedgerAsync(companyId, accountId, accountCode, new InquiryQuery(to, from, null, basis, null, includeClosing ?? false, DimensionFilters(http)), page, ct);
            if (ledger.IsFailure)
            {
                return ApiProblems.From(ledger.Error!);
            }

            if (format is null)
            {
                return TypedResults.Ok(ledger.Value);
            }

            var file = await service.ExportLedgerAsync(ledger.Value, format, RightToLeft(http), ct);
            return TypedResults.File(file.Bytes, file.ContentType, file.FileName);
        })
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("Account ledger (accountId or accountCode) from a date to a date with opening, running balance and the source document of every line (sourceLink where the platform knows the document); paged by limit/cursor; format=csv|xlsx exports up to 10,000 lines");

        reports.MapGet("/dimension-balances", async (Guid companyId, HttpRequest http, string dimension, DateOnly? asOf, DateOnly? from, string? accountType, Guid? accountId, string? basis, bool? includeClosing, InquiryService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.DimensionBalancesAsync(companyId, dimension, accountType, accountId, new InquiryQuery(asOf, from, null, basis, null, includeClosing ?? false, DimensionFilters(http)), ct)))
            .RequirePermission(AccountingPermissions.JournalRead)
            .WithSummary("Balances per value of one dimension (lines without the dimension are the row without a value), optionally for one account type or account; the same window and filter parameters as the trial balance");

        return accounting;
    }

    private static Error FormatInvalid(string format) => Error.Validation("export.format_invalid", "Formats are csv and xlsx.").WithWhy(("format", format));

    /// <summary>Dimension filters come as <c>d.COST_CENTER=valueId</c> query pairs (any number of dimensions).</summary>
    private static Dictionary<string, Guid> DimensionFilters(HttpRequest http)
    {
        var filters = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (key, values) in http.Query)
        {
            if (key.StartsWith("d.", StringComparison.OrdinalIgnoreCase) && key.Length > 2 && Guid.TryParse(values.LastOrDefault(), out var valueId))
            {
                filters[key[2..]] = valueId;
            }
        }

        return filters;
    }

    private static bool RightToLeft(HttpRequest http) => http.Headers.AcceptLanguage.ToString().Split(',')[0].Trim().StartsWith("ar", StringComparison.OrdinalIgnoreCase);
}

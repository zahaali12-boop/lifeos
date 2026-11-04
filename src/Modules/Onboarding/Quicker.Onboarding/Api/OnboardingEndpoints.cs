using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Kernel.Results;
using Quicker.Onboarding.Application;
using Quicker.Onboarding.Contracts;
using Quicker.Web;
using Quicker.Web.Imports;

namespace Quicker.Onboarding.Api;

/// <summary>The setup wizard's surface under /api/v1/onboarding: where the tenant stands, and the go-live imports that span modules.</summary>
public static class OnboardingEndpoints
{
    public static RouteGroupBuilder MapOnboardingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var onboarding = api.MapGroup("/onboarding").WithTags("Onboarding").RequireAuthorization();
        onboarding.MapGet("/status", async (Guid? companyId, SetupStatusService service, CancellationToken ct) => TypedResults.Ok(await service.ReadAsync(companyId, ct)))
            .RequirePermission("organization.company.read")
            .WithSummary("What the tenant has set up so far: companies, the chosen (or first) company's chart, tax regimes, customers, suppliers, items and its posted opening journals, so the setup wizard can show each step done");
        onboarding.MapPost("/companies/{companyId:guid}/opening-balances", async (Guid companyId, DateOnly? asOf, bool? dryRun, HttpRequest http, OpeningBalanceService service, CancellationToken ct) =>
        {
            if (asOf is null)
            {
                return ApiProblems.From(Error.Validation("opening.as_of_required", "asOf names the day the balances stand at (the day before the first open period usually)."));
            }

            IReadOnlyList<OpeningBalanceRow> rows;
            if (Sheet.IsSpreadsheet(http))
            {
                var sheet = await Sheet.ReadAsync(http, ct);
                if (sheet.IsFailure)
                {
                    return ApiProblems.From(sheet.Error!);
                }

                var parsed = OpeningBalanceCsv.Read(sheet.Value);
                if (parsed.IsFailure)
                {
                    return ApiProblems.From(parsed.Error!);
                }

                rows = parsed.Value;
            }
            else
            {
                var body = await http.ReadFromJsonAsync<OpeningBalanceImportRequest>(ct);
                if (body?.Lines is null)
                {
                    return ApiProblems.From(Error.Validation("import.body_required", "Send text/csv, an .xlsx workbook or a JSON body {lines: [...]}."));
                }

                rows = body.Lines;
            }

            return ApiProblems.From(await service.ImportAsync(companyId, asOf.Value, rows, dryRun == true, ct), static r => TypedResults.Ok(r));
        })
            .Accepts<OpeningBalanceImportRequest>("application/json", "text/csv", Sheet.XlsxContentType)
            .Produces<OpeningBalanceResult>()
            .RequirePermission("accounting.journal.post")
            .WithSummary("Opening balances as of a day from CSV, an Excel workbook (.xlsx: account, debit, credit, partner, bank_account, due_date, description) or JSON: one opening journal created and posted, a partner on each receivable or payable line opening its item, a bank account or cash box on each cash line, the difference to opening balance equity. dryRun=true checks the whole file and keeps nothing");
        return api;
    }
}

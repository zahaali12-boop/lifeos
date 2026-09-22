using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Accounting.Application;
using Quicker.Kernel.Results;
using Quicker.Web;

namespace Quicker.Accounting.Api;

/// <summary>Manual journals, recurring templates, deferral schedules and the daily routines under /api/v1/accounting.</summary>
public static class JournalEndpoints
{
    public static RouteGroupBuilder MapJournalEndpoints(this RouteGroupBuilder accounting)
    {
        ArgumentNullException.ThrowIfNull(accounting);

        var company = accounting.MapGroup("/companies/{companyId:guid}");
        company.MapGet("/journals", async (Guid companyId, string? status, DateOnly? from, DateOnly? to, int? limit, string? cursor, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(companyId, status, from, to, new PageRequest(limit, cursor), ct)))
            .RequirePermission(AccountingPermissions.JournalRead);
        company.MapPost("/journals", async (Guid companyId, SaveJournalRequest request, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(companyId, request, null, ct), static j => $"/api/v1/accounting/journals/{j.Id}"))
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("A draft journal: kind manual|opening|accrual|reversing|allocation, lines by account code with debit or credit; accruals name their auto-reversal date");
        company.MapPost("/journals/import", async (Guid companyId, HttpRequest http, ManualJournalService service, CancellationToken ct) =>
        {
            IReadOnlyList<SaveJournalRequest> rows;
            if (http.ContentType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(http.Body);
                var parsed = JournalCsv.Read(await reader.ReadToEndAsync(ct));
                if (parsed.IsFailure)
                {
                    return ApiProblems.From(parsed.Error!);
                }

                rows = parsed.Value;
            }
            else
            {
                var body = await http.ReadFromJsonAsync<JournalImportRequest>(ct);
                if (body is null)
                {
                    return ApiProblems.From(Error.Validation("import.body_required", "Send text/csv or a JSON body {journals: [...]}."));
                }

                rows = body.Journals;
            }

            return ApiProblems.From(await service.ImportAsync(companyId, rows, ct), static r => TypedResults.Ok(r));
        })
            .Accepts<JournalImportRequest>("application/json", "text/csv")
            .Produces<JournalImportResult>()
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("Drafts from a batch: JSON journals or CSV rows grouped by journal_ref (columns: journal_ref, posting_date, currency, account_code, debit, credit, description); all or nothing");
        company.MapGet("/recurring-templates", async (Guid companyId, RecurringService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.JournalRead);
        company.MapPost("/recurring-templates", async (Guid companyId, SaveRecurringTemplateRequest request, RecurringService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(companyId, request, ct), static t => $"/api/v1/accounting/recurring-templates/{t.Id}"))
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("Cron in the company's time zone; amountMode fixed (amounts), percentage (shares of a base amount) or variable (accounts only, always reviewed)");
        company.MapGet("/deferrals", async (Guid companyId, DeferralService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(AccountingPermissions.JournalRead);
        company.MapPost("/deferrals", async (Guid companyId, SaveDeferralRequest request, DeferralService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(companyId, request, ct), static s => $"/api/v1/accounting/deferrals/{s.Id}"))
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("A prepayment, accrual or deferred revenue schedule over N fiscal periods (straight line or daily), amortised to the minor unit");
        company.MapPost("/deferrals/preview", async (Guid companyId, SaveDeferralRequest request, DeferralService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.PreviewAsync(companyId, request, ct)))
            .RequirePermission(AccountingPermissions.JournalRead);

        var journals = accounting.MapGroup("/journals");
        journals.MapGet("/{journalId:guid}", async (Guid journalId, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(journalId, ct), ManualJournalService.EntityType, journalId))
            .RequirePermission(AccountingPermissions.JournalRead);
        journals.MapPut("/{journalId:guid}", async (Guid journalId, SaveJournalRequest request, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(journalId, request, ct)))
            .RequirePermission(AccountingPermissions.JournalManage);
        journals.MapPost("/{journalId:guid}/submit", async (Guid journalId, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SubmitAsync(journalId, ct)))
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("Balanced drafts move to pending_approval when the company setting accounting.journals.approval is \"required\", else straight to approved");
        journals.MapPost("/{journalId:guid}/approve", async (Guid journalId, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ApproveAsync(journalId, ct)))
            .RequirePermission(AccountingPermissions.JournalApprove)
            .WithSummary("By someone other than the submitter");
        journals.MapPost("/{journalId:guid}/reject", async (Guid journalId, RejectJournalRequest request, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.RejectAsync(journalId, request.Reason, ct)))
            .RequirePermission(AccountingPermissions.JournalApprove);
        journals.MapPost("/{journalId:guid}/cancel", async (Guid journalId, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.CancelAsync(journalId, ct)))
            .RequirePermission(AccountingPermissions.JournalManage);
        journals.MapPost("/{journalId:guid}/correct", async (Guid journalId, CorrectJournalRequest request, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CorrectAsync(journalId, request.Reason, ct), static j => $"/api/v1/accounting/journals/{j.Id}"))
            .RequirePermission(AccountingPermissions.JournalManage)
            .WithSummary("A correction draft of a posted journal: its lines copied, dated in the first open period on or after the original; posting it reverses the original there and posts the replacement, both linked (ADR-0026)");
        journals.MapPost("/{journalId:guid}/post", async (Guid journalId, ManualJournalService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.PostAsync(journalId, ct)))
            .RequirePermission(AccountingPermissions.JournalPost)
            .WithSummary("Posts through the engine; the journal keeps its number and the entry id");

        var templates = accounting.MapGroup("/recurring-templates");
        templates.MapGet("/{templateId:guid}", async (Guid templateId, RecurringService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(templateId, ct), "recurring_template", templateId))
            .RequirePermission(AccountingPermissions.JournalRead);
        templates.MapPut("/{templateId:guid}", async (Guid templateId, SaveRecurringTemplateRequest request, RecurringService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(templateId, request, ct)))
            .RequirePermission(AccountingPermissions.JournalManage);
        templates.MapPost("/{templateId:guid}/generate", async (Guid templateId, GenerateRecurringRequest? request, RecurringService service, CancellationToken ct) =>
            ApiProblems.Created(await service.GenerateAsync(templateId, request ?? new GenerateRecurringRequest(), ct), static j => $"/api/v1/accounting/journals/{j.Id}"))
            .RequirePermission(AccountingPermissions.JournalPost)
            .WithSummary("Generates the journal of the next due run (or the given date) and posts it unless the template requires review");

        var deferrals = accounting.MapGroup("/deferrals");
        deferrals.MapGet("/{scheduleId:guid}", async (Guid scheduleId, DeferralService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(scheduleId, ct), "deferral_schedule", scheduleId))
            .RequirePermission(AccountingPermissions.JournalRead);
        deferrals.MapPost("/{scheduleId:guid}/post-due", async (Guid scheduleId, DateOnly? asOf, DeferralService service, AccountingRoutines routines, CancellationToken ct) =>
        {
            var schedule = await service.GetAsync(scheduleId, ct);
            if (schedule is null)
            {
                return ApiProblems.From(Error.NotFound("deferral_schedule", scheduleId));
            }

            return ApiProblems.From(await service.PostDueAsync(scheduleId, null, asOf ?? await routines.TodayForAsync(schedule.CompanyId, ct), ct), static r => TypedResults.Ok(r));
        })
            .Produces<IReadOnlyList<RoutineOutcome>>()
            .RequirePermission(AccountingPermissions.JournalPost)
            .WithSummary("Posts the planned lines due on or before the date (today in the company's time zone by default)");
        deferrals.MapPost("/{scheduleId:guid}/cancel", async (Guid scheduleId, DeferralService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.CancelAsync(scheduleId, ct)))
            .RequirePermission(AccountingPermissions.JournalManage);

        accounting.MapPost("/routines/run", async (Guid? companyId, DateOnly? asOf, AccountingRoutines routines, CancellationToken ct) =>
            TypedResults.Ok(await routines.RunAsync(companyId, asOf ?? (companyId is { } c ? await routines.TodayForAsync(c, ct) : null), ct)))
            .RequirePermission(AccountingPermissions.RoutinesRun)
            .WithSummary("Runs the daily routines now for the tenant (or one company): automatic reversals, recurring journals, deferral postings; what waited says why");

        return accounting;
    }
}

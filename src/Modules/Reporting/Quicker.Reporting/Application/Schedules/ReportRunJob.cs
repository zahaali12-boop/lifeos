using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Printing.Contracts;
using Quicker.Reporting.Application.Reports;
using Quicker.Reporting.Application.Statements;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;
using Quicker.Storage;
using Quicker.Web;

namespace Quicker.Reporting.Application.Schedules;

/// <summary>What the job carries: the schedule to run and whether the scheduler or a person fired it.</summary>
public sealed record ReportRunPayload(Guid ScheduleId, string Trigger = "schedule");

/// <summary>
/// Job <c>reporting.run</c>: runs a schedule as its owner (the owner's permissions and scopes as they are now), writes
/// the file, emails it, and records the run. A failure is recorded on the run rather than retried: the next slot of
/// the cron runs again, and the history shows what went wrong (A-185).
/// </summary>
public sealed class ReportRunJob(ScheduleRunner runner) : IJobHandler<ReportRunPayload>
{
    public static string JobType => "reporting.run";

    public async Task<object?> ExecuteAsync(ReportRunPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is null)
        {
            throw new JobFailedException("A report schedule runs inside a tenant.");
        }

        var run = await runner.RunAsync(payload.ScheduleId, payload.Trigger, context.JobId, cancellationToken);
        return new { run.Status, run.RowCount, run.FileName, recipients = run.Recipients, run.Error };
    }
}

/// <summary>The outcome of a run, as the job reports it.</summary>
public sealed record RunOutcome(string Status, int? RowCount, string? FileName, IReadOnlyList<string> Recipients, string? Error);

/// <summary>Runs one schedule inside the job's unit of work; the run row is written through <see cref="RunRecorder"/> in a transaction of its own so a failure is kept.</summary>
public sealed class ScheduleRunner(
    ReportingDbContext db,
    RunRecorder recorder,
    IMemberPrincipals principals,
    CurrentPrincipal current,
    ReportService reports,
    StatementService statements,
    ICompanyDirectory companies,
    IMemberDirectory members,
    IPdfPrinter pdf,
    IObjectStorage storage,
    IEmailSender email,
    IUnitOfWorkAccessor unitOfWork,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RunOutcome> RunAsync(Guid scheduleId, string trigger, Guid jobId, CancellationToken cancellationToken)
    {
        var schedule = await db.Schedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return new RunOutcome(RunStatuses.Failed, null, null, [], "schedule.not_found");
        }

        var run = new ReportRun
        {
            Id = Guid.CreateVersion7(),
            ScheduleId = schedule.Id,
            CompanyId = schedule.CompanyId,
            Source = schedule.Source,
            ReportId = schedule.ReportId,
            LayoutId = schedule.LayoutId,
            LayoutCode = schedule.LayoutCode,
            RunAsMembershipId = schedule.OwnerMembershipId,
            Trigger = trigger,
            JobId = jobId,
            StartedAt = clock.UtcNow,
            Status = RunStatuses.Running,
        };
        await recorder.WriteAsync(run, cancellationToken);
        try
        {
            var outcome = await ExecuteAsync(schedule, run, cancellationToken);
            if (outcome.IsFailure)
            {
                run.Status = RunStatuses.Failed;
                run.Error = $"{outcome.Error!.Code}: {outcome.Error.Message}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = RunStatuses.Failed;
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        run.FinishedAt = clock.UtcNow;
        await recorder.WriteAsync(run, cancellationToken);
        return new RunOutcome(run.Status, run.RowCount, run.FileName, JsonSerializer.Deserialize<List<string>>(run.Recipients, Json) ?? [], run.Error);
    }

    private async Task<Result> ExecuteAsync(ReportSchedule schedule, ReportRun run, CancellationToken cancellationToken)
    {
        var owner = await principals.ForMemberAsync(new MembershipId(schedule.OwnerMembershipId), cancellationToken);
        if (owner is null)
        {
            return Error.Conflict("schedule.owner_inactive", "The member who owns the schedule is no longer active.");
        }

        current.Principal = owner;
        var company = await companies.FindAsync(new CompanyId(schedule.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", schedule.CompanyId);
        }

        var today = clock.TodayIn(company.TimeZone);
        if (RelativePeriods.Resolve(schedule.Period, today) is not { } window)
        {
            return Error.Validation("schedule.period_invalid", $"'{schedule.Period}' is not a period.");
        }

        run.PeriodFrom = window.From;
        run.PeriodTo = window.To;
        var language = owner.Language;
        var name = ReportFiles.Label(schedule.Name.Values, language);
        var baseName = $"{Safe(name)}_{ReportFiles.Iso(window.From)}_{ReportFiles.Iso(window.To)}";
        byte[] bytes;
        string fileName;
        string contentType;
        if (schedule.Source == WidgetSources.Report)
        {
            var result = await reports.RunAsync(schedule.ReportId!.Value, new RunReportRequest(company.Id.Value, window.From, window.To), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            run.RowCount = result.Value.RowCount;
            if (schedule.OnlyIfRows && result.Value.RowCount == 0)
            {
                run.Status = RunStatuses.Skipped;
                return Result.Success();
            }

            if (schedule.Format == RunFormats.Pdf)
            {
                var printed = await pdf.PrintAsync(new PrintSubject(ReportPrintModel.DocumentType, null, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reportId"] = schedule.ReportId.Value.ToString(),
                    ["companyId"] = company.Id.Value.ToString(),
                    ["from"] = ReportFiles.Iso(window.From),
                    ["to"] = ReportFiles.Iso(window.To),
                }), PrintVariants.Standard, cancellationToken);
                if (printed.IsFailure)
                {
                    return printed.Error!;
                }

                (bytes, fileName, contentType) = (printed.Value.Content, baseName + ".pdf", "application/pdf");
            }
            else
            {
                var file = ReportFiles.FromQuery(result.Value, schedule.Format, language, baseName);
                (bytes, fileName, contentType) = (file.Bytes, file.FileName, file.ContentType);
            }
        }
        else
        {
            var result = await statements.RunAsync(new StatementRequest(company.Id.Value, schedule.LayoutId, schedule.LayoutCode, window.From, window.To, null, schedule.IncludeClosing), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            run.RowCount = ReportFiles.FigureCount(result.Value);
            if (schedule.OnlyIfRows && run.RowCount == 0)
            {
                run.Status = RunStatuses.Skipped;
                return Result.Success();
            }

            if (schedule.Format == RunFormats.Pdf)
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["companyId"] = company.Id.Value.ToString(),
                    ["from"] = ReportFiles.Iso(window.From),
                    ["to"] = ReportFiles.Iso(window.To),
                };
                if (schedule.LayoutId is { } layoutId)
                {
                    parameters["layoutId"] = layoutId.ToString();
                }
                else
                {
                    parameters["layoutCode"] = schedule.LayoutCode ?? string.Empty;
                }

                if (schedule.IncludeClosing is { } closing)
                {
                    parameters["includeClosing"] = closing ? "true" : "false";
                }

                var printed = await pdf.PrintAsync(new PrintSubject(FinancialStatementPrintModel.DocumentType, null, parameters), PrintVariants.Standard, cancellationToken);
                if (printed.IsFailure)
                {
                    return printed.Error!;
                }

                (bytes, fileName, contentType) = (printed.Value.Content, baseName + ".pdf", "application/pdf");
            }
            else
            {
                var file = ReportFiles.FromStatement(result.Value, schedule.Format, language, baseName);
                (bytes, fileName, contentType) = (file.Bytes, file.FileName, file.ContentType);
            }
        }

        var tenant = unitOfWork.Current.Context.TenantId.Value.ToString("N");
        run.StorageKey = $"reporting/{tenant}/runs/{run.Id:N}/{fileName}";
        using (var content = new MemoryStream(bytes, writable: false))
        {
            await storage.PutAsync(run.StorageKey, content, contentType, cancellationToken: cancellationToken);
        }

        run.FileName = fileName;
        run.ContentType = contentType;
        run.SizeBytes = bytes.LongLength;

        var sent = new List<string>();
        var period = $"{ReportFiles.Iso(window.From)} – {ReportFiles.Iso(window.To)}";
        foreach (var recipient in ScheduleService.Recipients(schedule))
        {
            string address;
            string recipientLanguage;
            string? toName = null;
            if (recipient.MembershipId is { } membershipId)
            {
                var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
                if (member is null || !member.IsActive)
                {
                    continue;
                }

                (address, recipientLanguage, toName) = (member.Email, member.Locale, member.DisplayName);
            }
            else
            {
                (address, recipientLanguage) = (recipient.Email ?? string.Empty, language);
            }

            if (address.Length == 0)
            {
                continue;
            }

            var arabic = recipientLanguage.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
            var title = ReportFiles.Label(schedule.Name.Values, recipientLanguage);
            var subject = $"{title} {period}";
            var text = arabic
                ? $"مرفق {title} للفترة {period} ({company.LegalName.Resolve("ar", "en")}).\n\nأُرسل وفق جدولة في Quicker."
                : $"Attached is {title} for {period} ({company.LegalName.Resolve("en", "ar")}).\n\nSent by a schedule in Quicker.";
            var html = $"<p dir=\"auto\">{System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal)}</p>";
            await email.SendAsync(new EmailMessage(address, subject, text, html, toName,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Quicker-Report-Run"] = run.Id.ToString() },
                [new EmailAttachment(fileName, contentType, bytes)]), cancellationToken);
            sent.Add(address);
        }

        run.Recipients = JsonSerializer.Serialize(sent, Json);
        run.Status = RunStatuses.Sent;
        return Result.Success();
    }

    private static string Safe(string name)
    {
        var chars = name.Trim().Select(static c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "report" : safe.Length > 60 ? safe[..60] : safe;
    }
}

/// <summary>Writes a run row in a transaction of its own, so the run is kept whatever the job's own unit of work does afterwards.</summary>
public sealed class RunRecorder(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, IUnitOfWorkAccessor unitOfWork)
{
    public async Task WriteAsync(ReportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var uow = await unitOfWorkFactory.BeginAsync(unitOfWork.Current.Context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        var existing = await db.Runs.SingleOrDefaultAsync(r => r.Id == run.Id, cancellationToken);
        if (existing is null)
        {
            db.Runs.Add(new ReportRun
            {
                Id = run.Id,
                ScheduleId = run.ScheduleId,
                CompanyId = run.CompanyId,
                Source = run.Source,
                ReportId = run.ReportId,
                LayoutId = run.LayoutId,
                LayoutCode = run.LayoutCode,
                RunAsMembershipId = run.RunAsMembershipId,
                Trigger = run.Trigger,
                JobId = run.JobId,
                StartedAt = run.StartedAt,
                Status = run.Status,
            });
        }
        else
        {
            existing.Status = run.Status;
            existing.FinishedAt = run.FinishedAt;
            existing.PeriodFrom = run.PeriodFrom;
            existing.PeriodTo = run.PeriodTo;
            existing.RowCount = run.RowCount;
            existing.FileName = run.FileName;
            existing.ContentType = run.ContentType;
            existing.StorageKey = run.StorageKey;
            existing.SizeBytes = run.SizeBytes;
            existing.Recipients = run.Recipients;
            existing.Error = run.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>
/// Recurring journal templates (roadmap 2.3): a cron schedule in the company's time zone, lines with fixed
/// amounts, percentages of a base amount, or accounts only (variable). Each due run generates a journal; it posts
/// at once unless the template requires review or its amounts are variable.
/// </summary>
public sealed class RecurringService(AccountingDbContext db, ICompanyDirectory companies, ManualJournalService journals, IAuditSink audit, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Modes = ["fixed", "variable", "percentage"];

    public async Task<Result<IReadOnlyList<RecurringTemplateSummary>>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var templates = await db.Set<RecurringTemplate>().Where(t => t.CompanyId == companyId).OrderBy(static t => t.Code).ToListAsync(cancellationToken);
        return templates.Select(Map).ToList();
    }

    public async Task<RecurringTemplateSummary?> GetAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var template = await db.Set<RecurringTemplate>().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        return template is null ? null : Map(template);
    }

    public async Task<Result<RecurringTemplateSummary>> CreateAsync(Guid companyId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var template = new RecurringTemplate { Id = Guid.CreateVersion7(), CompanyId = companyId, TimeZone = company.TimeZone, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(company, template, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Set<RecurringTemplate>().AnyAsync(t => t.CompanyId == companyId && t.Code == template.Code, cancellationToken))
        {
            return Error.Conflict("recurring_template.code_taken", $"Template '{template.Code}' already exists for this company.");
        }

        db.Set<RecurringTemplate>().Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return Map(template);
    }

    public async Task<Result<RecurringTemplateSummary>> UpdateAsync(Guid templateId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = await db.Set<RecurringTemplate>().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("recurring_template", templateId);
        }

        var company = (await companies.FindAsync(new CompanyId(template.CompanyId), cancellationToken))!;
        var applied = await ApplyAsync(company, template, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Set<RecurringTemplate>().AnyAsync(t => t.CompanyId == template.CompanyId && t.Code == template.Code && t.Id != templateId, cancellationToken))
        {
            return Error.Conflict("recurring_template.code_taken", $"Template '{template.Code}' already exists for this company.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(template);
    }

    private async Task<Result> ApplyAsync(CompanyInfo company, RecurringTemplate template, SaveRecurringTemplateRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.Code(request.Code, "recurring_template");
        var name = Validation.Name(request.Name, "recurring_template");
        var mode = Validation.OneOf(request.AmountMode, "recurring_template.amount_mode", Modes);
        var cron = CronExpression.Parse(request.Cron);
        if (code.IsFailure || name.IsFailure || mode.IsFailure || cron.IsFailure)
        {
            return code.Error ?? name.Error ?? mode.Error ?? cron.Error!;
        }

        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.");
        }

        var currency = await companies.FindCurrencyAsync(request.Currency?.Trim().ToUpperInvariant() ?? string.Empty, cancellationToken);
        if (currency is not { } tc)
        {
            return Error.Validation("recurring_template.currency_unknown", $"Currency '{request.Currency}' is not in the ISO 4217 list.");
        }

        if (request.Lines is null || request.Lines.Count < 2)
        {
            return Error.Validation("recurring_template.lines_required", "A template needs at least two lines.");
        }

        var codes = request.Lines.Select(static l => l.AccountCode?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        var accounts = await db.Accounts.Where(a => a.ChartId == chartId && codes.Contains(a.Code)).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var lines = new List<RecurringLineRequest>(request.Lines.Count);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (!accounts.TryGetValue(line.AccountCode?.Trim() ?? string.Empty, out var account) || account.IsHeader || !account.IsActive)
            {
                return Error.NotFound("account", line.AccountCode ?? string.Empty).WithWhy(("line", i + 1));
            }

            if (line.Debit < 0m || line.Credit < 0m || (line.Debit > 0m && line.Credit > 0m))
            {
                return Error.Validation("recurring_template.line_sides", $"Line {i + 1}: enter a debit or a credit, not both, never negative.").WithWhy(("line", i + 1));
            }

            if (mode.Value != "variable" && line.Debit == 0m && line.Credit == 0m)
            {
                return Error.Validation("recurring_template.line_amount", $"Line {i + 1}: an amount (or percentage) is required in {mode.Value} mode.").WithWhy(("line", i + 1));
            }

            if (mode.Value == "fixed" && (!new Money(line.Debit, tc).IsRoundedToMinorUnit || !new Money(line.Credit, tc).IsRoundedToMinorUnit))
            {
                return Error.Validation("recurring_template.amount_precision", $"Line {i + 1}: more decimals than {tc.Code} allows.").WithWhy(("line", i + 1));
            }

            lines.Add(line with { AccountCode = account.Code, Dimensions = line.Dimensions is null ? null : new Dictionary<string, Guid>(line.Dimensions.Select(static p => new KeyValuePair<string, Guid>(p.Key.Trim().ToUpperInvariant(), p.Value)), StringComparer.Ordinal) });
        }

        if (mode.Value != "variable" && lines.Sum(static l => l.Debit) != lines.Sum(static l => l.Credit))
        {
            return Error.Validation("recurring_template.unbalanced", mode.Value == "fixed" ? "The template's debits and credits differ." : "Debit and credit percentages must add up to the same share.")
                .WithWhy(("debit", lines.Sum(static l => l.Debit)), ("credit", lines.Sum(static l => l.Credit)));
        }

        if (mode.Value == "percentage" && request.BaseAmount is { } baseAmount && baseAmount <= 0m)
        {
            return Error.Validation("recurring_template.base_amount", "The base amount is positive.");
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(company.TimeZone);
        var startsOn = request.StartsOn ?? template.NextRunOn ?? clock.TodayIn(company.TimeZone);
        var after = new DateTimeOffset(startsOn.AddDays(-1).ToDateTime(TimeOnly.MaxValue), timeZone.GetUtcOffset(startsOn.ToDateTime(TimeOnly.MinValue)));
        var next = cron.Value.Next(after, timeZone);
        template.Code = code.Value.ToUpperInvariant();
        template.Name = name.Value;
        template.Cron = cron.Value.Text;
        template.TimeZone = company.TimeZone;
        template.NextRunOn = next is { } n && (request.EndsOn is null || DateOnly.FromDateTime(n.Date) <= request.EndsOn) ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(n, timeZone).Date) : null;
        template.EndsOn = request.EndsOn;
        template.AmountMode = mode.Value;
        template.BaseAmount = mode.Value == "percentage" ? request.BaseAmount : null;
        template.Currency = tc.Code;
        template.Lines = JsonSerializer.Serialize(lines, Json);
        template.Description = request.Description is null ? new LocalizedText() : new LocalizedText(request.Description);
        template.RequiresReview = request.RequiresReview || mode.Value == "variable";
        template.AutoReverse = request.AutoReverse;
        template.IsActive = request.IsActive;
        template.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Generates the journal of one run (the next due date unless a date is given) and advances the schedule.</summary>
    public async Task<Result<ManualJournalSummary>> GenerateAsync(Guid templateId, GenerateRecurringRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = await db.Set<RecurringTemplate>().SingleOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Error.NotFound("recurring_template", templateId);
        }

        if (!template.IsActive)
        {
            return Error.Conflict("recurring_template.inactive", $"Template '{template.Code}' is inactive.");
        }

        var company = (await companies.FindAsync(new CompanyId(template.CompanyId), cancellationToken))!;
        var runDate = request.RunDate ?? template.NextRunOn ?? clock.TodayIn(company.TimeZone);
        if (template.EndsOn is { } ends && runDate > ends)
        {
            return Error.Conflict("recurring_template.ended", $"Template '{template.Code}' ended on {ends:yyyy-MM-dd}.");
        }

        var currency = (await companies.FindCurrencyAsync(template.Currency, cancellationToken))!.Value;
        var policy = new RoundingPolicy(company.RoundingMode);
        var lines = JsonSerializer.Deserialize<List<RecurringLineRequest>>(template.Lines, Json) ?? [];
        var baseAmount = request.BaseAmount ?? template.BaseAmount;
        if (template.AmountMode == "percentage" && baseAmount is null)
        {
            return Error.Validation("recurring_template.base_amount_required", "A percentage template needs a base amount for the run.");
        }

        var journalLines = new List<JournalLineRequest>(lines.Count);
        decimal debitTotal = 0m, creditTotal = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var (debit, credit) = template.AmountMode switch
            {
                "fixed" => (line.Debit, line.Credit),
                "percentage" => (policy.Round(baseAmount!.Value * line.Debit / 100m, currency.MinorUnits), policy.Round(baseAmount.Value * line.Credit / 100m, currency.MinorUnits)),
                _ => (0m, 0m),
            };
            debitTotal += debit;
            creditTotal += credit;
            journalLines.Add(new JournalLineRequest(debit, credit, line.AccountCode, null, line.Dimensions, Description: line.Description));
        }

        // Percentage rounding may leave a minor-unit gap: the last credit line absorbs it so the journal balances.
        if (template.AmountMode == "percentage" && debitTotal != creditTotal)
        {
            var last = journalLines.FindLastIndex(static l => l.Credit > 0m);
            if (last >= 0)
            {
                journalLines[last] = journalLines[last] with { Credit = journalLines[last].Credit + (debitTotal - creditTotal) };
            }
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(template.TimeZone);
        var description = template.Description.IsEmpty ? template.Name : template.Description;
        var created = await journals.CreateAsync(template.CompanyId, new SaveJournalRequest(runDate, template.Currency, journalLines, JournalKinds.Recurring, null,
            description.Values, template.Code + " " + runDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), null, RateTypes.Spot, null, null,
            template.AutoReverse, template.AutoReverse ? NextRun(template, runDate, timeZone) ?? runDate.AddMonths(1) : null), template.Id, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var journal = created.Value;
        if (!template.RequiresReview && template.AmountMode != "variable")
        {
            var posted = await journals.PostAsync(journal.Id, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            journal = posted.Value;
        }

        template.LastGeneratedOn = runDate;
        var next = NextRun(template, runDate, timeZone);
        template.NextRunOn = next is { } n && (template.EndsOn is null || n <= template.EndsOn) ? n : null;
        template.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_recurring_template", template.Id, template.Code, "generated", After: new { runDate, journalId = journal.Id, journal.Status, nextRunOn = template.NextRunOn }), cancellationToken);
        return journal;
    }

    /// <summary>Every active template due on or before the date, one run per due date (catching up at most a year).</summary>
    public async Task<IReadOnlyList<RoutineOutcome>> GenerateDueAsync(Guid? companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var due = await db.Set<RecurringTemplate>()
            .Where(t => t.IsActive && t.NextRunOn != null && t.NextRunOn <= asOf && (companyId == null || t.CompanyId == companyId))
            .OrderBy(static t => t.Code)
            .Select(static t => t.Id)
            .ToListAsync(cancellationToken);
        var outcomes = new List<RoutineOutcome>();
        foreach (var templateId in due)
        {
            for (var guard = 0; guard < 366; guard++)
            {
                var template = await db.Set<RecurringTemplate>().SingleAsync(t => t.Id == templateId, cancellationToken);
                if (template.NextRunOn is not { } run || run > asOf)
                {
                    break;
                }

                var generated = await GenerateAsync(templateId, new GenerateRecurringRequest(run), cancellationToken);
                if (generated.IsFailure)
                {
                    outcomes.Add(new RoutineOutcome(templateId, RoutineOutcomes.Waiting, null, null, generated.Error!.Code));
                    break;
                }

                outcomes.Add(new RoutineOutcome(templateId, RoutineOutcomes.Generated, generated.Value.Id, generated.Value.Number, null));
            }
        }

        return outcomes;
    }

    private static DateOnly? NextRun(RecurringTemplate template, DateOnly after, TimeZoneInfo timeZone)
    {
        var cron = CronExpression.Parse(template.Cron);
        if (cron.IsFailure)
        {
            return null;
        }

        var from = new DateTimeOffset(after.ToDateTime(TimeOnly.MaxValue), timeZone.GetUtcOffset(after.ToDateTime(TimeOnly.MinValue)));
        var next = cron.Value.Next(from, timeZone);
        return next is { } n ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(n, timeZone).Date) : null;
    }

    private static RecurringTemplateSummary Map(RecurringTemplate t) => new(t.Id, t.CompanyId, t.Code, t.Name.Values, t.Cron, t.TimeZone, t.NextRunOn, t.EndsOn, t.AmountMode, t.BaseAmount, t.Currency,
        JsonSerializer.Deserialize<List<RecurringLineRequest>>(t.Lines, Json) ?? [], t.Description.Values, t.RequiresReview, t.AutoReverse, t.IsActive, t.LastGeneratedOn, t.UpdatedAt);
}

using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>
/// Deferral schedules (roadmap 2.3, POSTING_RULES §8): a prepayment, accrual or deferred revenue amortised over
/// fiscal periods to the minor unit (straight line or by days, the last line taking the remainder). Each line posts
/// on its period end through the engine: prepayment and accrual Dr target / Cr balance, deferred revenue the other way.
/// </summary>
public sealed class DeferralService(AccountingDbContext db, ICompanyDirectory companies, IFiscalPeriodResolver periods, IPostingService posting, IAuditSink audit, IClock clock)
{
    private static readonly string[] Kinds = ["prepayment", "accrual", "deferred_revenue"];
    private static readonly string[] Methods = ["straight_line", "daily"];

    public async Task<Result<IReadOnlyList<DeferralScheduleSummary>>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var schedules = await db.Set<DeferralSchedule>().Include(static s => s.Lines).Where(s => s.CompanyId == companyId).OrderByDescending(static s => s.StartsOn).ToListAsync(cancellationToken);
        var codes = await CodesAsync(schedules.SelectMany(static s => new[] { s.BalanceAccountId, s.TargetAccountId }), cancellationToken);
        return schedules.Select(s => Map(s, codes)).ToList();
    }

    public async Task<DeferralScheduleSummary?> GetAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await db.Set<DeferralSchedule>().Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        return schedule is null ? null : Map(schedule, await CodesAsync([schedule.BalanceAccountId, schedule.TargetAccountId], cancellationToken));
    }

    public async Task<Result<DeferralPreview>> PreviewAsync(Guid companyId, SaveDeferralRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(companyId, request, cancellationToken);
        return prepared.IsFailure ? prepared.Error! : new DeferralPreview(prepared.Value.Lines.Select(Map).ToList(), prepared.Value.TotalAmount);
    }

    public async Task<Result<DeferralScheduleSummary>> CreateAsync(Guid companyId, SaveDeferralRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(companyId, request, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        db.Set<DeferralSchedule>().Add(prepared.Value);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_deferral_schedule", prepared.Value.Id, prepared.Value.Kind, AuditActions.Created, After: new { prepared.Value.CompanyId, prepared.Value.Kind, prepared.Value.TotalAmount, prepared.Value.Currency, prepared.Value.Periods, prepared.Value.StartsOn }), cancellationToken);
        return (await GetAsync(prepared.Value.Id, cancellationToken))!;
    }

    private async Task<Result<DeferralSchedule>> PrepareAsync(Guid companyId, SaveDeferralRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.");
        }

        var kind = Validation.OneOf(request.Kind, "deferral.kind", Kinds);
        var method = Validation.OneOf(request.Method, "deferral.method", Methods);
        if (kind.IsFailure || method.IsFailure)
        {
            return kind.Error ?? method.Error!;
        }

        if (request.Periods is < 1 or > 120)
        {
            return Error.Validation("deferral.periods", "A schedule runs over 1 to 120 periods.");
        }

        var currency = await companies.FindCurrencyAsync(request.Currency?.Trim().ToUpperInvariant() ?? string.Empty, cancellationToken);
        if (currency is not { } tc)
        {
            return Error.Validation("deferral.currency_unknown", $"Currency '{request.Currency}' is not in the ISO 4217 list.");
        }

        if (request.TotalAmount <= 0m || !new Money(request.TotalAmount, tc).IsRoundedToMinorUnit)
        {
            return Error.Validation("deferral.total_amount", $"The total is a positive amount with at most {tc.MinorUnits} decimals.");
        }

        var balance = await db.Accounts.SingleOrDefaultAsync(a => a.ChartId == chartId && a.Code == request.BalanceAccountCode, cancellationToken);
        var target = await db.Accounts.SingleOrDefaultAsync(a => a.ChartId == chartId && a.Code == request.TargetAccountCode, cancellationToken);
        if (balance is null || balance.IsHeader || !balance.IsActive)
        {
            return Error.NotFound("account", request.BalanceAccountCode);
        }

        if (target is null || target.IsHeader || !target.IsActive)
        {
            return Error.NotFound("account", request.TargetAccountCode);
        }

        if (balance.IsControl || target.IsControl)
        {
            return Error.Validation("deferral.control_account", "Deferrals move balances between non-control accounts.");
        }

        var schedule = new DeferralSchedule
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            Kind = kind.Value,
            SourceDocumentType = request.SourceDocumentType?.Trim().ToLowerInvariant(),
            SourceLineId = request.SourceLineId,
            BalanceAccountId = balance.Id,
            TargetAccountId = target.Id,
            StartsOn = request.StartsOn,
            Periods = request.Periods,
            Method = method.Value,
            TotalAmount = request.TotalAmount,
            Currency = tc.Code,
            Dimensions = new Dictionary<string, Guid>((request.Dimensions ?? new Dictionary<string, Guid>(StringComparer.Ordinal)).Select(static p => new KeyValuePair<string, Guid>(p.Key.Trim().ToUpperInvariant(), p.Value)), StringComparer.Ordinal),
            Description = request.Description is null ? new LocalizedText() : new LocalizedText(request.Description),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

        // One line per period: the fiscal period when the calendar has it, else the calendar month.
        var spans = new List<(DateOnly Start, DateOnly End, Guid? PeriodId)>(request.Periods);
        var cursor = request.StartsOn;
        for (var i = 0; i < request.Periods; i++)
        {
            var resolved = await periods.ResolveAsync(company.Id, cursor, PostingModules.GeneralLedger, cancellationToken);
            var (start, end, periodId) = resolved.IsSuccess
                ? (i == 0 ? cursor : resolved.Value.Period.StartsOn, resolved.Value.Period.EndsOn, (Guid?)resolved.Value.Period.PeriodId)
                : (cursor, new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month)), null);
            spans.Add((start, end, periodId));
            cursor = end.AddDays(1);
        }

        var policy = new RoundingPolicy(company.RoundingMode);
        var amounts = Amortise(request.TotalAmount, spans.Select(static s => (s.Start, s.End)).ToList(), method.Value, tc.MinorUnits, policy);
        for (var i = 0; i < spans.Count; i++)
        {
            schedule.Lines.Add(new DeferralLine { ScheduleId = schedule.Id, Sequence = i + 1, FiscalPeriodId = spans[i].PeriodId, PostingDate = spans[i].End, Amount = amounts[i] });
        }

        return schedule;
    }

    /// <summary>Straight line: equal parts to the minor unit, remainder on the last line. Daily: by days in each span, remainder on the last line. The parts always add up to the total.</summary>
    public static IReadOnlyList<decimal> Amortise(decimal total, IReadOnlyList<(DateOnly Start, DateOnly End)> spans, string method, int minorUnits, RoundingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(policy);
        var amounts = new decimal[spans.Count];
        var allocated = 0m;
        if (method == "daily")
        {
            var totalDays = spans.Sum(static s => s.End.DayNumber - s.Start.DayNumber + 1);
            for (var i = 0; i < spans.Count - 1; i++)
            {
                var days = spans[i].End.DayNumber - spans[i].Start.DayNumber + 1;
                amounts[i] = policy.Round(total * days / totalDays, minorUnits);
                allocated += amounts[i];
            }
        }
        else
        {
            var part = policy.Round(total / spans.Count, minorUnits);
            for (var i = 0; i < spans.Count - 1; i++)
            {
                amounts[i] = part;
                allocated += part;
            }
        }

        amounts[^1] = total - allocated;
        return amounts;
    }

    /// <summary>Posts every planned line due on or before the date; a closed period leaves the line planned and reports why.</summary>
    public async Task<Result<IReadOnlyList<RoutineOutcome>>> PostDueAsync(Guid? scheduleId, Guid? companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var query = db.Set<DeferralSchedule>().Include(static s => s.Lines).Where(s => s.Status == "active");
        if (scheduleId is { } id)
        {
            query = query.Where(s => s.Id == id);
        }

        if (companyId is { } company)
        {
            query = query.Where(s => s.CompanyId == company);
        }

        var schedules = await query.OrderBy(static s => s.StartsOn).ToListAsync(cancellationToken);
        if (scheduleId is not null && schedules.Count == 0)
        {
            return Error.NotFound("deferral_schedule", scheduleId);
        }

        var outcomes = new List<RoutineOutcome>();
        foreach (var schedule in schedules)
        {
            var companyInfo = (await companies.FindAsync(new CompanyId(schedule.CompanyId), cancellationToken))!;
            foreach (var line in schedule.Lines.Where(l => l.Status == "planned" && l.PostingDate <= asOf).OrderBy(static l => l.Sequence))
            {
                if (line.Amount == 0m)
                {
                    line.Status = "posted";
                    continue;
                }

                var (debitAccount, creditAccount) = schedule.Kind == "deferred_revenue" ? (schedule.BalanceAccountId, schedule.TargetAccountId) : (schedule.TargetAccountId, schedule.BalanceAccountId);
                var description = schedule.Description.IsEmpty
                    ? LocalizedText.Bilingual($"{schedule.Kind.Replace('_', ' ')} {line.Sequence}/{schedule.Periods}", $"{schedule.Kind.Replace('_', ' ')} {line.Sequence}/{schedule.Periods}")
                    : schedule.Description;
                var posted = await posting.PostAsync(new PostingRequest(companyInfo.Id, "accounting", "deferral", schedule.Id, line.PostingDate, schedule.Currency,
                [
                    new PostingLine("Manual", line.Amount, null, debitAccount, schedule.Dimensions, Description: description),
                    new PostingLine("Manual", -line.Amount, null, creditAccount, schedule.Dimensions, Description: description),
                ], null, line.PostingDate, description, null, RateTypes.Spot, null, null, $"deferral:{schedule.Id:N}:{line.Sequence}", IsManual: true), cancellationToken);
                if (posted.IsFailure)
                {
                    outcomes.Add(new RoutineOutcome(schedule.Id, RoutineOutcomes.Waiting, null, null, posted.Error!.Code));
                    break;
                }

                line.JournalEntryId = posted.Value.EntryId;
                line.Status = "posted";
                outcomes.Add(new RoutineOutcome(schedule.Id, RoutineOutcomes.Posted, posted.Value.EntryId, posted.Value.Number, null));
            }

            if (schedule.Lines.All(static l => l.Status != "planned"))
            {
                schedule.Status = "completed";
            }

            schedule.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return outcomes;
    }

    public async Task<Result<DeferralScheduleSummary>> CancelAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await db.Set<DeferralSchedule>().Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return Error.NotFound("deferral_schedule", scheduleId);
        }

        if (schedule.Status != "active")
        {
            return Error.Conflict("deferral.not_active", $"A {schedule.Status} schedule cannot be cancelled.");
        }

        foreach (var line in schedule.Lines.Where(static l => l.Status == "planned"))
        {
            line.Status = "cancelled";
        }

        schedule.Status = "cancelled";
        schedule.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_deferral_schedule", schedule.Id, schedule.Kind, AuditActions.StateChanged, After: new { status = "cancelled", remaining = schedule.Lines.Count(static l => l.Status == "cancelled") }), cancellationToken);
        return (await GetAsync(schedule.Id, cancellationToken))!;
    }

    private async Task<Dictionary<Guid, string>> CodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Accounts.Where(a => list.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, static a => a.Code, cancellationToken);
    }

    private static DeferralLineSummary Map(DeferralLine l) => new(l.Sequence, l.FiscalPeriodId, l.PostingDate, l.Amount, l.Status, l.JournalEntryId);

    private static DeferralScheduleSummary Map(DeferralSchedule s, Dictionary<Guid, string> codes)
    {
        var posted = s.Lines.Where(static l => l.Status == "posted").Sum(static l => l.Amount);
        return new DeferralScheduleSummary(s.Id, s.CompanyId, s.Kind, s.SourceDocumentType, s.SourceLineId, s.BalanceAccountId, codes.GetValueOrDefault(s.BalanceAccountId, string.Empty), s.TargetAccountId,
            codes.GetValueOrDefault(s.TargetAccountId, string.Empty), s.StartsOn, s.Periods, s.Method, s.TotalAmount, s.Currency, s.Dimensions, s.Description.Values, s.Status, posted, s.TotalAmount - posted,
            s.Lines.OrderBy(static l => l.Sequence).Select(Map).ToList());
    }
}

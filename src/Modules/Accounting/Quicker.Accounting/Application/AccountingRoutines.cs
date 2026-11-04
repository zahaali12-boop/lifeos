using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>Who started a run of the routines: the platform's schedule, or a member running them now.</summary>
public sealed record RoutineRunner(string Trigger, Guid? UserId = null, string? Name = null)
{
    public static readonly RoutineRunner Schedule = new(RoutineTriggers.Schedule);

    public static RoutineRunner Manual(Guid userId, string name) => new(RoutineTriggers.Manual, userId, name);
}

/// <summary>
/// The daily accounting routines for one tenant: reverse the entries whose reversal date has come, generate the
/// recurring journals that are due, post the deferral lines that are due. Each item that cannot run (a closed
/// period, a missing rate) is reported as waiting and retried on the next run; nothing is skipped silently. Every run
/// is logged per company, in the same transaction as what it posted, so the log never claims work that rolled back.
/// </summary>
public sealed class AccountingRoutines(AccountingDbContext db, ICompanyDirectory companies, IPostingService posting, RecurringService recurring, DeferralService deferrals, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RoutineRunResult> RunAsync(Guid? companyId, DateOnly? asOf, RoutineRunner runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var date = asOf ?? clock.TodayIn("UTC");
        var reversals = new List<RoutineOutcome>();
        var due = await db.Set<JournalEntry>()
            .Where(e => e.AutoReverseOn != null && e.AutoReverseOn <= date && !e.IsReversal && (companyId == null || e.CompanyId == companyId))
            .Where(e => !db.Set<EntryLink>().Any(l => l.ToEntryId == e.Id && l.Relation == EntryRelations.Reverses))
            .OrderBy(static e => e.AutoReverseOn).ThenBy(static e => e.Number)
            .Select(static e => new { e.Id, e.AutoReverseOn })
            .ToListAsync(cancellationToken);
        foreach (var entry in due)
        {
            var reversed = await posting.ReverseAsync(entry.Id, entry.AutoReverseOn, "Automatic reversal", automatic: true, cancellationToken);
            reversals.Add(reversed.IsSuccess
                ? new RoutineOutcome(entry.Id, RoutineOutcomes.Posted, reversed.Value.EntryId, reversed.Value.Number, null)
                : new RoutineOutcome(entry.Id, RoutineOutcomes.Waiting, null, null, reversed.Error!.Code));
        }

        var generated = await recurring.GenerateDueAsync(companyId, date, cancellationToken);
        var posted = await deferrals.PostDueAsync(null, companyId, date, cancellationToken);
        var result = new RoutineRunResult(companyId, date, reversals, generated, posted.IsSuccess ? posted.Value : []);
        await LogAsync(result, runner, cancellationToken);
        return result;
    }

    /// <summary>The logged runs for a company, newest first.</summary>
    public async Task<IReadOnlyList<RoutineRunSummary>> RunsAsync(Guid companyId, int limit, CancellationToken cancellationToken)
    {
        var runs = await db.RoutineRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .OrderByDescending(static r => r.RanAt).ThenByDescending(static r => r.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return runs.Select(static r => new RoutineRunSummary(r.Id, r.CompanyId, r.AsOf, r.Trigger, r.RunBy, r.RunByName, r.RanAt, r.Posted, r.Waiting,
            JsonSerializer.Deserialize<List<RoutineRunItem>>(r.Items, Json) ?? [])).ToList();
    }

    /// <summary>
    /// One row per company the run covered (every company for a tenant-wide run, so a day with nothing due still shows
    /// that the routines ran), each with its own items: the reversed entry's number, the template's code, the schedule's kind.
    /// </summary>
    private async Task LogAsync(RoutineRunResult result, RoutineRunner runner, CancellationToken cancellationToken)
    {
        var entryIds = result.AutoReversals.Select(static o => o.TargetId).Distinct().ToList();
        var entries = await db.Set<JournalEntry>().AsNoTracking().Where(e => entryIds.Contains(e.Id)).Select(static e => new { e.Id, e.CompanyId, Ref = e.Number }).ToListAsync(cancellationToken);
        var templateIds = result.RecurringJournals.Select(static o => o.TargetId).Distinct().ToList();
        var templates = await db.Set<RecurringTemplate>().AsNoTracking().Where(t => templateIds.Contains(t.Id)).Select(static t => new { t.Id, t.CompanyId, Ref = t.Code }).ToListAsync(cancellationToken);
        var scheduleIds = result.DeferralPostings.Select(static o => o.TargetId).Distinct().ToList();
        var schedules = await db.Set<DeferralSchedule>().AsNoTracking().Where(d => scheduleIds.Contains(d.Id)).Select(static d => new { d.Id, d.CompanyId, Ref = d.Kind }).ToListAsync(cancellationToken);
        var targets = entries.Select(static e => (e.Id, e.CompanyId, e.Ref))
            .Concat(templates.Select(static t => (t.Id, t.CompanyId, t.Ref)))
            .Concat(schedules.Select(static d => (d.Id, d.CompanyId, d.Ref)))
            .GroupBy(static t => t.Id).ToDictionary(static g => g.Key, static g => (g.First().CompanyId, g.First().Ref));

        var items = new List<(Guid CompanyId, RoutineRunItem Item)>();
        void Add(string kind, IEnumerable<RoutineOutcome> outcomes)
        {
            foreach (var o in outcomes)
            {
                var (company, reference) = targets.TryGetValue(o.TargetId, out var target) ? target : (result.CompanyId ?? Guid.Empty, null);
                items.Add((company, new RoutineRunItem(kind, o.TargetId, reference, o.Outcome, o.ProducedId, o.ProducedNumber, o.Problem)));
            }
        }

        Add("reversal", result.AutoReversals);
        Add("recurring", result.RecurringJournals);
        Add("deferral", result.DeferralPostings);

        var covered = result.CompanyId is { } one ? [one] : (await companies.ListAsync(cancellationToken)).Select(static c => c.Id.Value).ToList();
        var now = clock.UtcNow;
        foreach (var company in covered.Union(items.Select(static i => i.CompanyId).Where(static c => c != Guid.Empty)))
        {
            var own = items.Where(i => i.CompanyId == company).Select(static i => i.Item).ToList();
            db.RoutineRuns.Add(new RoutineRun
            {
                Id = Guid.CreateVersion7(),
                CompanyId = company,
                AsOf = result.AsOf,
                Trigger = runner.Trigger,
                RunBy = runner.UserId,
                RunByName = runner.Name,
                RanAt = now,
                Posted = own.Count(static i => i.Outcome != RoutineOutcomes.Waiting),
                Waiting = own.Count(static i => i.Outcome == RoutineOutcomes.Waiting),
                Items = JsonSerializer.Serialize(own, Json),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The company's date for a routine started without one: today in its time zone.</summary>
    public async Task<DateOnly> TodayForAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        return clock.TodayIn(company?.TimeZone ?? "UTC");
    }
}

public sealed record AccountingDailyPayload(DateOnly? AsOf = null);

/// <summary>Platform schedule <c>accounting.daily</c>: the routines for every tenant, each in its own unit of work bound to the tenant as the system actor.</summary>
public sealed class AccountingDailyJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, AccountingRoutines routines) : IJobHandler<AccountingDailyPayload>
{
    public static string JobType => "accounting.daily_routines";

    public async Task<object?> ExecuteAsync(AccountingDailyPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId is not null)
        {
            // Bound to a tenant: the job runner already opened the unit of work.
            return await routines.RunAsync(null, payload?.AsOf, RoutineRunner.Schedule, cancellationToken);
        }

        var results = new List<RoutineRunResult>();
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            results.Add(await InTenantAsync(tenantId, (sp, ct) => sp.GetRequiredService<AccountingRoutines>().RunAsync(null, payload?.AsOf, RoutineRunner.Schedule, ct), cancellationToken));
        }

        return new { tenants = results.Count, reversals = results.Sum(static r => r.AutoReversals.Count), recurring = results.Sum(static r => r.RecurringJournals.Count), deferrals = results.Sum(static r => r.DeferralPostings.Count) };
    }

    private async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        return tenants.Where(static t => t.Status == "active").Select(static t => t.Id.Value).ToList();
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

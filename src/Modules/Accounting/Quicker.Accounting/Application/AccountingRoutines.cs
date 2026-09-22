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

/// <summary>
/// The daily accounting routines for one tenant: reverse the entries whose reversal date has come, generate the
/// recurring journals that are due, post the deferral lines that are due. Each item that cannot run (a closed
/// period, a missing rate) is reported as waiting and retried on the next run; nothing is skipped silently.
/// </summary>
public sealed class AccountingRoutines(AccountingDbContext db, ICompanyDirectory companies, IPostingService posting, RecurringService recurring, DeferralService deferrals, IClock clock)
{
    public async Task<RoutineRunResult> RunAsync(Guid? companyId, DateOnly? asOf, CancellationToken cancellationToken)
    {
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
        return new RoutineRunResult(companyId, date, reversals, generated, posted.IsSuccess ? posted.Value : []);
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
            return await routines.RunAsync(null, payload?.AsOf, cancellationToken);
        }

        var results = new List<RoutineRunResult>();
        foreach (var tenantId in await TenantIdsAsync(cancellationToken))
        {
            results.Add(await InTenantAsync(tenantId, (sp, ct) => sp.GetRequiredService<AccountingRoutines>().RunAsync(null, payload?.AsOf, ct), cancellationToken));
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

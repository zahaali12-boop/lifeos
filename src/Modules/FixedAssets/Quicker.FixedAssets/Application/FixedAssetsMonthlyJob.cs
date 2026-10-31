using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.FixedAssets.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.FixedAssets.Application;

/// <summary>What the monthly schedule passes: the month to depreciate through (its last day), else the month before today.</summary>
public sealed record FixedAssetsMonthlyPayload(DateOnly? Through = null);

/// <summary>Platform schedule <c>fixed_assets.monthly</c>: on the first of the month, every company's ledger book is depreciated through the month just ended, each tenant in its own unit of work.</summary>
public sealed class FixedAssetsMonthlyJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, IClock clock) : IJobHandler<FixedAssetsMonthlyPayload>
{
    public static string JobType => "fixed_assets.monthly_depreciation";

    public async Task<object?> ExecuteAsync(FixedAssetsMonthlyPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var today = clock.TodayIn("UTC");
        var through = payload?.Through ?? new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        if (context.TenantId is not null)
        {
            return await RunTenantAsync(scopeFactory.CreateAsyncScope().ServiceProvider, through, cancellationToken);
        }

        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        var results = new List<object>();
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            results.Add(await InTenantAsync(tenant.Id.Value, (sp, ct) => RunTenantAsync(sp, through, ct), cancellationToken));
        }

        return new { through, tenants = results.Count, results };
    }

    private static async Task<object> RunTenantAsync(IServiceProvider services, DateOnly through, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<FixedAssetsDbContext>();
        var depreciation = services.GetRequiredService<DepreciationService>();
        var posted = new List<object>();
        var skipped = 0;
        foreach (var book in await db.Books.AsNoTracking().Where(static b => b.PostsToGl && b.IsActive).ToListAsync(cancellationToken))
        {
            var run = await depreciation.PostRunAsync(new DepreciationRunRequest(book.CompanyId, through, book.Id), cancellationToken);
            if (run.IsSuccess)
            {
                posted.Add(new { run.Value.CompanyId, run.Value.Number, run.Value.AssetCount, run.Value.Total });
            }
            else
            {
                skipped++;
            }
        }

        return new { posted, skipped };
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

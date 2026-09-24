using Quicker.Kernel.Time;
using Quicker.Partners.Contracts;

namespace Quicker.Partners.Application;

/// <summary>
/// Customer 360 (roadmap 5.1, DOMAIN_MODEL §7): the partner record, its customer accounts, contacts and addresses, its
/// pipeline, its activities and every module's balances and documents with it, all within what the member may read.
/// The modules that hold money with the partner answer through <see cref="IPartnerActivitySource"/>.
/// </summary>
public sealed class Customer360Service(
    PartnerService partners,
    CustomerService customers,
    OpportunityService opportunities,
    CrmActivityService activities,
    IEnumerable<IPartnerActivitySource> sources,
    IClock clock)
{
    private const int RecentCount = 10;

    public async Task<Customer360?> GetAsync(Guid partnerId, CancellationToken cancellationToken)
    {
        var detail = await partners.GetAsync(partnerId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var yearAgo = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddYears(-1);
        var all = await opportunities.ListAsync(null, partnerId, null, null, null, null, null, cancellationToken);
        var open = all.Where(static o => o.Status == OpportunityOutcomes.Open).OrderBy(static o => o.ExpectedClose == null).ThenBy(static o => o.ExpectedClose).ToList();
        var closed = all.Where(o => o.Status != OpportunityOutcomes.Open && o.ClosedOn >= yearAgo).OrderByDescending(static o => o.ClosedOn).ToList();
        var won = closed.Count(static o => o.Status == OpportunityOutcomes.Won);
        var lost = closed.Count(static o => o.Status == OpportunityOutcomes.Lost);
        var pipeline = new Customer360Pipeline(open, OpportunityService.Totals(open), closed.Take(RecentCount).ToList(), won, lost, won + lost == 0 ? null : decimal.Round(won * 100m / (won + lost), 1));

        var list = await activities.ListAsync(partnerId, null, null, null, null, cancellationToken);
        var openActivities = list.Where(static a => a.Status == CrmActivityStatuses.Open).ToList();
        var recent = list.Where(static a => a.Status != CrmActivityStatuses.Open).OrderByDescending(static a => a.CompletedAt).Take(RecentCount).ToList();

        var companies = customers.ReadableCompanies()?.ToList();
        var panels = new List<PartnerActivityPanel>();
        foreach (var source in sources)
        {
            if (await source.ReadAsync(partnerId, companies, cancellationToken) is { } panel)
            {
                panels.Add(panel);
            }
        }

        return new Customer360(detail, pipeline, openActivities, recent, panels);
    }
}

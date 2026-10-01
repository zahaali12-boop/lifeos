using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Persistence;

namespace Quicker.Payables.Application;

/// <summary>The payables side of a partner in its 360 view: what the company owes it per company and currency, and its latest items.</summary>
public sealed class PayablesPartnerActivity(PayablesDbContext db, ICompanyDirectory companies, ICurrentPrincipal principal, IClock clock) : IPartnerActivitySource
{
    public const string Source = "payables";

    private const int LatestDocuments = 10;

    public async Task<PartnerActivityPanel?> ReadAsync(Guid partnerId, IReadOnlyCollection<Guid>? companyIds, CancellationToken cancellationToken = default)
    {
        var query = db.OpenItems.AsNoTracking().Where(i => i.PartnerId == partnerId && i.Status != "reversed");
        if (principal.Principal is { } current)
        {
            if (current.ScopesFor(PayablesPermissions.OpenItemRead) is not { } scopes)
            {
                return null;
            }

            if (scopes.CompanyIds.Count > 0)
            {
                var allowed = scopes.CompanyIds.ToList();
                query = query.Where(i => allowed.Contains(i.CompanyId));
            }
        }

        if (companyIds is not null)
        {
            var wanted = companyIds.ToList();
            query = query.Where(i => wanted.Contains(i.CompanyId));
        }

        var open = await query.Where(static i => i.RemainingTc != 0m).ToListAsync(cancellationToken);
        var latest = await query.OrderByDescending(static i => i.PostingDate).ThenByDescending(static i => i.CreatedAt).Take(LatestDocuments * 3).ToListAsync(cancellationToken);
        var today = new Dictionary<Guid, DateOnly>();
        foreach (var companyId in open.Select(static i => i.CompanyId).Distinct())
        {
            var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
            today[companyId] = company is null ? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime) : clock.TodayIn(company.TimeZone);
        }

        var balances = open
            .GroupBy(static i => (i.CompanyId, i.Currency))
            .OrderBy(static g => g.Key.CompanyId).ThenBy(static g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new PartnerBalance(
                Source, g.Key.CompanyId, PartnerBalanceSides.Payable, g.Key.Currency,
                g.Sum(static i => i.RemainingTc), g.Sum(static i => i.RemainingFc),
                g.Where(i => i.DueDate < today[g.Key.CompanyId]).Sum(static i => i.RemainingTc),
                g.Count(), g.Min(static i => i.DueDate)))
            .ToList();

        // Instalments of one document read as the document.
        var documents = latest
            .GroupBy(static i => (i.DocumentType, i.DocumentId))
            .Select(static g => new PartnerDocument(
                Source, g.Key.DocumentType, g.Key.DocumentId, g.First().DocumentNumber, g.First().CompanyId, g.First().DocumentDate,
                g.Where(static i => i.RemainingTc != 0m).Select(static i => (DateOnly?)i.DueDate).Min(), g.First().Currency,
                g.Sum(static i => i.OriginalTc), g.Sum(static i => i.RemainingTc),
                g.All(static i => i.Status == "settled") ? "settled" : g.Any(static i => i.Status == "partially_settled" || i.Status == "settled") ? "partially_settled" : "open"))
            .Take(LatestDocuments)
            .ToList();
        return new PartnerActivityPanel(Source, balances, documents);
    }
}

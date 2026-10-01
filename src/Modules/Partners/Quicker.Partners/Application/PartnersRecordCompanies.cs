using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>The company of each opportunity, so its comments, files and history follow the company scope of the reader.</summary>
public sealed class PartnersRecordCompanies(PartnersDbContext db) : IRecordCompanies
{
    public IReadOnlyCollection<string> EntityTypes { get; } = [OpportunityService.EntityType];

    public async Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.ToList();
        return await db.Opportunities.AsNoTracking().Where(o => list.Contains(o.Id)).ToDictionaryAsync(static o => o.Id, static o => o.CompanyId, cancellationToken);
    }
}

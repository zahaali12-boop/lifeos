using Microsoft.EntityFrameworkCore;
using Quicker.Tax.Contracts;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

public sealed class TaxDirectory(TaxDbContext db) : ITaxDirectory
{
    public async Task<TaxGroupInfo?> FindGroupAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Groups.AsNoTracking().Where(g => g.Id == id).Select(static g => new TaxGroupInfo(g.Id, g.Kind, g.Code, g.Name, g.IsActive)).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TaxCodeRef>> DescribeCodesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, TaxCodeRef>();
        }

        var wanted = ids.Distinct().ToArray();
        return await db.Codes.AsNoTracking()
            .Where(c => wanted.Contains(c.Id))
            .Select(static c => new TaxCodeRef(c.Id, c.Code, c.Name, c.Treatment, c.ExemptionReasonCode, c.ExemptionReason))
            .ToDictionaryAsync(static c => c.Id, cancellationToken);
    }
}

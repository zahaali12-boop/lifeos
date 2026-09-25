using Microsoft.EntityFrameworkCore;
using Quicker.Tax.Contracts;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

public sealed class TaxGroupDirectory(TaxDbContext db) : ITaxGroupDirectory
{
    public async Task<TaxGroupInfo?> FindGroupAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Groups.AsNoTracking().Where(g => g.Id == id).Select(static g => new TaxGroupInfo(g.Id, g.Kind, g.Code, g.Name, g.IsActive)).SingleOrDefaultAsync(cancellationToken);
}

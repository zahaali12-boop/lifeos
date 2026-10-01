using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>
/// Whether stock has moved, for the guards on costing methods (A-139): a company whose stock has moved keeps its
/// costing method and cost pool, as an item that has moved keeps the method it was costed by (Business Central
/// refuses the same change on an item with ledger entries). Changing either would re-value history without a
/// document; a method change needs a cut-over revaluation, not an edit.
/// </summary>
public sealed class StockActivity(InventoryDbContext db) : IStockActivity, ICompanyCostingGuard
{
    public async Task<IReadOnlyList<Guid>> ItemsWithMovementsAsync(IReadOnlyCollection<Guid> itemIds, Guid? companyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return [];
        }

        return await db.Entries.AsNoTracking()
            .Where(e => itemIds.Contains(e.ItemId) && (companyId == null || e.CompanyId == companyId))
            .Select(static e => e.ItemId).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<Error?> RefusalAsync(CompanyId companyId, CancellationToken cancellationToken = default)
    {
        var entries = await db.Entries.AsNoTracking().CountAsync(e => e.CompanyId == companyId.Value, cancellationToken);
        return entries == 0
            ? null
            : Error.Conflict("company.costing_locked", "The company's stock has moved under its costing method and cost pool; they cannot change now.").WithWhy(("stockLedgerEntries", entries));
    }
}

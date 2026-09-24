using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.Inventory.Application;

/// <summary>The company of stock documents and warehouses, so what hangs off them follows the member's company scopes.</summary>
internal sealed class InventoryRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["stock_adjustment"] = "app.inv_adjustments",
        ["stock_transfer"] = "app.inv_transfers",
        ["stock_count"] = "app.inv_counts",
        ["stock_assembly"] = "app.inv_assemblies",
        ["stock_revaluation"] = "app.inv_revaluations",
        ["warehouse"] = "app.inv_warehouses",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

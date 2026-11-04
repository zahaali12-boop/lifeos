using Quicker.Collaboration.Contracts;
using Quicker.Persistence;

namespace Quicker.FixedAssets.Application;

/// <summary>The company of assets and depreciation runs, so what hangs off them follows the member's company scopes.</summary>
internal sealed class FixedAssetsRecordCompanies(IUnitOfWorkAccessor unitOfWork) : IRecordCompanies
{
    private static readonly Dictionary<string, string> Tables = new(StringComparer.Ordinal)
    {
        ["fixed_asset"] = "app.fa_assets",
        ["depreciation_run"] = "app.fa_depreciation_runs",
        ["asset_book"] = "app.fa_books",
    };

    public IReadOnlyCollection<string> EntityTypes => Tables.Keys;

    public Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        unitOfWork.CompaniesAsync(Tables[entityType], ids, cancellationToken);
}

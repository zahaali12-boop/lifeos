namespace Quicker.Collaboration.Contracts;

/// <summary>
/// The permission that lets a member read records of one entity type. A module registers one per record type it owns
/// (<c>services.AddSingleton(new RecordReadPermission("purchase_order", "purchasing.order.read"))</c>), and the
/// comments, files, timeline and links of those records are then shown only to members who may read the record
/// itself. Several registrations for one type mean any of the permissions will do; a type nobody registered stays
/// open to the collaboration permissions alone.
/// </summary>
public sealed record RecordReadPermission(string EntityType, string Permission);

/// <summary>
/// The company each record of some entity types belongs to, answered by the module that owns them, so that what hangs
/// off a record (comments, files, timeline, links) is shown only to members whose read permission reaches the record's
/// company: a role limited to one company does not see the discussion of another company's orders. A module registers
/// one for its company-bound types (<c>services.AddScoped&lt;IRecordCompanies, PurchasingRecordCompanies&gt;()</c>);
/// types without one (items, partners) are shared by every company of the workspace.
/// </summary>
public interface IRecordCompanies
{
    /// <summary>The entity types this module answers for.</summary>
    IReadOnlyCollection<string> EntityTypes { get; }

    /// <summary>The company of each record found; ids of records that do not exist (or not in this workspace) are left out.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> CompaniesAsync(string entityType, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

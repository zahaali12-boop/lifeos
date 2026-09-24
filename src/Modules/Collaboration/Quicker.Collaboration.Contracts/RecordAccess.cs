namespace Quicker.Collaboration.Contracts;

/// <summary>
/// The permission that lets a member read records of one entity type. A module registers one per record type it owns
/// (<c>services.AddSingleton(new RecordReadPermission("purchase_order", "purchasing.order.read"))</c>), and the
/// comments, files, timeline and links of those records are then shown only to members who may read the record
/// itself. Several registrations for one type mean any of the permissions will do; a type nobody registered stays
/// open to the collaboration permissions alone.
/// </summary>
public sealed record RecordReadPermission(string EntityType, string Permission);

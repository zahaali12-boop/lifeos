using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Collaboration.Application;

/// <summary>
/// Whether the current member may see what is attached to a record (comments, files, timeline, links): the record's own
/// read permission, as its module registered it, held for the record's company when its module tells the company. A
/// role limited to some companies sees what hangs off their records only. Without a principal (system work) nothing is
/// withheld.
/// </summary>
public sealed class RecordAccess(IEnumerable<RecordReadPermission> registrations, IEnumerable<IRecordCompanies> companies, ICurrentPrincipal principal)
{
    private readonly Dictionary<string, string[]> _permissions = registrations
        .GroupBy(static r => r.EntityType, StringComparer.Ordinal)
        .ToDictionary(static g => g.Key, static g => g.Select(static r => r.Permission).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private readonly Dictionary<string, IRecordCompanies> _companies = companies
        .SelectMany(static c => c.EntityTypes.Select(type => (type, c)))
        .GroupBy(static x => x.type, StringComparer.Ordinal)
        .ToDictionary(static g => g.Key, static g => g.First().c, StringComparer.Ordinal);

    /// <summary>Whether the member may read records of the type at all (in some company).</summary>
    public bool MayRead(string entityType) =>
        principal.Principal is not { } current || !_permissions.TryGetValue(entityType, out var permissions) || permissions.Any(current.Has);

    /// <summary>Whether the member may read this record: its type, and its company when the owning module tells it.</summary>
    public async Task<bool> MayReadAsync(string entityType, Guid entityId, CancellationToken cancellationToken) =>
        MayRead(entityType) && (await ReadableAsync([(entityType, entityId)], cancellationToken)).Count == 1;

    /// <summary>
    /// Why what hangs off a record is withheld, or null when it may be read: a type the member may not read is refused by
    /// name (they know it from the screen they are on); a record in a company their permission does not reach answers
    /// "not found", so whether it exists there is not revealed.
    /// </summary>
    public async Task<Error?> CheckAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        if (!MayRead(entityType))
        {
            return Refusal(entityType);
        }

        return (await ReadableAsync([(entityType, entityId)], cancellationToken)).Count == 1 ? null : Error.NotFound(entityType, entityId);
    }

    /// <summary>The records among these the member may read (one lookup per entity type).</summary>
    public async Task<IReadOnlySet<(string EntityType, Guid EntityId)>> ReadableAsync(IEnumerable<(string EntityType, Guid EntityId)> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        var readable = new HashSet<(string EntityType, Guid EntityId)>();
        foreach (var group in records.Where(r => MayRead(r.EntityType)).GroupBy(static r => r.EntityType, StringComparer.Ordinal))
        {
            var ids = group.Select(static r => r.EntityId).Distinct().ToList();
            var permissions = _permissions.GetValueOrDefault(group.Key);
            if (principal.Principal is not { } current || permissions is null || !_companies.TryGetValue(group.Key, out var lookup))
            {
                readable.UnionWith(ids.Select(id => (group.Key, id)));
                continue;
            }

            var scopes = permissions.Select(current.ScopesFor).OfType<RecordScopes>().ToList();
            if (scopes.Any(static s => s.CompanyIds.Count == 0))
            {
                readable.UnionWith(ids.Select(id => (group.Key, id)));
                continue;
            }

            // A record the module does not know (never created, or removed) has no company to withhold it by.
            var found = await lookup.CompaniesAsync(group.Key, ids, cancellationToken);
            readable.UnionWith(ids.Where(id => !found.TryGetValue(id, out var companyId) || scopes.Any(s => s.AllowsCompany(companyId))).Select(id => (group.Key, id)));
        }

        return readable;
    }

    /// <summary>The refusal for a list or a new entry on records the member may not read (the type is named, no record is revealed).</summary>
    public Error Refusal(string entityType) =>
        Error.Forbidden("record.read_forbidden", $"You may not read {entityType} records, so their comments, files and history are not shown.")
            .WithWhy(("entityType", entityType), ("permissions", string.Join(", ", _permissions.GetValueOrDefault(entityType) ?? [])));
}

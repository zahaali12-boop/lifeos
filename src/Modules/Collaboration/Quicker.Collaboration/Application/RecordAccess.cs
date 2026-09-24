using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Collaboration.Application;

/// <summary>
/// Whether the current member may see what is attached to records of an entity type (comments, files, timeline,
/// links): the record's own read permission, as its module registered it. Without a principal (system work) nothing
/// is withheld.
/// </summary>
public sealed class RecordAccess(IEnumerable<RecordReadPermission> registrations, ICurrentPrincipal principal)
{
    private readonly Dictionary<string, string[]> _permissions = registrations
        .GroupBy(static r => r.EntityType, StringComparer.Ordinal)
        .ToDictionary(static g => g.Key, static g => g.Select(static r => r.Permission).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    public bool MayRead(string entityType) =>
        principal.Principal is not { } current || !_permissions.TryGetValue(entityType, out var permissions) || permissions.Any(current.Has);

    /// <summary>The refusal for a list or a new entry on records the member may not read (the type is named, no record is revealed).</summary>
    public Error Refusal(string entityType) =>
        Error.Forbidden("record.read_forbidden", $"You may not read {entityType} records, so their comments, files and history are not shown.")
            .WithWhy(("entityType", entityType), ("permissions", string.Join(", ", _permissions.GetValueOrDefault(entityType) ?? [])));
}

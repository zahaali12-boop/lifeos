using System.Collections.Concurrent;
using System.Text.Json;
using Quicker.Kernel.Results;

namespace Quicker.Collaboration.Contracts;

/// <summary>A table that carries custom-field values in a jsonb column; modules register their hosts at startup.</summary>
public sealed record CustomFieldHost(string EntityType, string Table, string Column);

public static class CustomFieldHosts
{
    private static readonly ConcurrentDictionary<string, CustomFieldHost> Registry = new(StringComparer.Ordinal);

    public static void Register(CustomFieldHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Registry[host.EntityType] = host;
    }

    public static CustomFieldHost? Find(string entityType) => Registry.GetValueOrDefault(entityType);

    public static IReadOnlyCollection<string> EntityTypes => Registry.Keys.Order(StringComparer.Ordinal).ToList();
}

/// <summary>Checks a record's custom-field values against the tenant's definitions and returns them normalised as JSON.</summary>
public interface ICustomFieldValidator
{
    Task<Result<string>> ValidateAsync(string entityType, JsonElement? values, CancellationToken cancellationToken = default);
}

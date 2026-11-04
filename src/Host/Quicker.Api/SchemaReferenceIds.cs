using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;

namespace Quicker.Api;

/// <summary>
/// Schema ids for the generated contract: the framework's default (the type's simple name, generics folded in), with
/// one guard the default lacks. Two request or response types with the same simple name in different modules would
/// silently share one schema, and the generated clients would type one module's endpoints with the other's shape
/// (the item and account categories both had a <c>CategorySummary</c> once). Such a collision now fails the document,
/// and with it the build that commits the contract.
/// </summary>
public static class SchemaReferenceIds
{
    private static readonly ConcurrentDictionary<string, Type> Owners = new(StringComparer.Ordinal);

    public static string? Create(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var id = OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
        if (id is null)
        {
            return null;
        }

        // A nullable value type shares its underlying type's schema by design (int and int? are one schema).
        var type = Nullable.GetUnderlyingType(typeInfo.Type) ?? typeInfo.Type;
        var owner = Owners.GetOrAdd(id, type);
        if (owner != type)
        {
            throw new InvalidOperationException($"OpenAPI schema id '{id}' is claimed by both {owner.FullName} and {type.FullName}; rename one of them so every schema names one type.");
        }

        return id;
    }
}

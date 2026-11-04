using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;

namespace Quicker.Persistence.EntityFramework;

/// <summary>Maps a strongly typed id to its uuid column.</summary>
public sealed class EntityIdConverter<TId>() : ValueConverter<TId, Guid>(
    static id => id.Value,
    static value => (TId)Activator.CreateInstance(typeof(TId), value)!)
    where TId : struct, IEntityId
{
}

/// <summary>Maps <see cref="LocalizedText"/> to a jsonb language map.</summary>
public sealed class LocalizedTextConverter() : ValueConverter<LocalizedText, string>(
    static text => JsonSerializer.Serialize(text.Values, JsonOptions),
    static json => new LocalizedText(JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal)))
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

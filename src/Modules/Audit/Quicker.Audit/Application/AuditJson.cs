using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Audit.Application;

/// <summary>JSON shaping for audit payloads: value conversion, secret redaction and field-level diffs.</summary>
public static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "key", "apikey", "privatekey", "recoverycodes", "otp", "pin", "clientsecret",
        "secretenc", "keyhash", "tokenhash", "passwordhash", "refreshtoken", "accesstoken", "credential", "credentialid",
    };

    private static readonly string[] SensitiveSuffixes = ["password", "secret", "token", "hash", "secretenc"];

    /// <summary>True for property names that must never carry a value into the log, whatever the caller passed.</summary>
    public static bool IsSensitive(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var compact = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return SensitiveNames.Contains(compact) || SensitiveSuffixes.Any(suffix => compact.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Converts a CLR value (entity property, anonymous object, DTO) to a JSON node with the log's conventions.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonSerializer.SerializeToNode(element, Options),
        LocalizedText text => JsonSerializer.SerializeToNode(text.Values, Options),
        IEntityId id => JsonValue.Create(id.Value),
        IPAddress ip => JsonValue.Create(ip.ToString()),
        IEnumerable<IPAddress> ips => new JsonArray(ips.Select(static ip => (JsonNode?)JsonValue.Create(ip.ToString())).ToArray()),
        _ => JsonSerializer.SerializeToNode(value, Options),
    };

    /// <summary>Replaces the value of every sensitive property, at any depth, with the redaction marker. Mutates and returns the node.</summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(static p => p.Key).ToList())
                {
                    if (IsSensitive(name))
                    {
                        if (obj[name] is not null)
                        {
                            obj[name] = AuditAnnotations.RedactedValue;
                        }
                    }
                    else
                    {
                        Redact(obj[name]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Redact(item);
                }

                break;
        }

        return node;
    }

    /// <summary>Top-level field differences between two object snapshots: <c>{ field: { old, new } }</c>; null when there is nothing to compare.</summary>
    public static JsonObject? Diff(JsonNode? before, JsonNode? after)
    {
        if (before is not JsonObject b || after is not JsonObject a)
        {
            return null;
        }

        var diff = new JsonObject();
        foreach (var key in b.Select(static p => p.Key).Union(a.Select(static p => p.Key), StringComparer.Ordinal))
        {
            var oldValue = b[key];
            var newValue = a[key];
            if (!JsonNode.DeepEquals(oldValue, newValue))
            {
                diff[key] = new JsonObject { ["old"] = oldValue?.DeepClone(), ["new"] = newValue?.DeepClone() };
            }
        }

        return diff.Count == 0 ? null : diff;
    }

    public static string? Serialize(JsonNode? node) => node?.ToJsonString(Options);

    public static JsonElement? Parse(string? json) => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json, Options);
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Collaboration.Application;

public sealed record CustomFieldOption(string Value, IReadOnlyDictionary<string, string> Label);

/// <summary>Type-specific rules: number range, text length and pattern, the entity type a reference points at.</summary>
public sealed record CustomFieldRules(decimal? Min = null, decimal? Max = null, int? MaxLength = null, string? Pattern = null, string? ReferenceType = null);

public sealed record SaveCustomFieldRequest(
    string EntityType,
    string Key,
    IReadOnlyDictionary<string, string> Label,
    string Type,
    bool Required = false,
    IReadOnlyList<CustomFieldOption>? Options = null,
    CustomFieldRules? Rules = null,
    bool Indexed = false,
    int Position = 0,
    bool Active = true,
    IReadOnlyDictionary<string, string>? Description = null,
    JsonElement? DefaultValue = null);

/// <summary>The record types that carry custom fields, in code order.</summary>
public sealed record CustomFieldHostList(IReadOnlyCollection<string> EntityTypes);

public sealed record CustomFieldView(Guid Id, string EntityType, string Key, IReadOnlyDictionary<string, string> Label, IReadOnlyDictionary<string, string> Description, string Type, bool Required, IReadOnlyList<CustomFieldOption> Options, CustomFieldRules Rules, bool Indexed, int Position, bool Active, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, JsonElement? DefaultValue = null);

public static class CustomFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Select = "select";
    public const string MultiSelect = "multi_select";
    public const string Reference = "reference";

    public static readonly string[] All = [Text, Number, Date, Boolean, Select, MultiSelect, Reference];
}

/// <summary>
/// Creates and drops the expression index a definition asks for on its host table. DDL runs on the owner connection
/// outside the request transaction (indexes are per table, shared by tenants, and carry tenant_id first).
/// </summary>
public sealed class CustomFieldIndexer(DbOptions options) : IDisposable
{
    private readonly Lazy<NpgsqlDataSource> _owner = new(() => DataSources.ForOwner(options));

    public static string IndexName(CustomFieldHost host, string key) => $"{host.Table.Split('.')[^1]}_cf_{key}_idx";

    public async Task EnsureAsync(CustomFieldHost host, string key, bool indexed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!IsKey(key))
        {
            throw new ArgumentException($"'{key}' is not a custom-field key.", nameof(key));
        }

        var schema = host.Table.Split('.')[0];
        var name = IndexName(host, key);
        var sql = indexed
            ? $"CREATE INDEX IF NOT EXISTS \"{name}\" ON {host.Table} (tenant_id, ({Quicker.Persistence.EntityFramework.JsonFunctions.IndexExpression(host.Column, key)}))" // the filter language translates cf.<key> to the same expression, so it uses the index
            : $"DROP INDEX IF EXISTS {schema}.\"{name}\"";
        await using var connection = await _owner.Value.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public static bool IsKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= 40 && char.IsAsciiLetterLower(key[0]) && key.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public void Dispose()
    {
        if (_owner.IsValueCreated)
        {
            _owner.Value.Dispose();
        }
    }
}

/// <summary>Custom-field definitions per host entity type, the validator every host calls before saving, and the JSON Schema the API exposes.</summary>
public sealed class CustomFieldService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, CustomFieldIndexer indexer, IClock clock) : ICustomFieldValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(200);

    // ------------------------------------------------------------------ definitions

    public async Task<Result<IReadOnlyList<CustomFieldView>>> ListAsync(string? entityType, CancellationToken cancellationToken)
    {
        var type = entityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type))
        {
            return Error.Validation("custom_field.entity_invalid", "entityType is a lower-case name such as company.");
        }

        return (await db.CustomFields.Where(f => f.EntityType == type).OrderBy(static f => f.Position).ThenBy(static f => f.Key).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<CustomFieldView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        return field is null ? null : Map(field);
    }

    public async Task<Result<CustomFieldView>> CreateAsync(SaveCustomFieldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var field = new CustomFieldDefinition { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(field, request, isNew: true, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.CustomFields.Add(field);
        await db.SaveChangesAsync(cancellationToken);
        ScheduleIndex(field);
        return Map(field);
    }

    public async Task<Result<CustomFieldView>> UpdateAsync(Guid id, SaveCustomFieldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return Error.NotFound("custom_field", id);
        }

        var applied = await ApplyAsync(field, request, isNew: false, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        field.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ScheduleIndex(field);
        return Map(field);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
        {
            return Error.NotFound("custom_field", id);
        }

        db.CustomFields.Remove(field);
        await db.SaveChangesAsync(cancellationToken);
        field.Indexed = false;
        ScheduleIndex(field);
        return Result.Success();
    }

    private void ScheduleIndex(CustomFieldDefinition field)
    {
        var host = CustomFieldHosts.Find(field.EntityType)!;
        var (key, indexed) = (field.Key, field.Indexed);
        unitOfWork.Current.BeforeCommit(ct => indexer.EnsureAsync(host, key, indexed, ct));
    }

    private async Task<Result> ApplyAsync(CustomFieldDefinition field, SaveCustomFieldRequest request, bool isNew, CancellationToken cancellationToken)
    {
        var type = request.EntityType?.Trim() ?? string.Empty;
        if (CustomFieldHosts.Find(type) is null)
        {
            return Error.Validation("custom_field.entity_unsupported", "Custom fields can be defined for: " + string.Join(", ", CustomFieldHosts.EntityTypes) + ".").WithWhy(("supported", CustomFieldHosts.EntityTypes));
        }

        var key = request.Key?.Trim() ?? string.Empty;
        if (!CustomFieldIndexer.IsKey(key))
        {
            return Error.Validation("custom_field.key_invalid", "A key starts with a letter and has up to 40 lower-case letters, digits or underscores.");
        }

        var label = new LocalizedText((request.Label ?? new Dictionary<string, string>(StringComparer.Ordinal)).Where(static kv => !string.IsNullOrWhiteSpace(kv.Value)));
        if (label.IsEmpty)
        {
            return Error.Validation("custom_field.label_required", "A label in at least one language is required.");
        }

        var fieldType = request.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!CustomFieldTypes.All.Contains(fieldType, StringComparer.Ordinal))
        {
            return Error.Validation("custom_field.type_invalid", "The type is one of: " + string.Join(", ", CustomFieldTypes.All) + ".");
        }

        if (!isNew && (field.EntityType != type || field.Key != key || field.Type != fieldType))
        {
            return Error.Conflict("custom_field.identity_locked", "Entity type, key and type cannot change once values may exist; create a new field instead.");
        }

        var options = (request.Options ?? []).Select(o => new CustomFieldOption(o.Value?.Trim() ?? string.Empty, o.Label ?? new Dictionary<string, string>(StringComparer.Ordinal))).ToList();
        if (fieldType is CustomFieldTypes.Select or CustomFieldTypes.MultiSelect)
        {
            if (options.Count == 0 || options.Any(static o => o.Value.Length is 0 or > 100) || options.Select(static o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                return Error.Validation("custom_field.options_invalid", "Select fields need distinct option values of up to 100 characters.");
            }
        }
        else if (options.Count > 0)
        {
            return Error.Validation("custom_field.options_invalid", "Only select and multi_select fields have options.");
        }

        var rules = request.Rules ?? new CustomFieldRules();
        if (rules.Min is { } min && rules.Max is { } max && min > max)
        {
            return Error.Validation("custom_field.rules_invalid", "min cannot exceed max.");
        }

        if (rules.Pattern is { } pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
            }
            catch (ArgumentException)
            {
                return Error.Validation("custom_field.rules_invalid", "pattern is not a valid regular expression.");
            }
        }

        if (fieldType == CustomFieldTypes.Reference && !EntityTypes.IsValid(rules.ReferenceType))
        {
            return Error.Validation("custom_field.rules_invalid", "A reference field names the entity type it points at (rules.referenceType).");
        }

        if (await db.CustomFields.AnyAsync(f => f.EntityType == type && f.Key == key && f.Id != field.Id, cancellationToken))
        {
            return Error.Conflict("custom_field.key_taken", $"{type} already has a custom field '{key}'.");
        }

        // A default must be a value the field accepts; it is kept as the validator would keep it.
        string? defaultValue = null;
        if (request.DefaultValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given)
        {
            var probe = new CustomFieldDefinition { Key = key, Type = fieldType, Options = JsonSerializer.Serialize(options, Json), Rules = JsonSerializer.Serialize(rules, Json) };
            var normalized = Normalize(probe, given);
            if (normalized.IsFailure)
            {
                return Error.Validation("custom_field.default_invalid", "The default value is not a value this field accepts: " + normalized.Error!.Message).WithWhy(("field", key));
            }

            defaultValue = normalized.Value?.ToJsonString(Json);
        }

        field.EntityType = type;
        field.Key = key;
        field.Label = label;
        field.Description = new LocalizedText((request.Description ?? new Dictionary<string, string>(StringComparer.Ordinal)).Where(static kv => !string.IsNullOrWhiteSpace(kv.Value)));
        field.Type = fieldType;
        field.Required = request.Required;
        field.Options = JsonSerializer.Serialize(options, Json);
        field.Rules = JsonSerializer.Serialize(rules, Json);
        field.Indexed = request.Indexed;
        field.Position = request.Position;
        field.Active = request.Active;
        field.DefaultValue = defaultValue;
        return Result.Success();
    }

    // ------------------------------------------------------------------ validation

    public async Task<Result<string>> ValidateAsync(string entityType, JsonElement? values, CancellationToken cancellationToken = default)
    {
        var definitions = await db.CustomFields.Where(f => f.EntityType == entityType && f.Active).ToListAsync(cancellationToken);
        var given = values is { ValueKind: JsonValueKind.Object } v ? v : (JsonElement?)null;
        if (values is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            return Error.Validation("custom_field.values_invalid", "customFields is an object keyed by field.");
        }

        var known = definitions.Select(static d => d.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = given?.EnumerateObject().Select(static p => p.Name).Where(name => !known.Contains(name)).ToList() ?? [];
        if (unknown.Count > 0)
        {
            return Error.Validation("custom_field.unknown", $"Unknown custom field(s) for {entityType}: {string.Join(", ", unknown)}.").WithWhy(("fields", unknown));
        }

        var result = new JsonObject();
        foreach (var definition in definitions.OrderBy(static d => d.Position).ThenBy(static d => d.Key, StringComparer.Ordinal))
        {
            var present = given is { } g && g.TryGetProperty(definition.Key, out var raw) && raw.ValueKind is not JsonValueKind.Null;
            if (!present && definition.DefaultValue is { } fallback)
            {
                result[definition.Key] = JsonNode.Parse(fallback);
                continue;
            }

            if (!present)
            {
                if (definition.Required)
                {
                    return Error.Validation("custom_field.required", $"Custom field '{definition.Key}' is required.").WithWhy(("field", definition.Key));
                }

                continue;
            }

            var value = given!.Value.GetProperty(definition.Key);
            var normalized = Normalize(definition, value);
            if (normalized.IsFailure)
            {
                return normalized.Error!;
            }

            result[definition.Key] = normalized.Value;
        }

        return result.ToJsonString(Json);
    }

    private static Result<JsonNode?> Normalize(CustomFieldDefinition definition, JsonElement value)
    {
        var rules = JsonSerializer.Deserialize<CustomFieldRules>(definition.Rules, Json) ?? new CustomFieldRules();
        Error Invalid(string expectation) => Error.Validation("custom_field.value_invalid", $"Custom field '{definition.Key}' expects {expectation}.").WithWhy(("field", definition.Key), ("type", definition.Type));
        switch (definition.Type)
        {
            case CustomFieldTypes.Text:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return Invalid("text");
                }

                var text = value.GetString()!;
                if (rules.MaxLength is { } maxLength && text.Length > maxLength)
                {
                    return Invalid($"at most {maxLength} characters");
                }

                if (rules.Pattern is { } pattern && !Regex.IsMatch(text, pattern, RegexOptions.None, PatternTimeout))
                {
                    return Invalid($"a value matching {pattern}");
                }

                return JsonValue.Create(text);
            case CustomFieldTypes.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                {
                    return Invalid("a number");
                }

                if ((rules.Min is { } min && number < min) || (rules.Max is { } max && number > max))
                {
                    return Invalid($"a number between {rules.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {rules.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞"}");
                }

                return JsonValue.Create(number);
            case CustomFieldTypes.Date:
                if (value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return Invalid("a date as yyyy-MM-dd");
                }

                return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case CustomFieldTypes.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValue.Create(value.GetBoolean()) : Invalid("true or false");
            case CustomFieldTypes.Select:
                var allowed = OptionValues(definition);
                return value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()!) ? JsonValue.Create(value.GetString()) : Invalid("one of: " + string.Join(", ", allowed));
            case CustomFieldTypes.MultiSelect:
                var allowedMany = OptionValues(definition);
                if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || !allowedMany.Contains(e.GetString()!)))
                {
                    return Invalid("a list of: " + string.Join(", ", allowedMany));
                }

                return new JsonArray(value.EnumerateArray().Select(static e => e.GetString()).Distinct(StringComparer.Ordinal).Select(static s => (JsonNode?)JsonValue.Create(s)).ToArray());
            case CustomFieldTypes.Reference:
                return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? JsonValue.Create(id.ToString()) : Invalid($"the id of a {rules.ReferenceType}");
            default:
                return Invalid("a supported type");
        }
    }

    private static HashSet<string> OptionValues(CustomFieldDefinition definition) =>
        (JsonSerializer.Deserialize<List<CustomFieldOption>>(definition.Options, Json) ?? []).Select(static o => o.Value).ToHashSet(StringComparer.Ordinal);

    // ------------------------------------------------------------------ schema

    /// <summary>A JSON Schema (draft 2020-12) object for the entity's active custom fields, so clients validate and render forms from it.</summary>
    public async Task<Result<JsonElement>> SchemaAsync(string? entityType, CancellationToken cancellationToken)
    {
        var listed = await ListAsync(entityType, cancellationToken);
        if (listed.IsFailure)
        {
            return listed.Error!;
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in listed.Value.Where(static f => f.Active))
        {
            var property = new JsonObject { ["title"] = field.Label.GetValueOrDefault("en") ?? field.Label.Values.FirstOrDefault() ?? field.Key, ["x-label"] = JsonSerializer.SerializeToNode(field.Label, Json), ["x-fieldType"] = field.Type };
            switch (field.Type)
            {
                case CustomFieldTypes.Text:
                    property["type"] = "string";
                    if (field.Rules.MaxLength is { } maxLength)
                    {
                        property["maxLength"] = maxLength;
                    }

                    if (field.Rules.Pattern is { } pattern)
                    {
                        property["pattern"] = pattern;
                    }

                    break;
                case CustomFieldTypes.Number:
                    property["type"] = "number";
                    if (field.Rules.Min is { } min)
                    {
                        property["minimum"] = min;
                    }

                    if (field.Rules.Max is { } max)
                    {
                        property["maximum"] = max;
                    }

                    break;
                case CustomFieldTypes.Date:
                    property["type"] = "string";
                    property["format"] = "date";
                    break;
                case CustomFieldTypes.Boolean:
                    property["type"] = "boolean";
                    break;
                case CustomFieldTypes.Select:
                    property["type"] = "string";
                    property["enum"] = new JsonArray(field.Options.Select(static o => (JsonNode?)JsonValue.Create(o.Value)).ToArray());
                    break;
                case CustomFieldTypes.MultiSelect:
                    property["type"] = "array";
                    property["uniqueItems"] = true;
                    property["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(field.Options.Select(static o => (JsonNode?)JsonValue.Create(o.Value)).ToArray()) };
                    break;
                case CustomFieldTypes.Reference:
                    property["type"] = "string";
                    property["format"] = "uuid";
                    property["x-referenceType"] = field.Rules.ReferenceType;
                    break;
                default:
                    break;
            }

            // A field with a default is filled by the server, so a client need not send it.
            if (field.DefaultValue is { } fallback)
            {
                property["default"] = JsonNode.Parse(fallback.GetRawText());
            }

            properties[field.Key] = property;
            if (field.Required && field.DefaultValue is null)
            {
                required.Add(field.Key);
            }
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = $"{entityType} custom fields",
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(schema, Json);
    }

    private static CustomFieldView Map(CustomFieldDefinition f) => new(
        f.Id, f.EntityType, f.Key, f.Label.Values, f.Description.Values, f.Type, f.Required,
        JsonSerializer.Deserialize<List<CustomFieldOption>>(f.Options, Json) ?? [],
        JsonSerializer.Deserialize<CustomFieldRules>(f.Rules, Json) ?? new CustomFieldRules(),
        f.Indexed, f.Position, f.Active, f.CreatedAt, f.UpdatedAt,
        f.DefaultValue is null ? null : JsonDocument.Parse(f.DefaultValue).RootElement.Clone());
}

namespace Quicker.Kernel.Text;

/// <summary>
/// A bilingual (or multilingual) text value stored as a language→text map (ADR-0027). Resolution falls back
/// requested → tenant default → any available language, and never returns null for a non-empty map.
/// </summary>
public sealed class LocalizedText : IEquatable<LocalizedText>
{
    private readonly SortedDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public LocalizedText()
    {
    }

    public LocalizedText(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (language, text) in values)
        {
            Set(language, text);
        }
    }

    public static LocalizedText Of(string language, string text) => new LocalizedText().Set(language, text);

    public static LocalizedText Bilingual(string english, string arabic) => Of("en", english).Set("ar", arabic);

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool IsEmpty => _values.Count == 0;

    public LocalizedText Set(string language, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(text);
        _values[NormalizeLanguage(language)] = text;
        return this;
    }

    public string? Get(string language) => _values.TryGetValue(NormalizeLanguage(language), out var text) ? text : null;

    /// <summary>Resolves the best text for a language with the documented fallback chain.</summary>
    public string Resolve(string language, string defaultLanguage = "en")
    {
        if (_values.Count == 0)
        {
            return string.Empty;
        }

        return Get(language) ?? Get(defaultLanguage) ?? _values.Values.First();
    }

    public bool Equals(LocalizedText? other) =>
        other is not null && _values.Count == other._values.Count &&
        _values.All(kv => other._values.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as LocalizedText);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Resolve("en");

    private static string NormalizeLanguage(string language)
    {
        var trimmed = language.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        return (dash > 0 ? trimmed[..dash] : trimmed).ToLowerInvariant();
    }
}

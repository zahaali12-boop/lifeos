using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Payroll.Application;

/// <summary>Input rules shared by the payroll services: codes, bilingual names, enumerations.</summary>
internal static class Validation
{
    public static Result<string> UpperCode(string? value, string field)
    {
        var code = value?.Trim() ?? string.Empty;
        var valid = code.Length is >= 1 and <= 32
            && char.IsAsciiLetterOrDigit(code[0])
            && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        return valid
            ? code.ToUpperInvariant()
            : Error.Validation($"{field}.code_invalid", "Codes are 1–32 letters, digits, '_', '.' or '-'.");
    }

    public static Result<LocalizedText> Name(IReadOnlyDictionary<string, string>? values, string field)
    {
        if (values is null || values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"{field}.name_required", "A name in at least one language is required.");
        }

        return Text(values);
    }

    public static LocalizedText Text(IReadOnlyDictionary<string, string>? values)
    {
        var text = new LocalizedText();
        if (values is null)
        {
            return text;
        }

        foreach (var (language, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text = text.Set(language, value.Trim());
            }
        }

        return text;
    }

    public static Result<string> OneOf(string? value, string field, IReadOnlyList<string> allowed)
    {
        var v = value?.Trim() ?? string.Empty;
        return allowed.Contains(v, StringComparer.Ordinal)
            ? v
            : Error.Validation($"{field}.invalid", $"Expected one of: {string.Join(", ", allowed)}.").WithWhy(("value", v), ("allowed", allowed));
    }

    public static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A code from a name when none is given: the name's letters and digits, upper case, words joined by a dash.</summary>
    public static string CodeFromName(LocalizedText name)
    {
        var source = name.Resolve("en", "ar");
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in source)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                current.Append(char.ToUpperInvariant(c));
            }
            else if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        var code = string.Join('-', parts);
        return code.Length == 0 ? "COMP" : code.Length > 32 ? code[..32] : code;
    }
}

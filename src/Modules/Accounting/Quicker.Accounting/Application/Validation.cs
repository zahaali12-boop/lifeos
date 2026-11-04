using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Accounting.Application;

/// <summary>Input rules of the accounting services: codes, code formats, bilingual names, enumerations.</summary>
internal static class Validation
{
    public static Result<string> Code(string? value, string field)
    {
        var code = value?.Trim() ?? string.Empty;
        var valid = code.Length is >= 1 and <= 32
            && char.IsAsciiLetterOrDigit(code[0])
            && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        return valid ? code : Error.Validation($"{field}.code_invalid", "Codes are 1–32 letters, digits, '_', '.' or '-'.");
    }

    /// <summary>Account codes against the chart's format: '#' digit, 'A' letter, '?' letter or digit, anything else literal.</summary>
    public static Result<string> AccountCode(string? value, string format)
    {
        var code = Code(value, "account");
        if (code.IsFailure || string.IsNullOrEmpty(format))
        {
            return code;
        }

        var matches = code.Value.Length == format.Length && code.Value.Zip(format).All(static pair => pair.Second switch
        {
            '#' => char.IsAsciiDigit(pair.First),
            'A' => char.IsAsciiLetter(pair.First),
            '?' => char.IsAsciiLetterOrDigit(pair.First),
            _ => pair.First == pair.Second,
        });
        return matches
            ? code
            : Error.Validation("account.code_format", $"Account codes of this chart follow the format '{format}'.").WithWhy(("code", code.Value), ("format", format));
    }

    public static Result<string> CodeFormat(string? value)
    {
        var format = value?.Trim() ?? string.Empty;
        return format.Length <= 32 && format.All(static c => char.IsAsciiLetterOrDigit(c) || c is '#' or '?' or '_' or '.' or '-')
            ? format
            : Error.Validation("chart.code_format_invalid", "A code format is up to 32 characters of '#', 'A', '?', letters, digits, '_', '.' or '-'.");
    }

    public static Result<LocalizedText> Name(IReadOnlyDictionary<string, string>? values, string field)
    {
        if (values is null || values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"{field}.name_required", "A name in at least one language is required.");
        }

        var text = new LocalizedText();
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

    public static Result<string?> OptionalOneOf(string? value, string field, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string?)null;
        }

        var result = OneOf(value, field, allowed);
        return result.IsSuccess ? result.Value : result.Error!;
    }
}

using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Organization.Application;

/// <summary>Input rules shared by the organization services: codes, bilingual names, enumerations.</summary>
internal static class Validation
{
    public static Result<string> Code(string? value, string field)
    {
        var code = value?.Trim() ?? string.Empty;
        var valid = code.Length is >= 1 and <= 32
            && char.IsAsciiLetterOrDigit(code[0])
            && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        return valid
            ? code
            : Error.Validation($"{field}.code_invalid", "Codes are 1–32 letters, digits, '_', '.' or '-'.");
    }

    public static Result<string> UpperCode(string? value, string field)
    {
        var result = Code(value, field);
        return result.IsSuccess ? result.Value.ToUpperInvariant() : result;
    }

    public static Result<string> LowerCode(string? value, string field)
    {
        var result = Code(value, field);
        return result.IsSuccess ? result.Value.ToLowerInvariant() : result;
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

    public static Result<string> OneOf(string? value, string field, params string[] allowed)
    {
        var v = value?.Trim() ?? string.Empty;
        return allowed.Contains(v, StringComparer.Ordinal)
            ? v
            : Error.Validation($"{field}.invalid", $"Expected one of: {string.Join(", ", allowed)}.").WithWhy(("value", v), ("allowed", allowed));
    }

    public static Result<string> CurrencyCode(string? value, string field)
    {
        var code = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return code.Length == 3 && code.All(char.IsAsciiLetterUpper)
            ? code
            : Error.Validation($"{field}.currency_invalid", "Currency codes are three ISO 4217 letters.");
    }

    public static Result<string> TimeZone(string? value)
    {
        var id = value?.Trim() ?? string.Empty;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)
            ? id
            : Error.Validation("company.time_zone_invalid", "Use an IANA time zone id such as Asia/Baghdad.").WithWhy(("timeZone", id));
    }

    public static Dictionary<string, string> Map(IReadOnlyDictionary<string, string>? values) =>
        values is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(values, StringComparer.Ordinal);
}

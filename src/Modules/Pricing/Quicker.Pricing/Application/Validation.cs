using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Pricing.Application;

/// <summary>Shared checks of pricing records: codes, names, choices, validity periods and amounts.</summary>
internal static class Validation
{
    public static Result<string> Code(string? value, string field, int maxLength = 32)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0 || code.Length > maxLength || !code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return Error.Validation($"{field}.code_invalid", $"A code is 1–{maxLength} letters, digits, '-', '_' or '.'.").WithWhy(("code", value));
        }

        return code;
    }

    public static Result<LocalizedText> Name(IReadOnlyDictionary<string, string>? value, string field)
    {
        var text = new LocalizedText(value ?? new Dictionary<string, string>(StringComparer.Ordinal));
        if (text.Values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"{field}.name_required", "A name in at least one language is required.");
        }

        return text;
    }

    public static Result<string> OneOf(string? value, string field, IReadOnlyList<string> allowed, string? fallback = null)
    {
        var v = string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value.Trim().ToLowerInvariant();
        return allowed.Contains(v, StringComparer.Ordinal) ? v : Error.Validation($"{field}_invalid", $"Expected one of {string.Join(", ", allowed)}.").WithWhy(("value", value));
    }

    public static Result Period(DateOnly? from, DateOnly? to, string field) =>
        from is { } f && to is { } t && t < f
            ? Error.Validation($"{field}.validity_invalid", "The end of the validity is on or after its start.").WithWhy(("validFrom", from), ("validTo", to))
            : Result.Success();

    public static Result Percentage(decimal? value, string field, bool allowZero = false) =>
        value is { } v && (v > 100m || v < 0m || (!allowZero && v == 0m))
            ? Error.Validation($"{field}_invalid", allowZero ? "A percentage is between 0 and 100." : "A percentage is above 0 and at most 100.").WithWhy(("value", value))
            : Result.Success();

    public static string? Currency(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

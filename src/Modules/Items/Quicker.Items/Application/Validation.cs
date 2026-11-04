using System.Text.Json;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Items.Application;

/// <summary>Input rules of the item services: codes, bilingual names, enumerations, factors, barcodes.</summary>
internal static class Validation
{
    public static readonly IReadOnlyList<string> ItemTypes = ["stock", "non_stock", "service", "kit", "assembly"];
    public static readonly IReadOnlyList<string> Trackings = ["none", "lot", "serial", "lot_and_serial"];
    public static readonly IReadOnlyList<string> CostingMethods = ["fifo", "average", "standard"];
    public static readonly IReadOnlyList<string> Symbologies = ["EAN13", "EAN8", "UPCA", "CODE128", "CODE39", "QR", "DATAMATRIX", "OTHER"];
    public static readonly IReadOnlyList<string> BomKinds = ["kit", "assembly"];
    public static readonly IReadOnlyList<string> CycleCountClasses = ["A", "B", "C"];

    public static Result<string> Code(string? value, string field, int maxLength = 64)
    {
        var code = value?.Trim() ?? string.Empty;
        var valid = code.Length >= 1 && code.Length <= maxLength
            && char.IsAsciiLetterOrDigit(code[0])
            && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/');
        return valid ? code : Error.Validation($"{field}.code_invalid", $"Codes are 1–{maxLength} letters, digits, '_', '.', '-' or '/'.");
    }

    public static Result<string> UpperCode(string? value, string field, int maxLength = 32)
    {
        var code = Code(value, field, maxLength);
        return code.IsFailure ? code : code.Value.ToUpperInvariant();
    }

    public static Result<LocalizedText> Name(IReadOnlyDictionary<string, string>? values, string field)
    {
        if (values is null || values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"{field}.name_required", "A name in at least one language is required.");
        }

        return OptionalText(values);
    }

    public static LocalizedText OptionalText(IReadOnlyDictionary<string, string>? values)
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

    public static Result<string?> OptionalOneOf(string? value, string field, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string?)null;
        }

        var result = OneOf(value, field, allowed);
        return result.IsFailure ? result.Error! : result.Value;
    }

    /// <summary>A rational factor: both parts positive with at most 12 decimals, so the stored numeric(24,12) holds it exactly.</summary>
    public static Result<(decimal Numerator, decimal Denominator)> Factor(decimal numerator, decimal denominator, string field)
    {
        if (numerator <= 0m || denominator <= 0m)
        {
            return Error.Validation($"{field}.factor_invalid", "Numerator and denominator must be positive.").WithWhy(("numerator", numerator), ("denominator", denominator));
        }

        if (numerator.Scale > 12 || denominator.Scale > 12 || numerator >= 1_000_000_000_000m || denominator >= 1_000_000_000_000m)
        {
            return Error.Validation($"{field}.factor_precision", "Factors hold up to 12 integer and 12 decimal digits.").WithWhy(("numerator", numerator), ("denominator", denominator));
        }

        return (numerator, denominator);
    }

    public static Result<decimal?> NonNegative(decimal? value, string field)
    {
        return value is < 0m ? Error.Validation($"{field}.negative", "The value cannot be negative.").WithWhy(("value", value)) : value;
    }

    public static Result<int?> NonNegative(int? value, string field)
    {
        return value is < 0 ? Error.Validation($"{field}.negative", "The value cannot be negative.").WithWhy(("value", value)) : value;
    }

    public static Result<string?> Currency(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string?)null;
        }

        var code = value.Trim().ToUpperInvariant();
        return code.Length == 3 && code.All(char.IsAsciiLetterUpper) ? code : Error.Validation($"{field}.currency_invalid", "A currency is a three-letter ISO 4217 code.");
    }

    /// <summary>Barcodes are trimmed; EAN-13, EAN-8 and UPC-A must be numeric of the right length with a valid check digit.</summary>
    public static Result<(string Barcode, string Symbology)> Barcode(string? barcode, string? symbology)
    {
        var value = barcode?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 128 || value.Any(char.IsWhiteSpace))
        {
            return Error.Validation("barcode.invalid", "A barcode is 1–128 characters without whitespace.");
        }

        var sym = OneOf(string.IsNullOrWhiteSpace(symbology) ? "EAN13" : symbology.ToUpperInvariant(), "barcode.symbology", Symbologies);
        if (sym.IsFailure)
        {
            return sym.Error!;
        }

        var expectedLength = sym.Value switch { "EAN13" => 13, "EAN8" => 8, "UPCA" => 12, _ => 0 };
        if (expectedLength > 0)
        {
            if (value.Length != expectedLength || !value.All(char.IsAsciiDigit))
            {
                return Error.Validation("barcode.length", $"{sym.Value} barcodes are {expectedLength} digits.").WithWhy(("barcode", value), ("symbology", sym.Value));
            }

            if (!GtinCheckDigitValid(value))
            {
                return Error.Validation("barcode.check_digit", "The barcode's check digit is wrong.").WithWhy(("barcode", value), ("symbology", sym.Value));
            }
        }

        return (value, sym.Value);
    }

    /// <summary>GS1 modulo-10: weights 3 and 1 alternating from the right, starting with 3 for the digit left of the check digit.</summary>
    public static bool GtinCheckDigitValid(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        var check = (10 - (sum % 10)) % 10;
        return digits[^1] - '0' == check;
    }

    public static string JsonObject(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "{}";
        }

        return element.Value.ValueKind == JsonValueKind.Object ? element.Value.GetRawText() : "{}";
    }
}

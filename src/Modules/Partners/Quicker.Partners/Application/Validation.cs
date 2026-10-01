using System.Globalization;
using System.Net.Mail;
using System.Numerics;
using System.Text.Json;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Partners.Application;

/// <summary>Shared checks of the partner master: codes, names, contact details, bank identifiers and their masks.</summary>
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

    public static Result<string?> Email(string? value, string field)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return (string?)null;
        }

        return MailAddress.TryCreate(email, out _) ? email : Error.Validation($"{field}.email_invalid", "The email address is not valid.").WithWhy(("email", email));
    }

    public static Result<string> OneOf(string? value, string field, IReadOnlyList<string> allowed)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return allowed.Contains(v, StringComparer.Ordinal) ? v : Error.Validation($"{field}_invalid", $"Expected one of {string.Join(", ", allowed)}.").WithWhy(("value", value));
    }

    public static Result<string> Country(string? value, string field)
    {
        var country = (value ?? string.Empty).Trim().ToUpperInvariant();
        return country.Length == 2 && country.All(char.IsAsciiLetterUpper) ? country : Error.Validation($"{field}.country_invalid", "A country is its two-letter ISO code.").WithWhy(("country", value));
    }

    public static Result<decimal> Percentage(decimal value, string field)
    {
        return value is < 0m or > 100m ? Error.Validation($"{field}_invalid", "A percentage is between 0 and 100.").WithWhy(("value", value)) : value;
    }

    /// <summary>ISO 13616: 15–34 alphanumerics, country prefix, mod-97 check of the rearranged digits.</summary>
    public static Result<string> Iban(string? value)
    {
        var iban = new string((value ?? string.Empty).Where(static c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]))
        {
            return Error.Validation("bank_account.iban_invalid", "The IBAN is not well formed.").WithWhy(("iban", Mask(iban)));
        }

        var rearranged = string.Concat(iban.AsSpan(4), iban.AsSpan(0, 4));
        var digits = new System.Text.StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            digits.Append(char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
        }

        return BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture) % 97 == 1 ? iban : Error.Validation("bank_account.iban_invalid", "The IBAN's check digits do not match.").WithWhy(("iban", Mask(iban)));
    }

    public static Result<string> AccountNumber(string? value)
    {
        var number = new string((value ?? string.Empty).Where(static c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return number.Length is >= 4 and <= 34 && number.All(char.IsAsciiLetterOrDigit) ? number : Error.Validation("bank_account.number_invalid", "An account number is 4–34 letters or digits.");
    }

    /// <summary>The first two and last four characters stay visible: "IQ**************1234".</summary>
    public static string Mask(string value)
    {
        if (value.Length <= 6)
        {
            return new string('*', value.Length);
        }

        return string.Concat(value.AsSpan(0, 2), new string('*', value.Length - 6), value.AsSpan(value.Length - 4));
    }

    public static string JsonOrEmpty(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } v ? v.GetRawText() : "{}";
}

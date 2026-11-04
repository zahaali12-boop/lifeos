namespace Quicker.Kernel.Amounts;

/// <summary>
/// An ISO 4217 currency with its minor units. Values are validated at construction so a <see cref="Money"/>
/// can never carry an unknown or malformed currency code.
/// </summary>
public readonly record struct Currency
{
    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>Three-letter ISO 4217 code, upper case.</summary>
    public string Code { get; }

    /// <summary>Number of decimal places of the minor unit (0, 2 or 3 in practice; up to 4 allowed).</summary>
    public int MinorUnits { get; }

    /// <summary>The smallest representable amount, for example 0.01 for USD or 0.001 for KWD.</summary>
    public decimal MinorUnitValue => MinorUnits == 0 ? 1m : 1m / Pow10(MinorUnits);

    public static Currency Of(string code, int minorUnits)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length != 3 || !code.All(static c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException($"'{code}' is not a three-letter upper-case ISO 4217 code.", nameof(code));
        }

        if (minorUnits is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(minorUnits), minorUnits, "Minor units must be between 0 and 4.");
        }

        return new Currency(code, minorUnits);
    }

    public override string ToString() => Code;

    internal static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    // Frequently used currencies for tests and defaults. The full ISO list lives in the database seed.
    public static readonly Currency IQD = Of("IQD", 3);
    public static readonly Currency USD = Of("USD", 2);
    public static readonly Currency EUR = Of("EUR", 2);
    public static readonly Currency AED = Of("AED", 2);
    public static readonly Currency SAR = Of("SAR", 2);
    public static readonly Currency KWD = Of("KWD", 3);
    public static readonly Currency BHD = Of("BHD", 3);
    public static readonly Currency OMR = Of("OMR", 3);
    public static readonly Currency GBP = Of("GBP", 2);
    public static readonly Currency JPY = Of("JPY", 0);
}

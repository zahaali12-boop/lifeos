using System.Globalization;

namespace Quicker.Kernel.Quantities;

/// <summary>A unit of measure with the precision (decimal places) it is counted in.</summary>
public readonly record struct UnitOfMeasure
{
    private UnitOfMeasure(string code, int precision)
    {
        Code = code;
        Precision = precision;
    }

    public string Code { get; }

    public int Precision { get; }

    public static UnitOfMeasure Of(string code, int precision = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (precision is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Precision must be between 0 and 9.");
        }

        return new UnitOfMeasure(code.ToUpperInvariant(), precision);
    }

    public override string ToString() => Code;
}

/// <summary>
/// An exact rational conversion factor: 1 <see cref="From"/> = Numerator/Denominator <see cref="To"/>.
/// Rational factors keep carton→piece→dozen chains exact (hard scenario 9).
/// </summary>
public readonly record struct UomConversion
{
    public UomConversion(UnitOfMeasure from, UnitOfMeasure to, decimal numerator, decimal denominator)
    {
        if (numerator <= 0m || denominator <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Conversion factors must be positive.");
        }

        From = from;
        To = to;
        Numerator = numerator;
        Denominator = denominator;
    }

    public UnitOfMeasure From { get; }

    public UnitOfMeasure To { get; }

    public decimal Numerator { get; }

    public decimal Denominator { get; }

    public decimal Factor => Numerator / Denominator;

    public UomConversion Inverse() => new(To, From, Denominator, Numerator);

    public UomConversion Then(UomConversion next)
    {
        if (next.From != To)
        {
            throw new UnitMismatchException(To, next.From);
        }

        return new UomConversion(From, next.To, Numerator * next.Numerator, Denominator * next.Denominator);
    }
}

/// <summary>An exact decimal quantity in a unit of measure.</summary>
public readonly record struct Quantity : IComparable<Quantity>
{
    public Quantity(decimal value, UnitOfMeasure unit)
    {
        Value = value;
        Unit = unit;
    }

    public decimal Value { get; }

    public UnitOfMeasure Unit { get; }

    public bool IsZero => Value == 0m;

    public static Quantity Zero(UnitOfMeasure unit) => new(0m, unit);

    public static Quantity Of(decimal value, UnitOfMeasure unit) => new(value, unit);

    /// <summary>
    /// Converts to another unit. The result is exact; if the target unit's precision cannot hold it exactly the
    /// caller decides whether to round (through RoundingPolicy) or refuse. <see cref="IsExactIn"/> tells which.
    /// </summary>
    public Quantity ConvertTo(UnitOfMeasure target, UomConversion conversion)
    {
        if (conversion.From != Unit || conversion.To != target)
        {
            throw new UnitMismatchException(Unit, conversion.From);
        }

        return new Quantity(Value * conversion.Numerator / conversion.Denominator, target);
    }

    public bool IsExactIn(UnitOfMeasure unit) => decimal.Remainder(Value * Pow10(unit.Precision), 1m) == 0m;

    public static Quantity operator +(Quantity left, Quantity right)
    {
        EnsureSameUnit(left, right);
        return new Quantity(left.Value + right.Value, left.Unit);
    }

    public static Quantity operator -(Quantity left, Quantity right)
    {
        EnsureSameUnit(left, right);
        return new Quantity(left.Value - right.Value, left.Unit);
    }

    public static Quantity operator -(Quantity value) => new(-value.Value, value.Unit);

    public static Quantity operator *(Quantity left, decimal factor) => new(left.Value * factor, left.Unit);

    public static bool operator >(Quantity left, Quantity right) => left.CompareTo(right) > 0;

    public static bool operator <(Quantity left, Quantity right) => left.CompareTo(right) < 0;

    public static bool operator >=(Quantity left, Quantity right) => left.CompareTo(right) >= 0;

    public static bool operator <=(Quantity left, Quantity right) => left.CompareTo(right) <= 0;

    public Quantity Add(Quantity other) => this + other;

    public Quantity Subtract(Quantity other) => this - other;

    public Quantity Multiply(decimal factor) => this * factor;

    public int CompareTo(Quantity other)
    {
        EnsureSameUnit(this, other);
        return Value.CompareTo(other.Value);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value.ToString(CultureInfo.InvariantCulture)} {Unit.Code}");

    private static void EnsureSameUnit(Quantity left, Quantity right)
    {
        if (left.Unit != right.Unit)
        {
            throw new UnitMismatchException(left.Unit, right.Unit);
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

public sealed class UnitMismatchException(UnitOfMeasure left, UnitOfMeasure right)
    : InvalidOperationException($"Cannot combine quantities in {left.Code} and {right.Code}; convert first.")
{
    public UnitOfMeasure Left { get; } = left;

    public UnitOfMeasure Right { get; } = right;
}

using System.Globalization;

namespace Quicker.Kernel.Amounts;

/// <summary>
/// An exact decimal amount in a currency. Arithmetic between different currencies is refused; conversion
/// happens only through <see cref="ExchangeRate"/>. Money never rounds itself: use <see cref="RoundingPolicy"/>.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public bool IsPositive => Amount > 0m;

    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money Of(decimal amount, Currency currency) => new(amount, currency);

    /// <summary>True when the amount has no fraction finer than the currency's minor unit.</summary>
    public bool IsRoundedToMinorUnit => decimal.Remainder(Amount, Currency.MinorUnitValue) == 0m;

    public Money Negate() => new(-Amount, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => value.Negate();

    public static Money operator *(Money left, decimal factor) => new(left.Amount * factor, left.Currency);

    public static Money operator *(decimal factor, Money right) => new(right.Amount * factor, right.Currency);

    public static Money operator /(Money left, decimal divisor) => new(left.Amount / divisor, left.Currency);

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Multiply(decimal factor) => this * factor;

    public Money Divide(decimal divisor) => this / divisor;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public static Money Sum(IEnumerable<Money> values, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = Zero(currency);
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount.ToString($"F{Currency.MinorUnits}", CultureInfo.InvariantCulture)} {Currency.Code}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }
}

public sealed class CurrencyMismatchException(Currency left, Currency right)
    : InvalidOperationException($"Cannot combine amounts in {left.Code} and {right.Code}; convert through an exchange rate first.")
{
    public Currency Left { get; } = left;

    public Currency Right { get; } = right;
}

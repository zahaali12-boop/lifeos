namespace Quicker.Kernel.Amounts;

/// <summary>Which way a value goes to a multiple of an increment: the nearest (under the policy's midpoint rule), always up, or always down.</summary>
public enum RoundingDirection
{
    Nearest = 0,
    Up = 1,
    Down = 2,
}

public enum RoundingMode
{
    /// <summary>Commercial rounding: 0.5 rounds away from zero. The default for every company.</summary>
    HalfAwayFromZero = 0,

    /// <summary>Banker's rounding: 0.5 rounds to the even neighbour. Optional per company.</summary>
    HalfEven = 1,
}

/// <summary>
/// The only place in the system that rounds (ADR-0005). Rounds money to a currency's minor unit or to a cash
/// increment, and allocates a total across parts so the parts always sum to the whole (largest remainder).
/// </summary>
public sealed class RoundingPolicy
{
    public static readonly RoundingPolicy Default = new(RoundingMode.HalfAwayFromZero);

    public RoundingPolicy(RoundingMode mode)
    {
        Mode = mode;
    }

    public RoundingMode Mode { get; }

    private MidpointRounding Midpoint => Mode == RoundingMode.HalfEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;

    /// <summary>Rounds a raw decimal to a number of decimal places under this policy.</summary>
    public decimal Round(decimal value, int decimals)
    {
        if (decimals is < 0 or > 28)
        {
            throw new ArgumentOutOfRangeException(nameof(decimals));
        }

        return Math.Round(value, decimals, Midpoint);
    }

    /// <summary>Rounds money to its currency's minor unit.</summary>
    public Money Round(Money money) => new(Round(money.Amount, money.Currency.MinorUnits), money.Currency);

    /// <summary>Rounds money to its minor unit and returns the discarded remainder (original minus rounded).</summary>
    public (Money Rounded, Money Remainder) RoundWithRemainder(Money money)
    {
        var rounded = Round(money);
        return (rounded, money - rounded);
    }

    /// <summary>
    /// Rounds to a cash increment (for example 250 IQD or 0.05 CHF). The increment must be a positive multiple of
    /// the minor unit. Used only for cash totals, never for ledger amounts.
    /// </summary>
    public Money RoundToIncrement(Money money, decimal increment)
    {
        if (increment <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), increment, "Increment must be positive.");
        }

        if (decimal.Remainder(increment, money.Currency.MinorUnitValue) != 0m)
        {
            throw new ArgumentException("Increment must be a multiple of the currency's minor unit.", nameof(increment));
        }

        var units = Math.Round(money.Amount / increment, 0, Midpoint);
        return new Money(units * increment, money.Currency);
    }

    /// <summary>
    /// Splits <paramref name="total"/> across <paramref name="weights"/> proportionally, rounding each part to the
    /// currency's minor unit, and distributes the rounding residue by largest remainder so the parts sum exactly to
    /// the (rounded) total. Weights must be non-negative and not all zero; a zero weight always yields zero.
    /// </summary>
    public IReadOnlyList<Money> Allocate(Money total, IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        if (weights.Any(static w => w < 0m))
        {
            throw new ArgumentException("Weights must be non-negative.", nameof(weights));
        }

        var weightSum = weights.Sum();
        if (weightSum == 0m)
        {
            throw new ArgumentException("Weights must not all be zero.", nameof(weights));
        }

        var currency = total.Currency;
        var unit = currency.MinorUnitValue;
        var roundedTotal = Round(total);
        var totalUnits = decimal.Truncate(roundedTotal.Amount / unit);

        var exact = new decimal[weights.Count];
        var floors = new decimal[weights.Count];
        var remainders = new decimal[weights.Count];
        var sign = totalUnits < 0m ? -1m : 1m;
        var absTotalUnits = Math.Abs(totalUnits);
        var assigned = 0m;

        for (var i = 0; i < weights.Count; i++)
        {
            exact[i] = absTotalUnits * weights[i] / weightSum;
            floors[i] = decimal.Truncate(exact[i]);
            remainders[i] = exact[i] - floors[i];
            assigned += floors[i];
        }

        var leftover = (int)(absTotalUnits - assigned);
        var order = Enumerable.Range(0, weights.Count)
            .OrderByDescending(i => remainders[i])
            .ThenByDescending(i => weights[i])
            .ThenBy(i => i)
            .ToArray();

        for (var k = 0; k < leftover; k++)
        {
            floors[order[k % order.Length]] += 1m;
        }

        var result = new Money[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = new Money(sign * floors[i] * unit, currency);
        }

        return result;
    }

    /// <summary>
    /// Rounds a value to a multiple of an increment in a direction (a price list's rounding rule: to the nearest 250 IQD,
    /// up to the next 0.05). Up and down go away from and towards negative infinity.
    /// </summary>
    public decimal RoundToMultiple(decimal value, decimal increment, RoundingDirection direction)
    {
        if (increment <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(increment), increment, "Increment must be positive.");
        }

        var units = value / increment;
        units = direction switch
        {
            RoundingDirection.Up => Math.Ceiling(units),
            RoundingDirection.Down => Math.Floor(units),
            _ => Math.Round(units, 0, Midpoint),
        };
        return units * increment;
    }

    /// <summary>How many whole times a part fits in a total (complete sets of a bundle, applications of a buy-X offer); zero for a non-positive total.</summary>
    public static decimal WholeTimes(decimal total, decimal part)
    {
        if (part <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(part), part, "Part must be positive.");
        }

        return total <= 0m ? 0m : Math.Floor(total / part);
    }

    /// <summary>Convenience: split equally into <paramref name="parts"/> parts.</summary>
    public IReadOnlyList<Money> Split(Money total, int parts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parts);
        return Allocate(total, Enumerable.Repeat(1m, parts).ToArray());
    }
}

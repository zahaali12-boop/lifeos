namespace Quicker.Kernel.Amounts;

/// <summary>
/// A dated rate stating that 1 <see cref="From"/> = <see cref="Rate"/> <see cref="To"/>. Conversions are exact
/// (unrounded); the caller rounds through <see cref="RoundingPolicy"/> when the amount is posted.
/// </summary>
public readonly record struct ExchangeRate
{
    public ExchangeRate(Currency from, Currency to, decimal rate, DateOnly date, string rateType = "spot")
    {
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "An exchange rate must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rateType);
        From = from;
        To = to;
        Rate = rate;
        Date = date;
        RateType = rateType;
    }

    public Currency From { get; }

    public Currency To { get; }

    public decimal Rate { get; }

    public DateOnly Date { get; }

    public string RateType { get; }

    public bool IsIdentity => From == To;

    public static ExchangeRate Identity(Currency currency, DateOnly date) => new(currency, currency, 1m, date, "identity");

    public Money Convert(Money money)
    {
        if (money.Currency != From)
        {
            throw new CurrencyMismatchException(money.Currency, From);
        }

        return new Money(money.Amount * Rate, To);
    }

    /// <summary>Converts in the opposite direction by dividing, never by a stored rounded inverse rate.</summary>
    public Money ConvertBack(Money money)
    {
        if (money.Currency != To)
        {
            throw new CurrencyMismatchException(money.Currency, To);
        }

        return new Money(money.Amount / Rate, From);
    }

    /// <summary>Chains two rates through a common currency (for example EUR→USD then USD→IQD).</summary>
    public ExchangeRate Then(ExchangeRate next)
    {
        if (next.From != To)
        {
            throw new CurrencyMismatchException(To, next.From);
        }

        return new ExchangeRate(From, next.To, Rate * next.Rate, Date, RateType);
    }
}

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Quicker.Kernel.Amounts;

namespace Quicker.Kernel.Tests;

public class MoneyTests
{
    private static readonly RoundingPolicy Policy = RoundingPolicy.Default;

    [Fact]
    public void Adding_different_currencies_is_refused()
    {
        var usd = Money.Of(10m, Currency.USD);
        var eur = Money.Of(10m, Currency.EUR);
        Should.Throw<CurrencyMismatchException>(() => usd + eur);
    }

    [Fact]
    public void Half_away_from_zero_is_the_default()
    {
        Policy.Round(Money.Of(2.345m, Currency.USD)).Amount.ShouldBe(2.35m);
        Policy.Round(Money.Of(-2.345m, Currency.USD)).Amount.ShouldBe(-2.35m);
        Policy.Round(Money.Of(2.5m, Currency.JPY)).Amount.ShouldBe(3m);
    }

    [Fact]
    public void Half_even_rounds_to_the_even_neighbour()
    {
        var bankers = new RoundingPolicy(RoundingMode.HalfEven);
        bankers.Round(Money.Of(2.345m, Currency.USD)).Amount.ShouldBe(2.34m);
        bankers.Round(Money.Of(2.355m, Currency.USD)).Amount.ShouldBe(2.36m);
    }

    [Fact]
    public void Three_decimal_currencies_round_to_their_minor_unit()
    {
        Policy.Round(Money.Of(1.2345m, Currency.KWD)).Amount.ShouldBe(1.235m);
        Policy.Round(Money.Of(1234.5678m, Currency.IQD)).Amount.ShouldBe(1234.568m);
    }

    [Fact]
    public void Cash_rounding_to_250_dinars()
    {
        Policy.RoundToIncrement(Money.Of(12_374m, Currency.IQD), 250m).Amount.ShouldBe(12_250m);
        Policy.RoundToIncrement(Money.Of(12_375m, Currency.IQD), 250m).Amount.ShouldBe(12_500m);
        Policy.RoundToIncrement(Money.Of(12_374m, Currency.IQD), 1000m).Amount.ShouldBe(12_000m);
        Policy.RoundToIncrement(Money.Of(12_625m, Currency.IQD), 250m).Amount.ShouldBe(12_750m);
    }

    [Fact]
    public void Cash_increment_must_be_a_multiple_of_the_minor_unit()
    {
        Should.Throw<ArgumentException>(() => Policy.RoundToIncrement(Money.Of(10m, Currency.USD), 0.015m));
    }

    [Fact]
    public void Allocation_by_largest_remainder_sums_exactly()
    {
        var parts = Policy.Allocate(Money.Of(100m, Currency.USD), [1m, 1m, 1m]);
        parts.Select(static p => p.Amount).ShouldBe([33.34m, 33.33m, 33.33m]);
        Money.Sum(parts, Currency.USD).Amount.ShouldBe(100m);
    }

    [Fact]
    public void Allocation_of_negative_totals_keeps_the_sign()
    {
        var parts = Policy.Allocate(Money.Of(-10m, Currency.USD), [2m, 1m]);
        parts.Select(static p => p.Amount).ShouldBe([-6.67m, -3.33m]);
    }

    [Fact]
    public void Zero_weights_receive_nothing()
    {
        var parts = Policy.Allocate(Money.Of(5m, Currency.USD), [0m, 1m]);
        parts[0].Amount.ShouldBe(0m);
        parts[1].Amount.ShouldBe(5m);
    }

    [Property(MaxTest = 500)]
    public bool Allocation_always_sums_to_the_rounded_total(PositiveInt cents, NonEmptyArray<PositiveInt> rawWeights)
    {
        var currency = Currency.USD;
        var total = Money.Of(cents.Get / 100m, currency);
        var weights = rawWeights.Get.Take(20).Select(static w => (decimal)w.Get).ToArray();
        var parts = Policy.Allocate(total, weights);
        return Money.Sum(parts, currency) == Policy.Round(total)
            && parts.All(p => p.IsRoundedToMinorUnit)
            && parts.All(p => !p.IsNegative);
    }

    [Property(MaxTest = 500)]
    public bool Allocation_never_differs_from_exact_share_by_more_than_one_minor_unit(PositiveInt cents, NonEmptyArray<PositiveInt> rawWeights)
    {
        var total = cents.Get / 100m;
        var weights = rawWeights.Get.Take(20).Select(static w => (decimal)w.Get).ToArray();
        var sum = weights.Sum();
        var parts = Policy.Allocate(Money.Of(total, Currency.USD), weights);
        for (var i = 0; i < weights.Length; i++)
        {
            var exact = total * weights[i] / sum;
            if (Math.Abs(parts[i].Amount - exact) >= 0.01m)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 300)]
    public bool Rounding_is_idempotent_and_stays_within_half_a_minor_unit(decimal amount)
    {
        var scaled = Math.Abs(amount) % 1_000_000m;
        var money = Money.Of(scaled, Currency.BHD);
        var once = Policy.Round(money);
        var twice = Policy.Round(once);
        return once == twice && Math.Abs(once.Amount - money.Amount) <= 0.0005m;
    }

    [Property(MaxTest = 300)]
    public bool Exchange_rate_round_trip_is_exact_for_rational_rates(PositiveInt amountCents, PositiveInt rateThousandths)
    {
        var rate = new ExchangeRate(Currency.EUR, Currency.USD, rateThousandths.Get / 1000m, new DateOnly(2026, 1, 15));
        var eur = Money.Of(amountCents.Get / 100m, Currency.EUR);
        var back = rate.ConvertBack(rate.Convert(eur));
        return back.Currency == Currency.EUR && Math.Abs(back.Amount - eur.Amount) < 0.000000000001m;
    }

    [Fact]
    public void Chained_rates_go_through_the_functional_currency()
    {
        var eurUsd = new ExchangeRate(Currency.EUR, Currency.USD, 1.1m, new DateOnly(2026, 1, 15));
        var usdIqd = new ExchangeRate(Currency.USD, Currency.IQD, 1310m, new DateOnly(2026, 1, 15));
        var eurIqd = eurUsd.Then(usdIqd);
        eurIqd.Convert(Money.Of(100m, Currency.EUR)).Amount.ShouldBe(144_100m);
    }

    [Fact]
    public void Formatting_uses_the_currency_precision()
    {
        Money.Of(1234.5m, Currency.USD).ToString().ShouldBe("1234.50 USD");
        Money.Of(1234.5m, Currency.KWD).ToString().ShouldBe("1234.500 KWD");
        Money.Of(1234.5m, Currency.JPY).ToString().ShouldBe("1235 JPY");
    }
}

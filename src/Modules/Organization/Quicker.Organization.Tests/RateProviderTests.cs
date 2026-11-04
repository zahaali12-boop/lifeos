using System.Text.Json;
using Quicker.Organization.Application;

namespace Quicker.Organization.Tests;

/// <summary>Provider payload parsing: the part of an adapter that must be exact, checked against captured samples.</summary>
public sealed class RateProviderTests
{
    [Fact]
    public void Ecb_daily_envelope_yields_euro_based_quotes_for_the_latest_day()
    {
        var parsed = EcbRateProvider.Parse(ApiHostFixture.EcbSample);
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Value.BaseCurrency.ShouldBe("EUR");
        parsed.Value.Date.ShouldBe(new DateOnly(2026, 9, 21));
        parsed.Value.Rates.Select(static r => (r.From, r.To, r.Rate)).ShouldBe([("EUR", "USD", 1.1750m), ("EUR", "GBP", 0.8650m), ("EUR", "JPY", 173.25m)]);
    }

    [Fact]
    public void Ecb_history_picks_the_requested_day_and_reports_a_missing_one()
    {
        const string history = """
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube>
                <Cube time="2026-09-21"><Cube currency="USD" rate="1.1750"/></Cube>
                <Cube time="2026-09-18"><Cube currency="USD" rate="1.1700"/><Cube currency="BAD" rate="x"/></Cube>
              </Cube>
            </gesmes:Envelope>
            """;
        EcbRateProvider.Parse(history).Value.Rates.Single().Rate.ShouldBe(1.1750m);
        var friday = EcbRateProvider.Parse(history, new DateOnly(2026, 9, 18));
        friday.Value.Date.ShouldBe(new DateOnly(2026, 9, 18));
        friday.Value.Rates.Single().Rate.ShouldBe(1.1700m); // the malformed BAD entry is dropped
        EcbRateProvider.Parse(history, new DateOnly(2026, 9, 19)).Error!.Code.ShouldBe("rate_provider.no_rates");
        EcbRateProvider.Parse("<not xml").Error!.Code.ShouldBe("rate_provider.malformed");
        EcbRateProvider.Parse("<Envelope/>").Error!.Code.ShouldBe("rate_provider.no_rates");
    }

    [Fact]
    public void Open_exchange_rates_payload_yields_quotes_from_its_base_on_the_timestamp_day()
    {
        using var document = JsonDocument.Parse(ApiHostFixture.OxrSample);
        var parsed = OpenExchangeRatesProvider.Parse(document.RootElement);
        parsed.IsSuccess.ShouldBeTrue();
        parsed.Value.BaseCurrency.ShouldBe("USD");
        parsed.Value.Date.ShouldBe(new DateOnly(2026, 9, 21));
        parsed.Value.Rates.Select(static r => (r.To, r.Rate)).ShouldBe([("AED", 3.6725m), ("IQD", 1310.5m), ("EUR", 0.851m), ("XXX", 1m)]);
        parsed.Value.Rates.ShouldAllBe(static r => r.From == "USD");

        using var broken = JsonDocument.Parse("""{"error": true}""");
        OpenExchangeRatesProvider.Parse(broken.RootElement).Error!.Code.ShouldBe("rate_provider.malformed");
    }
}

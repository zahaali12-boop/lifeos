using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Trader.Tests;

/// <summary>
/// The day's exchange rate (trader edition, ADR-0035 §14): due from the hour chosen when a currency the shop buys in
/// has no rate entered for the day, entered in one go into the organization's rates, corrected only with a reason,
/// switched off with a negative hour; the pricing board shows what was entered.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DailyRatesTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task The_days_rate_is_asked_from_the_hour_chosen_until_it_is_entered()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        // The fixture's clock reads 08:00 UTC on 22 September 2026: 11:00 in Baghdad.
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRR", legalName = Name("Rate Trading", "تجارة الأسعار"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();

        // Nothing bought yet and no currency named: a dinar company is asked for the dollar, from 08:00 by default.
        var today = await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}");
        today.GetProperty("date").GetString().ShouldBe("2026-09-22");
        today.GetProperty("currency").GetString().ShouldBe("IQD");
        today.GetProperty("promptHour").GetInt32().ShouldBe(8);
        today.GetProperty("due").GetBoolean().ShouldBeTrue();
        var usd = today.GetProperty("rates").EnumerateArray().Single();
        usd.GetProperty("currency").GetString().ShouldBe("USD");
        usd.GetProperty("rate").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        usd.GetProperty("enteredToday").GetBoolean().ShouldBeFalse();

        // Not before the hour: at 11:00 a prompt from noon is not due yet; one from 11 is.
        await owner.PutAsync("/api/v1/organization/settings/trader.rates.prompt_hour", new { value = 12, valueType = "number" });
        (await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}")).GetProperty("due").GetBoolean().ShouldBeFalse();
        await owner.PutAsync("/api/v1/organization/settings/trader.rates.prompt_hour", new { value = 11, valueType = "number" });
        (await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}")).GetProperty("due").GetBoolean().ShouldBeTrue();

        // Yesterday's rate is shown as the last known, and does not count for today.
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-21", rate = 1390m });
        today = await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}");
        today.GetProperty("due").GetBoolean().ShouldBeTrue();
        usd = today.GetProperty("rates").EnumerateArray().Single();
        usd.GetProperty("rate").GetDecimal().ShouldBe(1390m);
        usd.GetProperty("asOf").GetString().ShouldBe("2026-09-21");
        usd.GetProperty("enteredToday").GetBoolean().ShouldBeFalse();

        // Refused: the company's own currency, a rate that is not positive, nothing at all.
        (await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = new[] { new { currency = "IQD", rate = 1m } } }, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("rates.same_currency");
        (await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = new[] { new { currency = "USD", rate = 0m } } }, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("rates.not_positive");
        (await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("rates.empty");

        // Entered: 1,400 today. The prompt is no longer due, the organization's rates resolve to it and the board prices at it.
        today = await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = new[] { new { currency = "usd", rate = 1400m } } }, HttpStatusCode.OK);
        today.GetProperty("due").GetBoolean().ShouldBeFalse();
        usd = today.GetProperty("rates").EnumerateArray().Single();
        usd.GetProperty("rate").GetDecimal().ShouldBe(1400m);
        usd.GetProperty("asOf").GetString().ShouldBe("2026-09-22");
        usd.GetProperty("enteredToday").GetBoolean().ShouldBeTrue();
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-09-22")).GetProperty("rate").GetDecimal().ShouldBe(1400m);
        var market = (await owner.GetOkAsync($"/api/v1/trader/pricing/board?companyId={companyId}")).GetProperty("marketRates").EnumerateArray().Single();
        market.GetProperty("currency").GetString().ShouldBe("USD");
        market.GetProperty("rate").GetDecimal().ShouldBe(1400m);

        // A rate of the day already entered is corrected only with a reason (ADR-0017).
        (await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = new[] { new { currency = "USD", rate = 1410m } } }, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("rate.reason_required");
        today = await owner.PostAsync("/api/v1/trader/rates/today", new { companyId, rates = new[] { new { currency = "USD", rate = 1410m } }, reason = "Typed 1,400 for 1,410" }, HttpStatusCode.OK);
        today.GetProperty("rates").EnumerateArray().Single().GetProperty("rate").GetDecimal().ShouldBe(1410m);

        // A currency named in the settings is asked for as well; a negative hour never asks.
        await owner.PutAsync("/api/v1/organization/settings/trader.rates.currencies", new { value = "EUR, usd", valueType = "string" });
        today = await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}");
        today.GetProperty("rates").EnumerateArray().Select(static r => r.GetProperty("currency").GetString()).ShouldBe(["EUR", "USD"]);
        today.GetProperty("due").GetBoolean().ShouldBeTrue();
        await owner.PutAsync("/api/v1/organization/settings/trader.rates.prompt_hour", new { value = -1, valueType = "number" });
        (await owner.GetOkAsync($"/api/v1/trader/rates/today?companyId={companyId}")).GetProperty("due").GetBoolean().ShouldBeFalse();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Organization.Tests;

/// <summary>ADR-0017 through the API: effective-dated rates, direct/inverse/cross resolution, corrections with reasons, provider import.</summary>
[Collection(ApiCollection.Name)]
public sealed class ExchangeRateTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Rates_resolve_for_a_date_directly_inversely_and_across_the_functional_currency()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.CreateCompanyAsync("USCO", functionalCurrency: "USD")).GetProperty("id").GetGuid();

        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-01", rate = 1310 });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-15", rate = 1320 });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "EUR", toCurrency = "USD", validFrom = "2026-09-10", rate = 1.17 });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "closing", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-30", rate = 1325 });

        var direct = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-09-14");
        direct.GetProperty("rate").GetDecimal().ShouldBe(1310m);
        direct.GetProperty("method").GetString().ShouldBe("direct");
        direct.GetProperty("effectiveFrom").GetString().ShouldBe("2026-09-01");
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-09-15")).GetProperty("rate").GetDecimal().ShouldBe(1320m);
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-12-31")).GetProperty("effectiveFrom").GetString().ShouldBe("2026-09-15");

        var inverse = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=IQD&to=USD&date=2026-09-20");
        inverse.GetProperty("method").GetString().ShouldBe("inverse");
        inverse.GetProperty("rate").GetDecimal().ShouldBe(1m / 1320m, 0.000000000001m);

        var cross = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=EUR&to=IQD&date=2026-09-20");
        cross.GetProperty("method").GetString().ShouldBe("cross");
        cross.GetProperty("rate").GetDecimal().ShouldBe(1.17m * 1320m, 0.000001m);
        cross.GetProperty("rateIds").GetArrayLength().ShouldBe(2);
        cross.GetProperty("effectiveFrom").GetString().ShouldBe("2026-09-15"); // the later of the two legs

        var crossInverse = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=IQD&to=EUR&date=2026-09-20");
        crossInverse.GetProperty("rate").GetDecimal().ShouldBe(1m / (1.17m * 1320m), 0.000000001m);

        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=USD&date=2026-09-20")).GetProperty("method").GetString().ShouldBe("identity");
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-10-05&rateType=closing")).GetProperty("rate").GetDecimal().ShouldBe(1325m);

        // Before the EUR leg exists there is no cross rate; the error explains the path that was tried.
        var missing = await owner.GetAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=EUR&to=IQD&date=2026-09-05");
        missing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await missing.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("rate.not_found");
        problem.GetProperty("why").GetProperty("functionalCurrency").GetString().ShouldBe("USD");
        (await owner.GetErrorAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-08-01", HttpStatusCode.Conflict)).ShouldBe("rate.not_found");
        (await owner.GetErrorAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-09-20&rateType=nope", HttpStatusCode.NotFound)).ShouldBe("rate_type.not_found");

        // An IQD-functional company crosses through IQD: EUR→USD has no IQD leg for EUR, so only the direct rate serves it.
        var iqdCompany = (await owner.CreateCompanyAsync("IQCO")).GetProperty("id").GetGuid();
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={iqdCompany}&from=EUR&to=USD&date=2026-09-20")).GetProperty("method").GetString().ShouldBe("direct");
        (await owner.GetErrorAsync($"/api/v1/organization/rates/resolve?companyId={iqdCompany}&from=EUR&to=IQD&date=2026-09-20", HttpStatusCode.Conflict)).ShouldBe("rate.not_found");
    }

    [Fact]
    public async Task Correcting_or_deleting_a_rate_needs_a_reason_and_leaves_an_audit_trail()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var created = await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-01", rate = 1310 });
        var rateId = created.GetProperty("id").GetGuid();
        created.GetProperty("source").GetString().ShouldBe("manual");

        var silent = await owner.PostAsJsonAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-01", rate = 1311 }, Json);
        silent.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await silent.ErrorCodeAsync()).ShouldBe("rate.reason_required");

        var corrected = await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-01", rate = 1311, reason = "Typo in the CBI bulletin" });
        corrected.GetProperty("id").GetGuid().ShouldBe(rateId);
        corrected.GetProperty("rate").GetDecimal().ShouldBe(1311m);

        (await (await owner.PostAsJsonAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "USD", validFrom = "2026-09-01", rate = 1 }, Json)).ErrorCodeAsync()).ShouldBe("rate.same_currency");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-02", rate = 0 }, Json)).ErrorCodeAsync()).ShouldBe("rate.not_positive");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "ZZZ", validFrom = "2026-09-02", rate = 1 }, Json)).ErrorCodeAsync()).ShouldBe("rate.currency_unknown");

        (await owner.DeleteAsync($"/api/v1/organization/rates/{rateId}")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await owner.DeleteAsync($"/api/v1/organization/rates/{rateId}?reason=Entered%20on%20the%20wrong%20type")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetOkAsync("/api/v1/organization/rates?from=USD&to=IQD")).GetArrayLength().ShouldBe(0);

        var timeline = (await owner.GetOkAsync($"/api/v1/audit/records/exchange_rate/{rateId}")).EnumerateArray().ToList();
        timeline.Select(static e => e.Str("action")).ShouldBe(["created", "updated", "deleted"]);
        timeline[1].GetProperty("diff").GetProperty("rate").GetProperty("old").GetDecimal().ShouldBe(1310m);
        timeline[1].GetProperty("after").GetProperty("reason").GetString().ShouldBe("Typo in the CBI bulletin");
        timeline[2].GetProperty("reason").GetString().ShouldBe("Entered on the wrong type");
    }

    [Fact]
    public async Task Providers_import_into_a_rate_type_without_overwriting_manual_rates()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.CreateCompanyAsync("EUCO", functionalCurrency: "EUR")).GetProperty("id").GetGuid();
        (await owner.GetOkAsync("/api/v1/organization/rates/providers")).EnumerateArray().Select(static p => p.GetString()).ShouldBe(["ecb", "openexchangerates"]);

        var ecb = await owner.PostAsync("/api/v1/organization/rates/import", new { provider = "ecb" }, HttpStatusCode.OK);
        ecb.GetProperty("date").GetString().ShouldBe("2026-09-21");
        ecb.GetProperty("imported").GetInt32().ShouldBe(3);
        ecb.GetProperty("currencies").EnumerateArray().Select(static c => c.GetString()).ShouldBe(["GBP", "JPY", "USD"]);

        var usd = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=EUR&to=USD&date=2026-09-21");
        usd.GetProperty("rate").GetDecimal().ShouldBe(1.175m);
        var stored = (await owner.GetOkAsync("/api/v1/organization/rates?from=EUR&to=USD")).EnumerateArray().Single();
        stored.GetProperty("source").GetString().ShouldBe("ecb");

        // Cross through EUR: GBP→JPY = (1/0.865) × 173.25
        var gbpJpy = await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=GBP&to=JPY&date=2026-09-25");
        gbpJpy.GetProperty("method").GetString().ShouldBe("cross");
        gbpJpy.GetProperty("rate").GetDecimal().ShouldBe(173.25m / 0.865m, 0.0000001m);

        var again = await owner.PostAsync("/api/v1/organization/rates/import", new { provider = "ecb", currencies = new[] { "USD", "GBP" } }, HttpStatusCode.OK);
        again.GetProperty("imported").GetInt32().ShouldBe(0);
        again.GetProperty("unchanged").GetInt32().ShouldBe(2);
        again.GetProperty("skipped").GetInt32().ShouldBe(1);

        // A manual rate on the same date is never overwritten by a feed.
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-09-21", rate = 1300 });
        var oxr = await owner.PostAsync("/api/v1/organization/rates/import", new { provider = "openexchangerates", baseCurrency = "USD" }, HttpStatusCode.OK);
        oxr.GetProperty("date").GetString().ShouldBe("2026-09-21");
        oxr.GetProperty("imported").GetInt32().ShouldBe(2); // AED, EUR
        oxr.GetProperty("skipped").GetInt32().ShouldBe(2); // manual USD→IQD and the unknown code XXX
        (await owner.GetOkAsync($"/api/v1/organization/rates/resolve?companyId={companyId}&from=USD&to=IQD&date=2026-09-21")).GetProperty("rate").GetDecimal().ShouldBe(1300m);

        (await (await owner.PostAsJsonAsync("/api/v1/organization/rates/import", new { provider = "cbi" }, Json)).ErrorCodeAsync()).ShouldBe("rate_provider.not_found");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/rates/import", new { provider = "ecb", baseCurrency = "USD" }, Json)).ErrorCodeAsync()).ShouldBe("rate_provider.base_unsupported");

        var events = (await owner.GetOkAsync("/api/v1/audit/events?entityType=exchange_rates")).GetProperty("items").EnumerateArray().ToList();
        events.Count.ShouldBe(3);
        events.ShouldAllBe(static e => e.Str("action") == "imported");
    }

    [Fact]
    public async Task Rate_types_are_tenant_data_with_the_system_ones_protected()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var created = await owner.PostAsync("/api/v1/organization/rate-types", new { code = "cbi_bulletin", name = new { en = "CBI bulletin", ar = "نشرة البنك المركزي" } });
        created.GetProperty("isSystem").GetBoolean().ShouldBeFalse();
        (await (await owner.PostAsJsonAsync("/api/v1/organization/rate-types", new { code = "spot", name = new { en = "Spot" } }, Json)).ErrorCodeAsync()).ShouldBe("rate_type.code_taken");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/rate-types", new { code = "Bad Code", name = new { en = "x" } }, Json)).ErrorCodeAsync()).ShouldBe("rate_type.code_invalid");

        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.GetOkAsync("/api/v1/organization/rate-types")).EnumerateArray().Select(static t => t.Str("code")).ShouldNotContain("cbi_bulletin");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Integration.Tests;

/// <summary>ADR-0009 idempotent mutations: same key and request replays the stored response, a different request is refused, concurrent replays execute once.</summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static HttpRequestMessage Post(string path, object body, string? key) =>
        new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
            Headers = { { "Idempotency-Key", key is null ? [] : [key] } },
        };

    [Fact]
    public async Task A_replayed_request_returns_the_stored_response_and_a_reused_key_is_refused()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var key = Guid.NewGuid().ToString();
        var body = new { code = "market_close", name = new { en = "Market close" } };

        var first = await owner.SendAsync(Post("/api/v1/organization/rate-types", body, key));
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        first.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        var created = await first.ReadJsonAsync();

        var replay = await owner.SendAsync(Post("/api/v1/organization/rate-types", body, key));
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await replay.ReadJsonAsync()).GetProperty("id").GetGuid().ShouldBe(created.GetProperty("id").GetGuid());
        (await (await owner.GetAsync("/api/v1/organization/rate-types")).ReadJsonAsync()).EnumerateArray().Count(static t => t.GetProperty("code").GetString() == "market_close").ShouldBe(1);

        var reused = await owner.SendAsync(Post("/api/v1/organization/rate-types", new { code = "other", name = new { en = "Other" } }, key));
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await reused.ErrorCodeAsync()).ShouldBe("idempotency.key_reused");

        // Without a key the request executes again and hits the real conflict; the same key from another member is its own scope.
        var again = await owner.SendAsync(Post("/api/v1/organization/rate-types", body, null));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ErrorCodeAsync()).ShouldBe("rate_type.code_taken");

        // Error responses are stored too (a client retrying a 4xx gets the same answer), server errors are not.
        var badKey = Guid.NewGuid().ToString();
        var bad = await owner.SendAsync(Post("/api/v1/organization/rate-types", new { code = "Bad Code", name = new { en = "x" } }, badKey));
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var badReplay = await owner.SendAsync(Post("/api/v1/organization/rate-types", new { code = "Bad Code", name = new { en = "x" } }, badKey));
        badReplay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badReplay.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await badReplay.ErrorCodeAsync()).ShouldBe("rate_type.code_invalid");

        var tooLong = await owner.SendAsync(Post("/api/v1/organization/rate-types", body, new string('k', 201)));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLong.ErrorCodeAsync()).ShouldBe("idempotency.key_invalid");
    }

    [Fact]
    public async Task Concurrent_replays_of_one_key_create_exactly_one_record()
    {
        var ws = await Api.SignupAsync();
        var key = Guid.NewGuid().ToString();
        var body = new { code = "RACECO", legalName = new { en = "Race Co" }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" };

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var client = Api.ClientFor(ws.AccessToken);
            var response = await client.SendAsync(Post("/api/v1/organization/companies", body, key));
            return (response.StatusCode, Body: await response.ReadJsonAsync(), Replayed: response.Headers.Contains("Idempotent-Replayed"));
        }));

        responses.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Created);
        responses.Select(static r => r.Body.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        responses.Count(static r => !r.Replayed).ShouldBe(1);
        using var owner = Api.ClientFor(ws.AccessToken);
        (await (await owner.GetAsync("/api/v1/organization/companies")).ReadJsonAsync()).EnumerateArray().Count(static c => c.GetProperty("code").GetString() == "RACECO").ShouldBe(1);
    }
}

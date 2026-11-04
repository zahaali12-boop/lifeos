using System.Net;
using System.Net.Http.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Integration.Tests;

/// <summary>Slice 1.9 rate limits: per principal when authenticated, per address for anonymous and auth endpoints; 429 problem details with Retry-After.</summary>
public sealed class RateLimitTests : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async ValueTask InitializeAsync() => _api = await ApiFixture.StartAsync(static builder =>
    {
        builder.UseSetting("Quicker:Api:RateLimit:Enabled", "true");
        builder.UseSetting("Quicker:Api:RateLimit:PerPrincipalPerMinute", "5");
        builder.UseSetting("Quicker:Api:RateLimit:AnonymousPerMinute", "50");
        builder.UseSetting("Quicker:Api:RateLimit:AuthPerMinute", "3");
    });

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Budgets_apply_per_principal_and_per_address_and_answer_429_with_retry_after()
    {
        var ws = await _api.SignupAsync();
        using var owner = _api.ClientFor(ws.AccessToken);
        for (var i = 0; i < 5; i++)
        {
            (await owner.GetAsync("/api/v1/organization/companies")).StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1} is within the budget");
        }

        var limited = await owner.GetAsync("/api/v1/organization/companies");
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        var problem = await limited.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("rate.limited");
        problem.GetProperty("why").GetProperty("retryAfterSeconds").GetInt32().ShouldBeGreaterThan(0);

        // Another principal of the same tenant has its own budget; the sign-in endpoint is stricter per address.
        var other = await _api.SignupAsync();
        using var second = _api.ClientFor(other.AccessToken);
        (await second.GetAsync("/api/v1/organization/companies")).StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpStatusCode last = default;
        for (var i = 0; i < 6; i++)
        {
            last = (await _api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@example.test", password = "wrong-password-value" }, ApiFixture.Json)).StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        last.ShouldBe(HttpStatusCode.TooManyRequests, "the auth budget per address is exhausted after three anonymous attempts");
    }
}

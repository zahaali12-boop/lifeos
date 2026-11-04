using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Migrator.Demo;
using Quicker.Testing;

namespace Quicker.Demo.Tests;

/// <summary>
/// Roadmap 5.1 acceptance: Customer 360 loads in under 300 ms at the 95th percentile on the demo tenant, through the
/// API as the web app calls it. It runs on a copy of the seeded demo, because signing in writes to the database the
/// restore drill counts.
/// </summary>
[Collection(DemoSeeding.Name)]
public sealed class Customer360Tests(BackedUpDemoFixture fixture)
{
    private static readonly TimeSpan Target = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Customer_360_loads_in_under_300_ms_at_the_95th_percentile_on_the_demo_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = ApiFixture.StartOn(await TestDatabase.CopyOfAsync(fixture.Db, ct));
        var login = await (await api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = DemoData.Owner.Email, password = DemoData.Password, tenantSlug = DemoData.Slug }, ApiFixture.Json, ct)).ReadJsonAsync();
        login.TryGetProperty("tokens", out var tokens).ShouldBeTrue(login.ToString());
        using var owner = api.ClientFor(tokens.GetProperty("accessToken").GetString()!);

        var accounts = await GetAsync(owner, "/api/v1/partners/customers", ct);
        var partners = accounts.EnumerateArray().Select(static a => a.GetProperty("partnerId").GetGuid()).Distinct().ToList();
        partners.Count.ShouldBe(6);

        // What the busiest customer's view holds: two companies, open deals in two currencies, the next steps.
        var basra = accounts.EnumerateArray().First(static a => a.GetProperty("partnerCode").GetString() == "CUS-BASRA-OIL").GetProperty("partnerId").GetGuid();
        var view = await GetAsync(owner, $"/api/v1/partners/{basra}/customer-360", ct);
        view.GetProperty("partner").GetProperty("customerAccounts").GetArrayLength().ShouldBe(2);
        view.GetProperty("partner").GetProperty("contacts").GetArrayLength().ShouldBe(2);
        view.GetProperty("pipeline").GetProperty("openTotals").EnumerateArray().Select(static t => t.GetProperty("currency").GetString()).ShouldBe(["IQD", "USD"]);
        view.GetProperty("pipeline").GetProperty("wonLastYear").GetInt32().ShouldBe(2);
        view.GetProperty("openActivities").GetArrayLength().ShouldBeGreaterThan(0);
        view.GetProperty("recentActivities").GetArrayLength().ShouldBeGreaterThan(0);

        // Warm the host and the database, then time every customer's view in turn.
        foreach (var partner in partners)
        {
            await GetAsync(owner, $"/api/v1/partners/{partner}/customer-360", ct);
        }

        var timings = new List<TimeSpan>();
        for (var i = 0; i < 120; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await GetAsync(owner, $"/api/v1/partners/{partners[i % partners.Count]}/customer-360", ct);
            timings.Add(Stopwatch.GetElapsedTime(started));
        }

        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        TestContext.Current.TestOutputHelper?.WriteLine($"Customer 360 on the demo tenant: median {timings[timings.Count / 2].TotalMilliseconds:0.0} ms, p95 {p95.TotalMilliseconds:0.0} ms, max {timings[^1].TotalMilliseconds:0.0} ms");
        p95.ShouldBeLessThan(Target);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), ct);
        var json = await response.ReadJsonAsync();
        response.IsSuccessStatusCode.ShouldBeTrue(json.ToString());
        return json;
    }
}

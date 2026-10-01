using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Invariants;

/// <summary>One API host and database shared by the harness tests; each test creates its own tenant.</summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Api = await ApiFixture.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "invariants-api";
}

internal static class InvariantsApi
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    public static async Task<JsonElement> PostAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    /// <summary>The harness report for the tenant (or a company) through the API.</summary>
    public static async Task<JsonElement> RunInvariantsAsync(this HttpClient client, Guid? companyId = null) =>
        await client.PostAsync("/api/v1/platform/integrity/run" + (companyId is { } c ? $"?companyId={c}" : string.Empty), new { }, HttpStatusCode.OK);

    public static JsonElement Check(this JsonElement report, string code) =>
        report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    /// <summary>A company on the IFRS template (chart and first posting profile in one call) with two journals posted.</summary>
    public static async Task<Guid> CompanyWithPostingsAsync(this HttpClient owner, string code)
    {
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code, legalName = new { en = code + " Trading", ar = "شركة " + code }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "CH-" + code, companyId });
        foreach (var (date, amount) in new[] { ("2026-09-05", 1500000m), ("2026-09-20", 250000m) })
        {
            var journal = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new
            {
                postingDate = date,
                currency = "IQD",
                description = new { en = "Rent " + date, ar = "إيجار " + date },
                lines = new object[] { new { accountCode = "6110", debit = amount }, new { accountCode = "2170", credit = amount } },
            });
            (await owner.PostAsync($"/api/v1/accounting/journals/{journal.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
        }

        return companyId;
    }
}

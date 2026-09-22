using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Numbering.Tests;

/// <summary>One API host and database shared by the numbering test classes; each test creates its own tenant.</summary>
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
    public const string Name = "numbering-api";
}

internal static class NumberingApi
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    public static async Task<JsonElement> PostAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    public static async Task<JsonElement> PutAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    public static async Task<JsonElement> GetOkAsync(this HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
        return json;
    }

    public static async Task<string?> PostErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<string?> PutErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>An IQD company in Iraq on the tenant's default (January) fiscal calendar, with one branch.</summary>
    public static async Task<(Guid CompanyId, Guid BranchId)> CompanyWithBranchAsync(this HttpClient client, string code)
    {
        var company = await client.PostAsync("/api/v1/organization/companies", new
        {
            code,
            legalName = new { en = code + " LLC", ar = "شركة " + code },
            country = "IQ",
            functionalCurrency = "IQD",
            timeZone = "Asia/Baghdad",
        });
        var companyId = company.GetProperty("id").GetGuid();
        var branch = await client.PostAsync($"/api/v1/organization/companies/{companyId}/branches", new { code = "BGW", name = new { en = "Baghdad", ar = "بغداد" } });
        return (companyId, branch.GetProperty("id").GetGuid());
    }

    public static Task<JsonElement> CreateSeriesAsync(this HttpClient client, string code, string documentType, Guid companyId, string template, Guid? branchId = null, Guid? fiscalYearId = null,
        bool gapless = true, string resetPolicy = "never", long startNumber = 1, bool isDefault = false, string? validFrom = null, string? validTo = null) =>
        client.PostAsync("/api/v1/numbering/series", new { code, documentType, companyId, template, branchId, fiscalYearId, gapless, resetPolicy, startNumber, isDefault, validFrom, validTo });

    public static async Task<JsonElement> AllocateAsync(this HttpClient client, string documentType, Guid companyId, string date, Guid? branchId = null, Guid? seriesId = null, Guid? documentId = null) =>
        await client.PostAsync("/api/v1/numbering/allocate", new { documentType, companyId, date, documentId = documentId ?? Guid.NewGuid(), branchId, seriesId }, HttpStatusCode.OK);

    public static string? Str(this JsonElement e, string property) => e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Accounting.Tests;

/// <summary>One API host and database shared by the accounting test classes; each test creates its own tenant.</summary>
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
    public const string Name = "accounting-api";
}

internal static class AccountingApi
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    public static async Task<JsonElement> PostAsync(this HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    public static async Task<JsonElement> PostContentAsync(this HttpClient client, string path, HttpContent content, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsync(new Uri(path, UriKind.Relative), content);
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

    public static async Task<(string Code, JsonElement Problem)> PostErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    public static async Task<(string Code, JsonElement Problem)> PutErrorAsync(this HttpClient client, string path, object body, HttpStatusCode expected)
    {
        var response = await client.PutAsJsonAsync(path, body, Json);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return (json.GetProperty("code").GetString()!, json);
    }

    /// <summary>A company on the default calendars (IQD functional) through the organization API.</summary>
    public static async Task<Guid> CompanyAsync(this HttpClient client, string code, string currency = "IQD")
    {
        var company = await client.PostAsync("/api/v1/organization/companies", new
        {
            code,
            legalName = new { en = code + " Trading", ar = "شركة " + code },
            country = "IQ",
            functionalCurrency = currency,
            timeZone = "Asia/Baghdad",
        });
        return company.GetProperty("id").GetGuid();
    }

    /// <summary>A chart from a template, assigned to the company when one is given.</summary>
    public static async Task<JsonElement> ChartFromTemplateAsync(this HttpClient client, string template, string code, Guid? companyId = null) =>
        await client.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = template, code, companyId });

    public static async Task<(Guid DimensionId, Guid ValueId)> DimensionValueAsync(this HttpClient client, string dimensionCode, string valueCode)
    {
        var dimension = (await client.GetOkAsync("/api/v1/organization/dimensions")).EnumerateArray().Single(d => d.GetProperty("code").GetString() == dimensionCode);
        var dimensionId = dimension.GetProperty("id").GetGuid();
        var value = await client.PostAsync($"/api/v1/organization/dimensions/{dimensionId}/values", new { code = valueCode, name = new { en = valueCode, ar = valueCode } });
        return (dimensionId, value.GetProperty("id").GetGuid());
    }
}

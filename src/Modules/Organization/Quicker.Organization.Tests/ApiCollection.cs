using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Organization.Tests;

/// <summary>
/// One API host and database shared by the organization test classes; each test creates its own tenant. A local
/// HTTP stub stands in for the ECB and Open Exchange Rates feeds so provider import is exercised end to end.
/// </summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public const string EcbSample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <gesmes:subject>Reference rates</gesmes:subject>
          <gesmes:Sender><gesmes:name>European Central Bank</gesmes:name></gesmes:Sender>
          <Cube>
            <Cube time="2026-09-21">
              <Cube currency="USD" rate="1.1750"/>
              <Cube currency="GBP" rate="0.8650"/>
              <Cube currency="JPY" rate="173.25"/>
            </Cube>
          </Cube>
        </gesmes:Envelope>
        """;

    // 1789948800 = 2026-09-21T00:00:00Z
    public const string OxrSample = """{"disclaimer":"test","license":"test","timestamp":1789948800,"base":"USD","rates":{"AED":3.6725,"IQD":1310.5,"EUR":0.851,"XXX":1}}""";

    private HttpListener? _listener;
    private Task? _serving;

    public ApiFixture Api { get; private set; } = null!;

    public string StubBaseUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        StubBaseUrl = StartStub();
        Api = await ApiFixture.StartAsync(builder =>
        {
            builder.UseSetting("Quicker:Organization:RateProviders:EcbUrl", StubBaseUrl + "ecb-daily.xml");
            builder.UseSetting("Quicker:Organization:RateProviders:EcbHistoryUrl", StubBaseUrl + "ecb-daily.xml");
            builder.UseSetting("Quicker:Organization:RateProviders:OpenExchangeRatesUrl", StubBaseUrl + "oxr");
            builder.UseSetting("Quicker:Organization:RateProviders:OpenExchangeRatesAppId", "test-app-id");
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Api.DisposeAsync();
        _listener?.Stop();
        if (_serving is not null)
        {
            try
            {
                await _serving;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }
    }

    private string StartStub()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _serving = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                var query = context.Request.QueryString;
                string body;
                if (path.EndsWith("ecb-daily.xml", StringComparison.Ordinal))
                {
                    context.Response.ContentType = "application/xml";
                    body = EcbSample;
                }
                else if (path.Contains("/oxr/", StringComparison.Ordinal) && query["app_id"] == "test-app-id")
                {
                    context.Response.ContentType = "application/json";
                    body = OxrSample;
                }
                else
                {
                    context.Response.StatusCode = 404;
                    body = "not found";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        return prefix;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "organization-api";
}

internal static class OrganizationApi
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

    public static async Task<string?> GetErrorAsync(this HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(expected);
        return await response.ErrorCodeAsync();
    }

    /// <summary>A company in Iraq (Sunday–Thursday week, Asia/Baghdad) on the given fiscal calendar (the tenant default when null).</summary>
    public static Task<JsonElement> CreateCompanyAsync(this HttpClient client, string code, string functionalCurrency = "IQD", Guid? fiscalCalendarId = null, string? reportingCurrency = null) =>
        client.PostAsync("/api/v1/organization/companies", new
        {
            code,
            legalName = new { en = code + " Trading LLC", ar = "شركة " + code },
            country = "IQ",
            functionalCurrency,
            reportingCurrency,
            timeZone = "Asia/Baghdad",
            fiscalCalendarId,
        });

    public static async Task<Guid> UomIdAsync(this HttpClient client, string code) =>
        (await client.GetOkAsync("/api/v1/organization/uoms")).EnumerateArray().Single(u => u.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    public static async Task<JsonElement> DimensionAsync(this HttpClient client, string code) =>
        (await client.GetOkAsync("/api/v1/organization/dimensions")).EnumerateArray().Single(d => d.GetProperty("code").GetString() == code);

    public static string? Str(this JsonElement e, string property) => e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
}

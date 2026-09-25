using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Quicker.Kernel.Results;

namespace Quicker.Organization.Application;

public sealed record ProviderQuote(string From, string To, decimal Rate);

public sealed record ProviderRates(string BaseCurrency, DateOnly Date, IReadOnlyList<ProviderQuote> Rates);

/// <summary>An external source of exchange rates, imported into org_exchange_rates with its code as the source.</summary>
public interface IExchangeRateProvider
{
    string Code { get; }

    /// <summary>Latest rates, or the rates of <paramref name="date"/> when the provider keeps history; base currency where the provider allows it.</summary>
    Task<Result<ProviderRates>> FetchAsync(string? baseCurrency, DateOnly? date, CancellationToken cancellationToken);
}

public sealed class RateProviderOptions
{
    public const string SectionName = "Quicker:Organization:RateProviders";

    public string EcbUrl { get; set; } = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";

    public string EcbHistoryUrl { get; set; } = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist-90d.xml";

    public string OpenExchangeRatesUrl { get; set; } = "https://openexchangerates.org/api";

    /// <summary>Open Exchange Rates app id; the provider is registered but refuses to fetch without it.</summary>
    public string? OpenExchangeRatesAppId { get; set; }
}

/// <summary>European Central Bank euro reference rates (EUR base, daily; last 90 days for a dated import).</summary>
public sealed class EcbRateProvider(HttpClient http, IOptions<RateProviderOptions> options) : IExchangeRateProvider
{
    public const string ProviderCode = "ecb";

    public string Code => ProviderCode;

    public async Task<Result<ProviderRates>> FetchAsync(string? baseCurrency, DateOnly? date, CancellationToken cancellationToken)
    {
        if (baseCurrency is not null && baseCurrency != "EUR")
        {
            return Error.Validation("rate_provider.base_unsupported", "The ECB publishes euro reference rates only.").WithWhy(("provider", Code), ("base", "EUR"));
        }

        var url = date is null ? options.Value.EcbUrl : options.Value.EcbHistoryUrl;
        string xml;
        try
        {
            xml = await http.GetStringAsync(new Uri(url), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return Error.Conflict("rate_provider.unavailable", $"The ECB feed could not be read: {ex.Message}").WithWhy(("provider", Code));
        }

        return Parse(xml, date);
    }

    /// <summary>Parses the ECB envelope; with a date, picks that day's cube (or fails when it is not in the feed).</summary>
    public static Result<ProviderRates> Parse(string xml, DateOnly? date = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            return Error.Conflict("rate_provider.malformed", $"The ECB feed is not valid XML: {ex.Message}");
        }

        XNamespace ns = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
        var cubes = document.Descendants(ns + "Cube").Where(static c => c.Attribute("time") is not null).ToList();
        var cube = date is null
            ? cubes.OrderByDescending(static c => c.Attribute("time")!.Value, StringComparer.Ordinal).FirstOrDefault()
            : cubes.FirstOrDefault(c => c.Attribute("time")!.Value == date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (cube is null)
        {
            return Error.Conflict("rate_provider.no_rates", date is null ? "The ECB feed contains no rates." : $"The ECB feed has no rates for {date:yyyy-MM-dd}.");
        }

        var day = DateOnly.ParseExact(cube.Attribute("time")!.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var quotes = new List<ProviderQuote>();
        foreach (var rate in cube.Elements(ns + "Cube"))
        {
            var currency = rate.Attribute("currency")?.Value;
            var value = rate.Attribute("rate")?.Value;
            if (currency is { Length: 3 } && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0m)
            {
                quotes.Add(new ProviderQuote("EUR", currency.ToUpperInvariant(), parsed));
            }
        }

        return new ProviderRates("EUR", day, quotes);
    }
}

/// <summary>openexchangerates.org: latest or historical rates for any base the plan allows.</summary>
public sealed class OpenExchangeRatesProvider(HttpClient http, IOptions<RateProviderOptions> options) : IExchangeRateProvider
{
    public const string ProviderCode = "openexchangerates";

    public string Code => ProviderCode;

    public async Task<Result<ProviderRates>> FetchAsync(string? baseCurrency, DateOnly? date, CancellationToken cancellationToken)
    {
        var appId = options.Value.OpenExchangeRatesAppId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            return Error.Conflict("rate_provider.not_configured", "Open Exchange Rates needs Quicker:Organization:RateProviders:OpenExchangeRatesAppId.").WithWhy(("provider", Code));
        }

        var path = date is null ? "latest.json" : $"historical/{date:yyyy-MM-dd}.json";
        var url = $"{options.Value.OpenExchangeRatesUrl.TrimEnd('/')}/{path}?app_id={Uri.EscapeDataString(appId)}&base={Uri.EscapeDataString(baseCurrency ?? "USD")}";
        JsonDocument? document;
        try
        {
            document = await http.GetFromJsonAsync<JsonDocument>(new Uri(url), cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return Error.Conflict("rate_provider.unavailable", $"Open Exchange Rates could not be read: {ex.Message}").WithWhy(("provider", Code));
        }
        catch (JsonException ex)
        {
            return Error.Conflict("rate_provider.malformed", $"Open Exchange Rates returned invalid JSON: {ex.Message}");
        }

        using (document)
        {
            return document is null ? Error.Conflict("rate_provider.no_rates", "Open Exchange Rates returned nothing.") : Parse(document.RootElement);
        }
    }

    public static Result<ProviderRates> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
        {
            return Error.Conflict("rate_provider.malformed", "Open Exchange Rates response has no 'rates' object.");
        }

        var baseCurrency = root.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()!.ToUpperInvariant() : "USD";
        var day = root.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unix)
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime)
            : throw new InvalidOperationException("Open Exchange Rates response has no timestamp.");
        var quotes = new List<ProviderQuote>();
        foreach (var property in rates.EnumerateObject())
        {
            if (property.Name.Length == 3 && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var rate) && rate > 0m)
            {
                quotes.Add(new ProviderQuote(baseCurrency, property.Name.ToUpperInvariant(), rate));
            }
        }

        return new ProviderRates(baseCurrency, day, quotes);
    }
}

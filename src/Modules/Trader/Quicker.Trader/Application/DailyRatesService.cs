using System.Globalization;
using System.Text.Json;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Trader.Contracts;

namespace Quicker.Trader.Application;

/// <summary>
/// The day's exchange rate (ADR-0035 §14, A-197). From the hour the trader chose, the first open of the day asks for
/// today's spot rate of every currency the shop buys in that has none entered for the day; what is entered goes into
/// the organization's rates like a rate typed under Exchange rates, so every document of the day converts at it and
/// the pricing board re-values at it. A rate of the day already entered is corrected only with a reason (ADR-0017).
/// </summary>
public sealed class DailyRatesService(
    ICompanyDirectory companies,
    ICompanySettings settings,
    IExchangeRateResolver resolver,
    IExchangeRateEntry entry,
    IPurchaseLandedCosts landedCosts,
    IClock clock)
{
    public const int DefaultPromptHour = 8;

    public async Task<Result<TraderDailyRates>> ReadAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        return await ReadAsync(companyId, company, cancellationToken);
    }

    public async Task<Result<TraderDailyRates>> EnterAsync(TraderDailyRatesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.Rates is not { Count: > 0 })
        {
            return Error.Validation("rates.empty", "Enter at least one rate.");
        }

        var functional = company.FunctionalCurrency.Code;
        var lines = new List<(string Currency, decimal Rate)>();
        foreach (var line in request.Rates)
        {
            var currency = (line.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (currency.Length != 3)
            {
                return Error.Validation("rates.currency_invalid", "A currency is its three-letter ISO code.").WithWhy(("currency", line.Currency ?? string.Empty));
            }

            if (string.Equals(currency, functional, StringComparison.Ordinal))
            {
                return Error.Validation("rates.same_currency", $"{functional} is the company's own currency: it needs no rate.").WithWhy(("currency", currency));
            }

            if (line.Rate <= 0m)
            {
                return Error.Validation("rates.not_positive", $"The rate of {currency} is a positive number.").WithWhy(("currency", currency));
            }

            lines.Add((currency, line.Rate));
        }

        var today = clock.TodayIn(company.TimeZone);
        foreach (var (currency, rate) in lines)
        {
            var saved = await entry.SaveAsync(currency, functional, today, rate, request.Reason, RateTypes.Spot, cancellationToken);
            if (saved.IsFailure)
            {
                return saved.Error!;
            }
        }

        return await ReadAsync(request.CompanyId, company, cancellationToken);
    }

    private async Task<TraderDailyRates> ReadAsync(Guid companyId, CompanyInfo company, CancellationToken cancellationToken)
    {
        var functional = company.FunctionalCurrency.Code;
        var now = TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(company.TimeZone));
        var today = DateOnly.FromDateTime(now.DateTime);
        var hour = await PromptHourAsync(companyId, cancellationToken);
        var seen = (await landedCosts.LatestByItemAsync(companyId, cancellationToken)).Select(static l => l.Currency);
        var rates = new List<TraderDailyRate>();
        foreach (var currency in await TraderCurrencies.ForAsync(settings, companyId, functional, seen, cancellationToken))
        {
            var resolved = await resolver.ResolveAsync(company.Id, currency, functional, today, RateTypes.Spot, cancellationToken);
            rates.Add(resolved.IsSuccess
                ? new TraderDailyRate(currency, resolved.Value.Rate.Rate, resolved.Value.EffectiveFrom, resolved.Value.EffectiveFrom == today)
                : new TraderDailyRate(currency, null, null, false));
        }

        var due = hour is >= 0 and <= 23 && now.Hour >= hour && rates.Any(static r => !r.EnteredToday);
        return new TraderDailyRates(companyId, functional, today, hour, due, rates);
    }

    private async Task<int> PromptHourAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var setting = await settings.GetAsync(companyId, TraderSettingKeys.RatesPromptHour, cancellationToken);
        return setting switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var hour) => hour,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) => hour,
            _ => DefaultPromptHour,
        };
    }
}

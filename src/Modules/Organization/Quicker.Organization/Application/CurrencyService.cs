using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Persistence;

namespace Quicker.Organization.Application;

/// <summary>ISO currencies, rate types, effective-dated rates, resolution with inverse and cross rates, provider import (ADR-0017).</summary>
public sealed class CurrencyService(OrganizationDbContext db, IUnitOfWorkAccessor unitOfWork, IEnumerable<IExchangeRateProvider> providers, IAuditSink audit, IClock clock) : IExchangeRateResolver
{
    private Guid? ActorUserId => unitOfWork.Current.Context.UserId?.Value;

    // ------------------------------------------------------------------ currencies

    public async Task<IReadOnlyList<CurrencySummary>> ListCurrenciesAsync(CancellationToken cancellationToken) =>
        (await db.IsoCurrencies.OrderBy(static c => c.Code).ToListAsync(cancellationToken))
            .Select(static c => new CurrencySummary(c.Code, c.NumericCode, c.MinorUnits, c.Symbol, c.Name.Values, c.IsActive)).ToList();

    // ------------------------------------------------------------------ rate types

    public async Task<IReadOnlyList<RateTypeSummary>> ListRateTypesAsync(CancellationToken cancellationToken) =>
        (await db.RateTypes.OrderBy(static t => t.IsSystem ? 0 : 1).ThenBy(static t => t.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<RateTypeSummary>> CreateRateTypeAsync(SaveRateTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.LowerCode(request.Code, "rate_type");
        var name = Validation.Name(request.Name, "rate_type");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (await db.RateTypes.AnyAsync(t => t.Code == code.Value, cancellationToken))
        {
            return Error.Conflict("rate_type.code_taken", $"A rate type with code '{code.Value}' already exists.");
        }

        var now = clock.UtcNow;
        var type = new ExchangeRateType { Id = Guid.CreateVersion7(), Code = code.Value, Name = name.Value, CreatedAt = now, UpdatedAt = now };
        db.RateTypes.Add(type);
        await db.SaveChangesAsync(cancellationToken);
        return Map(type);
    }

    // ------------------------------------------------------------------ rates

    public async Task<Result<IReadOnlyList<RateSummary>>> ListRatesAsync(string? rateType, string? from, string? to, DateOnly? fromDate, DateOnly? toDate, int limit, CancellationToken cancellationToken)
    {
        var query = from r in db.Rates
                    join t in db.RateTypes on new { r.TenantId, Id = r.RateTypeId } equals new { t.TenantId, t.Id }
                    select new { r, t };
        if (!string.IsNullOrWhiteSpace(rateType))
        {
            var code = rateType.Trim().ToLowerInvariant();
            query = query.Where(x => x.t.Code == code);
        }

        if (!string.IsNullOrWhiteSpace(from))
        {
            var code = from.Trim().ToUpperInvariant();
            query = query.Where(x => x.r.FromCurrency == code);
        }

        if (!string.IsNullOrWhiteSpace(to))
        {
            var code = to.Trim().ToUpperInvariant();
            query = query.Where(x => x.r.ToCurrency == code);
        }

        if (fromDate is { } f)
        {
            query = query.Where(x => x.r.ValidFrom >= f);
        }

        if (toDate is { } tdate)
        {
            query = query.Where(x => x.r.ValidFrom <= tdate);
        }

        var rows = await query.OrderByDescending(static x => x.r.ValidFrom).ThenBy(static x => x.r.FromCurrency).ThenBy(static x => x.r.ToCurrency)
            .Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
        return rows.Select(x => Map(x.r, x.t.Code)).ToList();
    }

    public async Task<Result<RateSummary>> SaveRateAsync(SaveRateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var typeCode = Validation.LowerCode(request.RateType, "rate_type");
        var from = Validation.CurrencyCode(request.FromCurrency, "rate");
        var to = Validation.CurrencyCode(request.ToCurrency, "rate");
        if (typeCode.IsFailure || from.IsFailure || to.IsFailure)
        {
            return typeCode.Error ?? from.Error ?? to.Error!;
        }

        if (from.Value == to.Value)
        {
            return Error.Validation("rate.same_currency", "A rate needs two different currencies.");
        }

        if (request.Rate <= 0m)
        {
            return Error.Validation("rate.not_positive", "A rate is a positive number.");
        }

        var type = await db.RateTypes.SingleOrDefaultAsync(t => t.Code == typeCode.Value, cancellationToken);
        if (type is null)
        {
            return Error.NotFound("rate_type", typeCode.Value);
        }

        var known = await db.IsoCurrencies.Where(c => c.Code == from.Value || c.Code == to.Value).CountAsync(cancellationToken);
        if (known != 2)
        {
            return Error.Validation("rate.currency_unknown", "Both currencies must be in the ISO 4217 list.").WithWhy(("from", from.Value), ("to", to.Value));
        }

        var rate = await db.Rates.SingleOrDefaultAsync(r => r.RateTypeId == type.Id && r.FromCurrency == from.Value && r.ToCurrency == to.Value && r.ValidFrom == request.ValidFrom, cancellationToken);
        if (rate is null)
        {
            rate = new RateEntry { Id = Guid.CreateVersion7(), RateTypeId = type.Id, FromCurrency = from.Value, ToCurrency = to.Value, ValidFrom = request.ValidFrom, CreatedAt = clock.UtcNow };
            db.Rates.Add(rate);
        }
        else if (string.IsNullOrWhiteSpace(request.Reason))
        {
            // Correcting a stored rate changes what documents would convert at from that date on; say why (ADR-0017).
            return Error.Validation("rate.reason_required", "Changing an existing rate requires a reason.").WithWhy(("existingRate", rate.Rate), ("validFrom", rate.ValidFrom));
        }

        rate.Rate = request.Rate;
        rate.Source = "manual";
        rate.EnteredBy = ActorUserId;
        rate.Reason = request.Reason?.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return Map(rate, type.Code);
    }

    public async Task<Result> DeleteRateAsync(Guid rateId, string? reason, CancellationToken cancellationToken)
    {
        var rate = await db.Rates.SingleOrDefaultAsync(r => r.Id == rateId, cancellationToken);
        if (rate is null)
        {
            return Error.NotFound("exchange_rate", rateId);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("rate.reason_required", "Deleting a rate requires a reason.");
        }

        db.Rates.Remove(rate);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("exchange_rate", rate.Id, $"{rate.FromCurrency}/{rate.ToCurrency} {rate.ValidFrom:yyyy-MM-dd}", AuditActions.Deleted, Reason: reason.Trim()), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ resolution

    public async Task<Result<RateResolution>> ResolveForApiAsync(Guid companyId, string from, string to, DateOnly date, string rateType, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(new CompanyId(companyId), from, to, date, rateType, cancellationToken);
        return resolved.IsFailure
            ? resolved.Error!
            : new RateResolution(resolved.Value.Rate.From.Code, resolved.Value.Rate.To.Code, date, resolved.Value.Rate.RateType, resolved.Value.Rate.Rate, resolved.Value.Method, resolved.Value.EffectiveFrom, resolved.Value.RateIds);
    }

    public async Task<Result<ResolvedRate>> ResolveAsync(CompanyId companyId, string fromCurrency, string toCurrency, DateOnly date, string rateType = RateTypes.Spot, CancellationToken cancellationToken = default)
    {
        var from = Validation.CurrencyCode(fromCurrency, "rate");
        var to = Validation.CurrencyCode(toCurrency, "rate");
        var typeCode = Validation.LowerCode(rateType, "rate_type");
        if (from.IsFailure || to.IsFailure || typeCode.IsFailure)
        {
            return from.Error ?? to.Error ?? typeCode.Error!;
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId.Value, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId.Value);
        }

        var currencies = await db.IsoCurrencies.Where(c => c.Code == from.Value || c.Code == to.Value || c.Code == company.FunctionalCurrency).ToDictionaryAsync(static c => c.Code, static c => Currency.Of(c.Code, c.MinorUnits), StringComparer.Ordinal, cancellationToken);
        if (!currencies.TryGetValue(from.Value, out var source) || !currencies.TryGetValue(to.Value, out var target))
        {
            return Error.Validation("rate.currency_unknown", "Both currencies must be in the ISO 4217 list.").WithWhy(("from", from.Value), ("to", to.Value));
        }

        if (from.Value == to.Value)
        {
            return new ResolvedRate(ExchangeRate.Identity(source, date), "identity", date, []);
        }

        var type = await db.RateTypes.SingleOrDefaultAsync(t => t.Code == typeCode.Value, cancellationToken);
        if (type is null)
        {
            return Error.NotFound("rate_type", typeCode.Value);
        }

        var direct = await LatestAsync(type.Id, from.Value, to.Value, date, cancellationToken);
        if (direct is not null)
        {
            return new ResolvedRate(new ExchangeRate(source, target, direct.Rate, date, type.Code), "direct", direct.ValidFrom, [direct.Id]);
        }

        var inverse = await LatestAsync(type.Id, to.Value, from.Value, date, cancellationToken);
        if (inverse is not null)
        {
            return new ResolvedRate(new ExchangeRate(source, target, 1m / inverse.Rate, date, type.Code), "inverse", inverse.ValidFrom, [inverse.Id]);
        }

        // Cross rate through the functional currency only (ADR-0017): no triangulation through arbitrary currencies.
        var functional = company.FunctionalCurrency;
        if (functional != from.Value && functional != to.Value)
        {
            var leg1 = await LegAsync(type.Id, from.Value, functional, date, cancellationToken);
            var leg2 = await LegAsync(type.Id, functional, to.Value, date, cancellationToken);
            if (leg1 is not null && leg2 is not null)
            {
                var pivot = currencies[functional];
                var chained = new ExchangeRate(source, pivot, leg1.Value.Rate, date, type.Code).Then(new ExchangeRate(pivot, target, leg2.Value.Rate, date, type.Code));
                var effective = leg1.Value.ValidFrom > leg2.Value.ValidFrom ? leg1.Value.ValidFrom : leg2.Value.ValidFrom;
                return new ResolvedRate(chained, "cross", effective, [leg1.Value.Id, leg2.Value.Id]);
            }
        }

        return Error.Conflict("rate.not_found", $"No {type.Code} rate from {from.Value} to {to.Value} on or before {date:yyyy-MM-dd}, directly or through {functional}.")
            .WithWhy(("from", from.Value), ("to", to.Value), ("date", date), ("rateType", type.Code), ("functionalCurrency", functional));
    }

    private async Task<(decimal Rate, DateOnly ValidFrom, Guid Id)?> LegAsync(Guid rateTypeId, string from, string to, DateOnly date, CancellationToken cancellationToken)
    {
        var direct = await LatestAsync(rateTypeId, from, to, date, cancellationToken);
        if (direct is not null)
        {
            return (direct.Rate, direct.ValidFrom, direct.Id);
        }

        var inverse = await LatestAsync(rateTypeId, to, from, date, cancellationToken);
        return inverse is null ? null : (1m / inverse.Rate, inverse.ValidFrom, inverse.Id);
    }

    private Task<RateEntry?> LatestAsync(Guid rateTypeId, string from, string to, DateOnly date, CancellationToken cancellationToken) =>
        db.Rates.Where(r => r.RateTypeId == rateTypeId && r.FromCurrency == from && r.ToCurrency == to && r.ValidFrom <= date)
            .OrderByDescending(static r => r.ValidFrom).FirstOrDefaultAsync(cancellationToken);

    // ------------------------------------------------------------------ provider import

    public IReadOnlyList<string> ProviderCodes => providers.Select(static p => p.Code).OrderBy(static c => c, StringComparer.Ordinal).ToList();

    public async Task<Result<ImportRatesResult>> ImportAsync(ImportRatesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = providers.FirstOrDefault(p => string.Equals(p.Code, request.Provider?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return Error.NotFound("rate_provider", request.Provider ?? string.Empty).WithWhy(("available", ProviderCodes));
        }

        var typeCode = Validation.LowerCode(request.RateType, "rate_type");
        if (typeCode.IsFailure)
        {
            return typeCode.Error!;
        }

        var type = await db.RateTypes.SingleOrDefaultAsync(t => t.Code == typeCode.Value, cancellationToken);
        if (type is null)
        {
            return Error.NotFound("rate_type", typeCode.Value);
        }

        var wanted = request.Currencies is { Count: > 0 } ? request.Currencies.Select(static c => c.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal) : null;
        var fetched = await provider.FetchAsync(request.BaseCurrency?.Trim().ToUpperInvariant(), request.Date, cancellationToken);
        if (fetched.IsFailure)
        {
            return fetched.Error!;
        }

        var known = await db.IsoCurrencies.Select(static c => c.Code).ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
        var imported = 0;
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;
        var currencies = new SortedSet<string>(StringComparer.Ordinal);
        var date = fetched.Value.Date;
        foreach (var quote in fetched.Value.Rates)
        {
            if (quote.From == quote.To || !known.Contains(quote.From) || !known.Contains(quote.To) || (wanted is not null && !wanted.Contains(quote.To)) || quote.Rate <= 0m)
            {
                skipped++;
                continue;
            }

            var rate = await db.Rates.SingleOrDefaultAsync(r => r.RateTypeId == type.Id && r.FromCurrency == quote.From && r.ToCurrency == quote.To && r.ValidFrom == date, cancellationToken);
            var rounded = decimal.Round(quote.Rate, 12, MidpointRounding.AwayFromZero);
            if (rate is null)
            {
                db.Rates.Add(new RateEntry { Id = Guid.CreateVersion7(), RateTypeId = type.Id, FromCurrency = quote.From, ToCurrency = quote.To, ValidFrom = date, Rate = rounded, Source = provider.Code, EnteredBy = ActorUserId, CreatedAt = clock.UtcNow });
                imported++;
            }
            else if (rate.Source == "manual")
            {
                // A rate somebody entered by hand on that date wins over the feed; the feed never overwrites a decision.
                skipped++;
                continue;
            }
            else if (rate.Rate != rounded)
            {
                rate.Rate = rounded;
                rate.Source = provider.Code;
                rate.EnteredBy = ActorUserId;
                updated++;
            }
            else
            {
                unchanged++;
            }

            currencies.Add(quote.To);
        }

        await db.SaveChangesAsync(cancellationToken);
        var result = new ImportRatesResult(provider.Code, type.Code, date, imported, updated, unchanged, skipped, currencies.ToList());
        await audit.RecordAsync(new AuditEntry("exchange_rates", type.Id, $"{provider.Code} {date:yyyy-MM-dd}", "imported",
            Details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["provider"] = provider.Code, ["base"] = fetched.Value.BaseCurrency, ["date"] = date, ["imported"] = imported, ["updated"] = updated, ["unchanged"] = unchanged, ["skipped"] = skipped }), cancellationToken);
        return result;
    }

    // ------------------------------------------------------------------ mapping

    private static RateTypeSummary Map(ExchangeRateType t) => new(t.Id, t.Code, t.Name.Values, t.IsSystem);

    private static RateSummary Map(RateEntry r, string typeCode) => new(r.Id, typeCode, r.FromCurrency, r.ToCurrency, r.ValidFrom, r.Rate, r.Source, r.EnteredBy, r.Reason, r.CreatedAt);
}

using Microsoft.EntityFrameworkCore;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Engine;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>
/// Picks the code of every document line (ADR-0018, A-146). The company's registration on the tax date names the regime;
/// without one the company charges and recovers no tax. On a sale, a partner's exemption certificate valid on the date
/// relieves the tax a line would otherwise carry. Otherwise the matrix row that matches the line most specifically wins:
/// an item group counts 8, a partner group 4, where goods ship from 2 and to 1, then the latest start; a line no row
/// matches is refused with the facts that were looked up, never taxed by guess.
/// </summary>
public sealed class TaxDeterminationService(TaxDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, ICustomerDirectory customers, IItemDirectory items) : ITaxDetermination
{
    private readonly Dictionary<Guid, TaxCode?> _codes = [];
    private readonly Dictionary<Guid, TaxRegime?> _regimes = [];

    public async Task<Result<TaxDetermination>> DetermineAsync(TaxDeterminationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await ScopeAsync(request.CompanyId, request.Direction, request.TaxDate, request.PartnerId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var from = request.ShipFromCountry ?? (request.Direction == TaxDirections.Sales ? scope.Value.Company.Country : null);
        var to = request.ShipToCountry ?? (request.Direction == TaxDirections.Purchase ? scope.Value.Company.Country : null);
        return await DetermineAsync(scope.Value, request.ItemTaxGroupId, request.PartnerTaxGroupId, from, to, cancellationToken);
    }

    public async Task<Result<TaxCodeInfo>> FindCodeAsync(Guid taxCodeId, DateOnly taxDate, CancellationToken cancellationToken = default)
    {
        var code = await CodeAsync(taxCodeId, cancellationToken);
        if (code is null)
        {
            return Error.Validation("tax.code_unknown", "There is no such tax code.").WithWhy(("taxCodeId", taxCodeId));
        }

        return await InfoAsync(code, taxDate, cancellationToken);
    }

    public async Task<Result<TaxedDocumentResult>> CalculateAsync(TaxDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = request.Lines ?? [];
        if (lines.Select(static l => l.Key).Distinct(StringComparer.Ordinal).Count() != lines.Count || lines.Any(static l => string.IsNullOrEmpty(l.Key)))
        {
            return Error.Validation("tax.line_keys_invalid", "Every line has its own key.");
        }

        if (await companies.FindCurrencyAsync(request.Currency ?? string.Empty, cancellationToken) is not { } currency)
        {
            return Error.Validation("tax.currency_unknown", "The currency is not known.").WithWhy(("currency", request.Currency));
        }

        var scoped = await ScopeAsync(request.CompanyId, request.Direction, request.TaxDate, request.PartnerId, cancellationToken);
        if (scoped.IsFailure)
        {
            return scoped.Error!;
        }

        var scope = scoped.Value;
        var partnerGroup = request.PartnerTaxGroupId;
        if (partnerGroup is null && request.PartnerId is { } partnerId)
        {
            partnerGroup = request.Direction == TaxDirections.Sales
                ? (await customers.FindCustomerAsync(request.CompanyId, partnerId, cancellationToken))?.TaxGroupId
                : (await partners.FindSupplierAsync(request.CompanyId, partnerId, cancellationToken))?.TaxGroupId;
        }

        var inputs = new List<TaxLineInput>(lines.Count);
        var determinations = new List<TaxLineDetermination>(lines.Count);
        foreach (var line in lines)
        {
            var itemGroup = line.ItemTaxGroupId;
            if (itemGroup is null && line.ItemId is { } itemId)
            {
                if (await items.FindAsync(itemId, cancellationToken) is not { } item)
                {
                    return Error.Validation("tax.item_unknown", "A line's item does not exist.").WithWhy(("line", line.Key), ("itemId", itemId));
                }

                itemGroup = item.ItemTaxGroupId;
            }

            Result<TaxDetermination> determined;
            if (line.TaxCodeId is { } chosen)
            {
                determined = await ChosenAsync(scope, chosen, cancellationToken);
            }
            else
            {
                var from = line.ShipFromCountry ?? request.ShipFromCountry ?? (request.Direction == TaxDirections.Sales ? scope.Company.Country : null);
                var to = line.ShipToCountry ?? request.ShipToCountry ?? (request.Direction == TaxDirections.Purchase ? scope.Company.Country : null);
                determined = await DetermineAsync(scope, itemGroup, partnerGroup, from, to, cancellationToken);
            }

            if (determined.IsFailure)
            {
                return determined.Error!.WithWhy(("line", line.Key));
            }

            var d = determined.Value;
            inputs.Add(new TaxLineInput(line.Key, line.Amount, d.Code));
            determinations.Add(new TaxLineDetermination(line.Key, d.Reason, d.Code?.Id, d.Code?.Code, d.RuleId, d.CertificateNumber, itemGroup));
        }

        // ADR-0005: lines round first unless the regime or the company asks for the tax of the whole document.
        var level = scope.Regime?.RoundingLevel == TaxRoundingLevels.Document || scope.Company.TaxRoundingMode == TaxRoundingLevels.Document
            ? TaxRoundingLevels.Document
            : TaxRoundingLevels.Line;
        var document = TaxCalculator.Calculate(inputs, request.PricesIncludeTax, level, currency, new RoundingPolicy(scope.Company.RoundingMode));
        return new TaxedDocumentResult(document, determinations, scope.Regime?.Id, scope.Regime?.Code, partnerGroup);
    }

    // ------------------------------------------------------------------ the steps

    private async Task<Result<Scope>> ScopeAsync(Guid companyId, string? direction, DateOnly taxDate, Guid? partnerId, CancellationToken cancellationToken)
    {
        if (direction is not (TaxDirections.Sales or TaxDirections.Purchase))
        {
            return Error.Validation("tax.direction_invalid", "A document is a sale or a purchase.").WithWhy(("direction", direction));
        }

        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is not { } company)
        {
            return Error.Validation("tax.company_unknown", "The company does not exist.").WithWhy(("companyId", companyId));
        }

        // The primary registration in force on the date, else the one registered longest.
        var registration = await db.Registrations.AsNoTracking()
            .Where(r => r.CompanyId == companyId && (r.RegisteredFrom == null || r.RegisteredFrom <= taxDate))
            .Join(db.Regimes.AsNoTracking().Where(static g => g.IsActive), static r => r.RegimeId, static g => g.Id, static (r, g) => r)
            .OrderByDescending(static r => r.IsPrimary)
            .ThenBy(static r => r.RegisteredFrom)
            .ThenBy(static r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (registration is null)
        {
            return new Scope(company, null, direction, taxDate, [], null);
        }

        var regime = await RegimeAsync(registration.RegimeId, cancellationToken);
        var rules = await db.Rules.AsNoTracking()
            .Where(r => r.RegimeId == registration.RegimeId && r.Direction == direction && (r.ValidFrom == null || r.ValidFrom <= taxDate) && (r.ValidTo == null || r.ValidTo >= taxDate))
            .ToListAsync(cancellationToken);

        // A certificate relieves a customer of tax; a supplier's standing shows in its partner tax group instead.
        TaxExemption? exemption = null;
        if (direction == TaxDirections.Sales && partnerId is { } partner)
        {
            exemption = await db.Exemptions.AsNoTracking()
                .Where(e => e.PartnerId == partner && e.RegimeId == registration.RegimeId && e.ValidFrom <= taxDate && (e.ValidTo == null || e.ValidTo >= taxDate))
                .OrderByDescending(static e => e.ValidFrom)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new Scope(company, regime, direction, taxDate, rules, exemption);
    }

    private async Task<Result<TaxDetermination>> DetermineAsync(Scope scope, Guid? itemGroup, Guid? partnerGroup, string? from, string? to, CancellationToken cancellationToken)
    {
        if (scope.Regime is null)
        {
            return new TaxDetermination(null, TaxReasons.NotRegistered, null, null);
        }

        var rule = scope.Rules
            .Where(r => (r.ItemTaxGroupId is null || r.ItemTaxGroupId == itemGroup)
                && (r.PartnerTaxGroupId is null || r.PartnerTaxGroupId == partnerGroup)
                && (r.ShipFromCountry is null || string.Equals(r.ShipFromCountry, from, StringComparison.OrdinalIgnoreCase))
                && (r.ShipToCountry is null || string.Equals(r.ShipToCountry, to, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static r => Specificity(r))
            .ThenByDescending(static r => r.ValidFrom ?? DateOnly.MinValue)
            .FirstOrDefault();

        TaxCodeInfo? ruled = null;
        if (rule is not null)
        {
            var info = await ActiveInfoAsync(rule.TaxCodeId, scope.Date, cancellationToken);
            if (info.IsFailure)
            {
                return info.Error!.WithWhy(("ruleId", rule.Id));
            }

            ruled = info.Value;
        }

        // The exemption replaces a taxed code; a line the matrix already puts at zero, exempt or out of scope keeps it.
        if (scope.Exemption is { } exemption && (ruled is null || ruled.Treatment == TaxTreatments.Standard))
        {
            var exempt = await ActiveInfoAsync(exemption.TaxCodeId, scope.Date, cancellationToken);
            return exempt.IsFailure ? exempt.Error! : new TaxDetermination(exempt.Value, TaxReasons.Exemption, null, exemption.CertificateNumber);
        }

        if (ruled is null)
        {
            return Error.Validation("tax.no_determination", "No row of the tax determination matrix matches this line; add one for it or name the line's tax code.")
                .WithWhy(("regime", scope.Regime.Code), ("direction", scope.Direction), ("taxDate", scope.Date), ("itemTaxGroupId", itemGroup), ("partnerTaxGroupId", partnerGroup), ("shipFrom", from), ("shipTo", to));
        }

        return new TaxDetermination(ruled, TaxReasons.Rule, rule!.Id, null);
    }

    private async Task<Result<TaxDetermination>> ChosenAsync(Scope scope, Guid codeId, CancellationToken cancellationToken)
    {
        if (scope.Regime is null)
        {
            return Error.Validation("tax.company_not_registered", "The company is registered in no tax regime on that date, so its lines carry no tax code.")
                .WithWhy(("companyId", scope.Company.Id.Value), ("taxDate", scope.Date));
        }

        var code = await CodeAsync(codeId, cancellationToken);
        if (code is null || code.RegimeId != scope.Regime.Id)
        {
            return Error.Validation("tax.code_not_regime", "The tax code is not one of the regime the company is registered in.").WithWhy(("taxCodeId", codeId), ("regime", scope.Regime.Code));
        }

        var info = await ActiveInfoAsync(codeId, scope.Date, cancellationToken);
        return info.IsFailure ? info.Error! : new TaxDetermination(info.Value, TaxReasons.Chosen, null, null);
    }

    private static int Specificity(TaxRule rule) =>
        (rule.ItemTaxGroupId is null ? 0 : 8) + (rule.PartnerTaxGroupId is null ? 0 : 4) + (rule.ShipFromCountry is null ? 0 : 2) + (rule.ShipToCountry is null ? 0 : 1);

    private async Task<Result<TaxCodeInfo>> ActiveInfoAsync(Guid codeId, DateOnly date, CancellationToken cancellationToken)
    {
        var code = await CodeAsync(codeId, cancellationToken);
        if (code is null)
        {
            return Error.Validation("tax.code_unknown", "There is no such tax code.").WithWhy(("taxCodeId", codeId));
        }

        if (!code.IsActive)
        {
            return Error.Validation("tax.code_inactive", "The tax code is no longer in use.").WithWhy(("taxCode", code.Code));
        }

        return await InfoAsync(code, date, cancellationToken);
    }

    private async Task<Result<TaxCodeInfo>> InfoAsync(TaxCode code, DateOnly date, CancellationToken cancellationToken)
    {
        var rate = code.Rates.Where(r => r.ValidFrom <= date).OrderByDescending(static r => r.ValidFrom).FirstOrDefault();
        if (rate is null)
        {
            return Error.Validation("tax.no_rate", "The tax code has no rate on that date.").WithWhy(("taxCode", code.Code), ("taxDate", date));
        }

        var regime = (await RegimeAsync(code.RegimeId, cancellationToken))!;
        return new TaxCodeInfo(code.Id, regime.Id, regime.Code, code.Code, code.Kind, code.Treatment, rate.RatePct, code.IsRecoverable, code.IsReverseCharge, code.ExemptionReasonCode,
            code.OutputAccountRole, code.InputAccountRole, regime.RoundingLevel);
    }

    private async Task<TaxCode?> CodeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_codes.TryGetValue(id, out var code))
        {
            code = await db.Codes.AsNoTracking().Include(static c => c.Rates).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            _codes[id] = code;
        }

        return code;
    }

    private async Task<TaxRegime?> RegimeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_regimes.TryGetValue(id, out var regime))
        {
            regime = await db.Regimes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
            _regimes[id] = regime;
        }

        return regime;
    }

    private sealed record Scope(CompanyInfo Company, TaxRegime? Regime, string Direction, DateOnly Date, IReadOnlyList<TaxRule> Rules, TaxExemption? Exemption);
}

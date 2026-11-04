using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>
/// Tax as configuration (ADR-0018): regimes installed from the country templates and then the tenant's own, codes with
/// dated rates and return boxes, item and partner tax groups, the determination matrix, each company's registrations
/// and partners' exemption certificates.
/// </summary>
public sealed class TaxSetupService(TaxDbContext db, ICompanyDirectory companies, IPartnerDirectory partners, TaxAccess access, IClock clock)
{
    private static readonly string[] Families = ["vat", "gst", "sales_tax", "none"];
    private static readonly string[] TaxPoints = ["invoice", "payment", "delivery"];
    private static readonly string[] Frequencies = ["monthly", "quarterly", "annual"];
    private static readonly string[] Applicability = ["goods", "services", "both"];
    private static readonly string[] GroupKinds = ["item", "partner"];

    // ------------------------------------------------------------------ templates

    public async Task<IReadOnlyList<TaxTemplateSummary>> ListTemplatesAsync(CancellationToken cancellationToken)
    {
        var installed = await db.Regimes.AsNoTracking().Where(static r => r.TemplateCode != null).Select(static r => r.TemplateCode!).ToListAsync(cancellationToken);
        return TemplateCatalog.All
            .Select(t => new TaxTemplateSummary(t.Code, t.Country, t.Version, t.Name.Values, t.Family, t.Codes.Count, t.Rules.Count, installed.Contains(t.Code, StringComparer.Ordinal)))
            .ToList();
    }

    /// <summary>Installs a country template: its regime, the groups it names (reusing the tenant's by code), its codes with their rates and its determination matrix.</summary>
    public async Task<Result<TaxRegimeDetail>> InstallTemplateAsync(string templateCode, CancellationToken cancellationToken)
    {
        var template = TemplateCatalog.Find(templateCode);
        if (template is null)
        {
            return Error.NotFound("tax_template", templateCode);
        }

        if (await db.Regimes.AnyAsync(r => r.Code == template.Code, cancellationToken))
        {
            return Error.Conflict("tax.regime_exists", $"The regime {template.Code} is already installed.");
        }

        var now = clock.UtcNow;
        var regime = new TaxRegime
        {
            Id = Guid.CreateVersion7(),
            Code = template.Code,
            Country = template.Country,
            Name = new LocalizedText(template.Name.Values),
            Family = template.Family,
            RoundingLevel = template.RoundingLevel,
            TaxPoint = template.TaxPoint,
            ReturnFrequency = template.ReturnFrequency,
            EinvoicingScheme = template.EinvoicingScheme,
            TemplateCode = template.Code,
            TemplateVersion = template.Version,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Regimes.Add(regime);

        var groups = await db.Groups.ToListAsync(cancellationToken);
        Guid GroupId(string kind, string code, TemplateText name)
        {
            var existing = groups.FirstOrDefault(g => g.Kind == kind && g.Code == code);
            if (existing is not null)
            {
                return existing.Id;
            }

            var group = new TaxGroup { Id = Guid.CreateVersion7(), Kind = kind, Code = code, Name = new LocalizedText(name.Values), CreatedAt = now, UpdatedAt = now };
            groups.Add(group);
            db.Groups.Add(group);
            return group.Id;
        }

        var itemGroups = template.ItemGroups.ToDictionary(g => g.Code, g => GroupId("item", g.Code, g.Name), StringComparer.Ordinal);
        var partnerGroups = template.PartnerGroups.ToDictionary(g => g.Code, g => GroupId("partner", g.Code, g.Name), StringComparer.Ordinal);
        var codes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var c in template.Codes)
        {
            var code = new TaxCode
            {
                Id = Guid.CreateVersion7(),
                RegimeId = regime.Id,
                Code = c.Code,
                Name = new LocalizedText(c.Name.Values),
                Kind = c.Kind,
                Treatment = c.Treatment,
                IsRecoverable = c.Recoverable,
                IsReverseCharge = c.ReverseCharge,
                AppliesTo = c.AppliesTo,
                ExemptionReasonCode = c.ExemptionReasonCode,
                ExemptionReason = c.ExemptionReason is null ? new LocalizedText() : new LocalizedText(c.ExemptionReason.Values),
                SalesBaseBox = c.Boxes.SalesBase,
                SalesTaxBox = c.Boxes.SalesTax,
                PurchaseBaseBox = c.Boxes.PurchaseBase,
                PurchaseTaxBox = c.Boxes.PurchaseTax,
                CreatedAt = now,
                UpdatedAt = now,
                Rates = c.Rates.Select(r => new TaxRate { ValidFrom = r.From, RatePct = r.Pct }).ToList(),
            };
            codes[c.Code] = code.Id;
            db.Codes.Add(code);
        }

        foreach (var r in template.Rules)
        {
            db.Rules.Add(new TaxRule
            {
                Id = Guid.CreateVersion7(),
                RegimeId = regime.Id,
                Direction = r.Direction,
                TaxCodeId = codes[r.Code],
                ItemTaxGroupId = r.ItemGroup is null ? null : itemGroups[r.ItemGroup],
                PartnerTaxGroupId = r.PartnerGroup is null ? null : partnerGroups[r.PartnerGroup],
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await RegimeAsync(regime.Id, cancellationToken);
    }

    // ------------------------------------------------------------------ regimes and codes

    public async Task<IReadOnlyList<TaxRegimeSummary>> ListRegimesAsync(CancellationToken cancellationToken)
    {
        var regimes = await db.Regimes.AsNoTracking().OrderBy(static r => r.Code).ToListAsync(cancellationToken);
        return await MapRegimesAsync(regimes, cancellationToken);
    }

    public async Task<Result<TaxRegimeDetail>> RegimeAsync(Guid regimeId, CancellationToken cancellationToken)
    {
        var regime = await db.Regimes.AsNoTracking().SingleOrDefaultAsync(r => r.Id == regimeId, cancellationToken);
        if (regime is null)
        {
            return Error.NotFound("tax_regime", regimeId);
        }

        var codes = await db.Codes.AsNoTracking().Include(static c => c.Rates).Where(c => c.RegimeId == regimeId).OrderBy(static c => c.Code).ToListAsync(cancellationToken);
        var rules = await db.Rules.AsNoTracking().Where(r => r.RegimeId == regimeId).ToListAsync(cancellationToken);
        return new TaxRegimeDetail((await MapRegimesAsync([regime], cancellationToken))[0], codes.Select(MapCode).ToList(), await MapRulesAsync(rules, cancellationToken));
    }

    public async Task<Result<TaxRegimeSummary>> SaveRegimeAsync(Guid regimeId, SaveTaxRegimeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var regime = await db.Regimes.SingleOrDefaultAsync(r => r.Id == regimeId, cancellationToken);
        if (regime is null)
        {
            return Error.NotFound("tax_regime", regimeId);
        }

        var name = Validation.Name(request.Name, "tax_regime");
        var rounding = Validation.OneOf(request.RoundingLevel, "tax_regime.rounding_level", TaxRoundingLevels.All);
        var point = Validation.OneOf(request.TaxPoint, "tax_regime.tax_point", TaxPoints);
        var frequency = Validation.OneOf(request.ReturnFrequency, "tax_regime.return_frequency", Frequencies);
        foreach (var error in new[] { name.Error, rounding.Error, point.Error, frequency.Error })
        {
            if (error is not null)
            {
                return error;
            }
        }

        regime.Name = name.Value;
        regime.RoundingLevel = rounding.Value;
        regime.TaxPoint = point.Value;
        regime.ReturnFrequency = frequency.Value;
        regime.EinvoicingScheme = Validation.Text(request.EinvoicingScheme)?.ToLowerInvariant();
        regime.IsActive = request.IsActive;
        regime.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await MapRegimesAsync([regime], cancellationToken))[0];
    }

    public async Task<Result<TaxCodeSummary>> SaveCodeAsync(Guid regimeId, Guid? codeId, SaveTaxCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Regimes.AnyAsync(r => r.Id == regimeId, cancellationToken))
        {
            return Error.NotFound("tax_regime", regimeId);
        }

        var code = Validation.Code(request.Code, "tax_code");
        var name = Validation.Name(request.Name, "tax_code");
        var kind = Validation.OneOf(request.Kind, "tax_code.kind", TaxKinds.All);
        var treatment = Validation.OneOf(request.Treatment, "tax_code.treatment", TaxTreatments.All);
        var applies = Validation.OneOf(request.AppliesTo, "tax_code.applies_to", Applicability, "both");
        foreach (var error in new[] { code.Error, name.Error, kind.Error, treatment.Error, applies.Error })
        {
            if (error is not null)
            {
                return error;
            }
        }

        var rates = (request.Rates ?? []).OrderBy(static r => r.ValidFrom).ToList();
        if (rates.Count == 0 || rates.Select(static r => r.ValidFrom).Distinct().Count() != rates.Count || rates.Any(static r => r.RatePct is < 0m or > 100m))
        {
            return Error.Validation("tax_code.rates_invalid", "A code has at least one rate, each from its own date, between 0 and 100%.");
        }

        if (treatment.Value != TaxTreatments.Standard && rates.Any(static r => r.RatePct != 0m))
        {
            return Error.Validation("tax_code.rate_not_zero", "A zero-rated, exempt or out-of-scope code taxes at 0%.");
        }

        if (treatment.Value == TaxTreatments.Exempt && string.IsNullOrWhiteSpace(request.ExemptionReasonCode))
        {
            return Error.Validation("tax_code.exemption_reason_required", "An exempt code names the reason printed on invoices.");
        }

        TaxCode? entity = null;
        if (codeId is { } id)
        {
            entity = await db.Codes.Include(static c => c.Rates).SingleOrDefaultAsync(c => c.Id == id && c.RegimeId == regimeId, cancellationToken);
            if (entity is null)
            {
                return Error.NotFound("tax_code", id);
            }
        }

        if (await db.Codes.AnyAsync(c => c.RegimeId == regimeId && c.Code == code.Value && c.Id != codeId, cancellationToken))
        {
            return Error.Conflict("tax_code.code_taken", $"The regime already has a code {code.Value}.");
        }

        var isNew = entity is null;
        entity ??= new TaxCode { Id = Guid.CreateVersion7(), RegimeId = regimeId, CreatedAt = clock.UtcNow };
        entity.Code = code.Value;
        entity.Name = name.Value;
        entity.Kind = kind.Value;
        entity.Treatment = treatment.Value;
        entity.IsRecoverable = request.IsRecoverable;
        entity.IsReverseCharge = request.IsReverseCharge;
        entity.AppliesTo = applies.Value;
        entity.ExemptionReasonCode = Validation.Text(request.ExemptionReasonCode);
        entity.ExemptionReason = new LocalizedText(request.ExemptionReason ?? new Dictionary<string, string>(StringComparer.Ordinal));
        entity.OutputAccountRole = string.IsNullOrWhiteSpace(request.OutputAccountRole) ? "OutputTax" : request.OutputAccountRole.Trim();
        entity.InputAccountRole = string.IsNullOrWhiteSpace(request.InputAccountRole) ? "InputTax" : request.InputAccountRole.Trim();
        entity.SalesBaseBox = Validation.Text(request.SalesBaseBox);
        entity.SalesTaxBox = Validation.Text(request.SalesTaxBox);
        entity.PurchaseBaseBox = Validation.Text(request.PurchaseBaseBox);
        entity.PurchaseTaxBox = Validation.Text(request.PurchaseTaxBox);
        entity.IsActive = request.IsActive;
        entity.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Codes.Add(entity);
        }
        else
        {
            // The rates are replaced as a whole; the old rows go first so a kept date is inserted again.
            entity.Rates.Clear();
            await db.SaveChangesAsync(cancellationToken);
        }

        entity.Rates.AddRange(rates.Select(r => new TaxRate { TaxCodeId = entity.Id, ValidFrom = r.ValidFrom, RatePct = r.RatePct }));
        await db.SaveChangesAsync(cancellationToken);
        return MapCode(entity);
    }

    // ------------------------------------------------------------------ groups and the matrix

    public async Task<IReadOnlyList<TaxGroupSummary>> ListGroupsAsync(string? kind, CancellationToken cancellationToken)
    {
        var query = db.Groups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(g => g.Kind == kind);
        }

        return (await query.OrderBy(static g => g.Kind).ThenBy(static g => g.Code).ToListAsync(cancellationToken)).Select(MapGroup).ToList();
    }

    public async Task<Result<TaxGroupSummary>> SaveGroupAsync(Guid? groupId, SaveTaxGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = Validation.OneOf(request.Kind, "tax_group.kind", GroupKinds);
        var code = Validation.Code(request.Code, "tax_group");
        var name = Validation.Name(request.Name, "tax_group");
        foreach (var error in new[] { kind.Error, code.Error, name.Error })
        {
            if (error is not null)
            {
                return error;
            }
        }

        TaxGroup? group = null;
        if (groupId is { } id)
        {
            group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return Error.NotFound("tax_group", id);
            }

            if (group.Kind != kind.Value)
            {
                return Error.Validation("tax_group.kind_fixed", "An item group stays an item group and a partner group a partner group.");
            }
        }

        if (await db.Groups.AnyAsync(g => g.Kind == kind.Value && g.Code == code.Value && g.Id != groupId, cancellationToken))
        {
            return Error.Conflict("tax_group.code_taken", $"There is already a {kind.Value} tax group {code.Value}.");
        }

        var isNew = group is null;
        group ??= new TaxGroup { Id = Guid.CreateVersion7(), Kind = kind.Value, CreatedAt = clock.UtcNow };
        group.Code = code.Value;
        group.Name = name.Value;
        group.IsActive = request.IsActive;
        group.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Groups.Add(group);
        }

        await db.SaveChangesAsync(cancellationToken);
        return MapGroup(group);
    }

    public async Task<Result<TaxRuleSummary>> SaveRuleAsync(Guid regimeId, Guid? ruleId, SaveTaxRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var direction = Validation.OneOf(request.Direction, "tax_rule.direction", TaxDirections.All);
        if (direction.IsFailure)
        {
            return direction.Error!;
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "tax_rule");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        if (!await db.Codes.AnyAsync(c => c.Id == request.TaxCodeId && c.RegimeId == regimeId, cancellationToken))
        {
            return Error.Validation("tax_rule.code_not_regime", "The code is not one of the regime's.").WithWhy(("taxCodeId", request.TaxCodeId));
        }

        if ((request.ItemTaxGroupId is { } itemGroup && !await db.Groups.AnyAsync(g => g.Id == itemGroup && g.Kind == "item", cancellationToken))
            || (request.PartnerTaxGroupId is { } partnerGroup && !await db.Groups.AnyAsync(g => g.Id == partnerGroup && g.Kind == "partner", cancellationToken)))
        {
            return Error.Validation("tax_rule.group_invalid", "The item group must be an item tax group and the partner group a partner tax group.");
        }

        var from = Country(request.ShipFromCountry);
        var to = Country(request.ShipToCountry);
        if ((request.ShipFromCountry is not null && from is null) || (request.ShipToCountry is not null && to is null))
        {
            return Error.Validation("tax_rule.country_invalid", "A country is its two-letter ISO code.");
        }

        TaxRule? rule = null;
        if (ruleId is { } id)
        {
            rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == id && r.RegimeId == regimeId, cancellationToken);
            if (rule is null)
            {
                return Error.NotFound("tax_rule", id);
            }
        }

        if (await db.Rules.AnyAsync(r => r.RegimeId == regimeId && r.Direction == direction.Value && r.ItemTaxGroupId == request.ItemTaxGroupId && r.PartnerTaxGroupId == request.PartnerTaxGroupId
            && r.ShipFromCountry == from && r.ShipToCountry == to && r.ValidFrom == request.ValidFrom && r.Id != ruleId, cancellationToken))
        {
            return Error.Conflict("tax_rule.duplicate", "The matrix already has a code for this combination from that date.");
        }

        var isNew = rule is null;
        rule ??= new TaxRule { Id = Guid.CreateVersion7(), RegimeId = regimeId, CreatedAt = clock.UtcNow };
        rule.Direction = direction.Value;
        rule.TaxCodeId = request.TaxCodeId;
        rule.ItemTaxGroupId = request.ItemTaxGroupId;
        rule.PartnerTaxGroupId = request.PartnerTaxGroupId;
        rule.ShipFromCountry = from;
        rule.ShipToCountry = to;
        rule.ValidFrom = request.ValidFrom;
        rule.ValidTo = request.ValidTo;
        rule.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Rules.Add(rule);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapRulesAsync([rule], cancellationToken))[0];
    }

    public async Task<Result> DeleteRuleAsync(Guid regimeId, Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.Id == ruleId && r.RegimeId == regimeId, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound("tax_rule", ruleId);
        }

        db.Rules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ registrations and exemptions

    public async Task<IReadOnlyList<CompanyTaxRegistrationSummary>> ListRegistrationsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Registrations.AsNoTracking();
        if (access.CompaniesFor(TaxPermissions.SetupRead) is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(r => ids.Contains(r.CompanyId));
        }

        if (companyId is { } company)
        {
            query = query.Where(r => r.CompanyId == company);
        }

        return await MapRegistrationsAsync(await query.ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result<CompanyTaxRegistrationSummary>> SaveRegistrationAsync(Guid? registrationId, SaveCompanyTaxRegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, TaxPermissions.SetupManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken) is null)
        {
            return Error.Validation("tax_registration.company_unknown", "The company does not exist.");
        }

        if (!await db.Regimes.AnyAsync(r => r.Id == request.RegimeId, cancellationToken))
        {
            return Error.Validation("tax_registration.regime_unknown", "The regime does not exist.");
        }

        TaxRegistration? registration = null;
        if (registrationId is { } id)
        {
            registration = await db.Registrations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (registration is null || registration.CompanyId != request.CompanyId)
            {
                return Error.NotFound("tax_registration", id);
            }
        }

        if (await db.Registrations.AnyAsync(r => r.CompanyId == request.CompanyId && r.RegimeId == request.RegimeId && r.Id != registrationId, cancellationToken))
        {
            return Error.Conflict("tax_registration.duplicate", "The company is already registered in this regime.");
        }

        if (request.IsPrimary)
        {
            // One primary registration per company: the new one takes the role from the previous one.
            foreach (var other in await db.Registrations.Where(r => r.CompanyId == request.CompanyId && r.IsPrimary && r.Id != registrationId).ToListAsync(cancellationToken))
            {
                other.IsPrimary = false;
                other.UpdatedAt = clock.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var isNew = registration is null;
        registration ??= new TaxRegistration { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        registration.RegimeId = request.RegimeId;
        registration.RegistrationNumber = Validation.Text(request.RegistrationNumber);
        registration.RegisteredFrom = request.RegisteredFrom;
        registration.IsPrimary = request.IsPrimary;
        registration.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Registrations.Add(registration);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapRegistrationsAsync([registration], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<TaxExemptionSummary>> ListExemptionsAsync(Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Exemptions.AsNoTracking();
        if (partnerId is { } partner)
        {
            query = query.Where(e => e.PartnerId == partner);
        }

        return await MapExemptionsAsync(await query.OrderBy(static e => e.ValidFrom).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result<TaxExemptionSummary>> SaveExemptionAsync(Guid? exemptionId, SaveTaxExemptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CertificateNumber))
        {
            return Error.Validation("tax_exemption.certificate_required", "An exemption names its certificate.");
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "tax_exemption");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        if (await partners.FindAsync(request.PartnerId, cancellationToken) is null)
        {
            return Error.Validation("tax_exemption.partner_unknown", "The partner does not exist.");
        }

        var code = await db.Codes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.TaxCodeId && c.RegimeId == request.RegimeId, cancellationToken);
        if (code is null || code.Treatment is not (TaxTreatments.Exempt or TaxTreatments.ZeroRated or TaxTreatments.OutOfScope))
        {
            return Error.Validation("tax_exemption.code_invalid", "An exemption puts lines on an exempt, zero-rated or out-of-scope code of the same regime.");
        }

        TaxExemption? exemption = null;
        if (exemptionId is { } id)
        {
            exemption = await db.Exemptions.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (exemption is null)
            {
                return Error.NotFound("tax_exemption", id);
            }
        }

        var overlapping = await db.Exemptions.AnyAsync(e => e.PartnerId == request.PartnerId && e.RegimeId == request.RegimeId && e.Id != exemptionId
            && (e.ValidTo == null || e.ValidTo >= request.ValidFrom) && (request.ValidTo == null || e.ValidFrom <= request.ValidTo), cancellationToken);
        if (overlapping)
        {
            return Error.Conflict("tax_exemption.overlap", "The partner already has an exemption in this regime for part of that period.");
        }

        var isNew = exemption is null;
        exemption ??= new TaxExemption { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        exemption.PartnerId = request.PartnerId;
        exemption.RegimeId = request.RegimeId;
        exemption.TaxCodeId = request.TaxCodeId;
        exemption.CertificateNumber = request.CertificateNumber.Trim();
        exemption.ValidFrom = request.ValidFrom;
        exemption.ValidTo = request.ValidTo;
        exemption.Notes = Validation.Text(request.Notes);
        exemption.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Exemptions.Add(exemption);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapExemptionsAsync([exemption], cancellationToken))[0];
    }

    public async Task<Result> DeleteExemptionAsync(Guid exemptionId, CancellationToken cancellationToken)
    {
        var exemption = await db.Exemptions.SingleOrDefaultAsync(e => e.Id == exemptionId, cancellationToken);
        if (exemption is null)
        {
            return Error.NotFound("tax_exemption", exemptionId);
        }

        db.Exemptions.Remove(exemption);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ mapping

    private static string? Country(string? value)
    {
        var country = Validation.Text(value)?.ToUpperInvariant();
        return country is { Length: 2 } && country.All(char.IsAsciiLetterUpper) ? country : null;
    }

    private async Task<IReadOnlyList<TaxRegimeSummary>> MapRegimesAsync(IReadOnlyList<TaxRegime> regimes, CancellationToken cancellationToken)
    {
        var ids = regimes.Select(static r => r.Id).ToArray();
        var codes = await db.Codes.AsNoTracking().Where(c => ids.Contains(c.RegimeId)).GroupBy(static c => c.RegimeId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var rules = await db.Rules.AsNoTracking().Where(r => ids.Contains(r.RegimeId)).GroupBy(static r => r.RegimeId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var registrations = await db.Registrations.AsNoTracking().Where(r => ids.Contains(r.RegimeId)).GroupBy(static r => r.RegimeId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return regimes.Select(r => new TaxRegimeSummary(r.Id, r.Code, r.Country, r.Name.Values, r.Family, r.RoundingLevel, r.TaxPoint, r.ReturnFrequency, r.EinvoicingScheme, r.TemplateCode, r.TemplateVersion, r.IsActive,
            codes.GetValueOrDefault(r.Id), rules.GetValueOrDefault(r.Id), registrations.GetValueOrDefault(r.Id), r.UpdatedAt)).ToList();
    }

    private TaxCodeSummary MapCode(TaxCode c)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var current = c.Rates.Where(r => r.ValidFrom <= today).OrderByDescending(static r => r.ValidFrom).FirstOrDefault();
        return new TaxCodeSummary(c.Id, c.RegimeId, c.Code, c.Name.Values, c.Kind, c.Treatment, c.IsRecoverable, c.IsReverseCharge, c.AppliesTo, c.ExemptionReasonCode, c.ExemptionReason.Values,
            c.OutputAccountRole, c.InputAccountRole, c.SalesBaseBox, c.SalesTaxBox, c.PurchaseBaseBox, c.PurchaseTaxBox, c.IsActive, current?.RatePct,
            c.Rates.OrderBy(static r => r.ValidFrom).Select(static r => new TaxRateDto(r.ValidFrom, r.RatePct)).ToList());
    }

    private static TaxGroupSummary MapGroup(TaxGroup g) => new(g.Id, g.Kind, g.Code, g.Name.Values, g.IsActive, g.UpdatedAt);

    private async Task<IReadOnlyList<TaxRuleSummary>> MapRulesAsync(IReadOnlyList<TaxRule> rules, CancellationToken cancellationToken)
    {
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(static g => g.Id, static g => g.Code, cancellationToken);
        var codeIds = rules.Select(static r => r.TaxCodeId).Distinct().ToArray();
        var codes = await db.Codes.AsNoTracking().Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        return rules
            .Select(r => new TaxRuleSummary(r.Id, r.RegimeId, r.Direction, r.ItemTaxGroupId, r.ItemTaxGroupId is { } i ? groups.GetValueOrDefault(i) : null, r.PartnerTaxGroupId,
                r.PartnerTaxGroupId is { } p ? groups.GetValueOrDefault(p) : null, r.ShipFromCountry, r.ShipToCountry, r.ValidFrom, r.ValidTo, r.TaxCodeId, codes.GetValueOrDefault(r.TaxCodeId) ?? string.Empty, r.UpdatedAt))
            .OrderBy(static r => r.Direction, StringComparer.Ordinal)
            .ThenBy(static r => r.ItemTaxGroupCode ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static r => r.PartnerTaxGroupCode ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static r => r.ShipFromCountry ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static r => r.ShipToCountry ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static r => r.ValidFrom ?? DateOnly.MinValue)
            .ToList();
    }

    private async Task<IReadOnlyList<CompanyTaxRegistrationSummary>> MapRegistrationsAsync(IReadOnlyList<TaxRegistration> registrations, CancellationToken cancellationToken)
    {
        var regimes = await db.Regimes.AsNoTracking().ToDictionaryAsync(static r => r.Id, cancellationToken);
        return registrations.Select(r => new CompanyTaxRegistrationSummary(r.Id, r.CompanyId, r.RegimeId, regimes[r.RegimeId].Code, regimes[r.RegimeId].Name.Values, r.RegistrationNumber, r.RegisteredFrom, r.IsPrimary, r.UpdatedAt)).ToList();
    }

    private async Task<IReadOnlyList<TaxExemptionSummary>> MapExemptionsAsync(IReadOnlyList<TaxExemption> exemptions, CancellationToken cancellationToken)
    {
        var regimes = await db.Regimes.AsNoTracking().ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        var codeIds = exemptions.Select(static e => e.TaxCodeId).Distinct().ToArray();
        var codes = await db.Codes.AsNoTracking().Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        var names = await partners.DescribeAsync(exemptions.Select(static e => e.PartnerId).Distinct().ToList(), cancellationToken);
        return exemptions.Select(e => new TaxExemptionSummary(e.Id, e.PartnerId, names.GetValueOrDefault(e.PartnerId)?.Code ?? string.Empty,
            names.GetValueOrDefault(e.PartnerId)?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), e.RegimeId, regimes.GetValueOrDefault(e.RegimeId) ?? string.Empty,
            e.TaxCodeId, codes.GetValueOrDefault(e.TaxCodeId) ?? string.Empty, e.CertificateNumber, e.ValidFrom, e.ValidTo, e.Notes, e.UpdatedAt)).ToList();
    }
}

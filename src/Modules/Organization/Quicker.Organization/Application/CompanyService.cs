using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Organization.Application;

/// <summary>Companies, branches (with their BRANCH dimension value), enabled currencies and settings.</summary>
public sealed class CompanyService(OrganizationDbContext db, IUnitOfWorkAccessor unitOfWork, FiscalCalendarService calendars, IAuditSink audit, ICustomFieldValidator customFields, IEnumerable<ICompanyCostingGuard> costingGuards, IClock clock) : ICompanyDirectory, ICompanySettings
{
    private static readonly string[] CostingMethods = ["fifo", "average", "standard"];
    private static readonly string[] CostingScopes = ["company", "warehouse"];
    private static readonly string[] RevenueRecognitionPoints = ["invoice", "shipment"];
    private static readonly string[] TaxRoundingModes = ["line", "document"];
    private static readonly string[] RoundingModes = ["half_away", "half_even"];
    private static readonly string[] NegativeStockPolicies = ["block", "allow", "approve"];
    private static readonly string[] BankRevaluationModes = ["permanent", "reversing"];
    private static readonly string[] SettingTypes = ["string", "number", "boolean", "json"];

    private Guid? ActorUserId => unitOfWork.Current.Context.UserId?.Value;

    // ------------------------------------------------------------------ companies

    /// <summary>Fields of the list filter language on companies (slice 1.9); <c>cf.&lt;key&gt;</c> reaches custom fields.</summary>
    public static readonly FilterSpec<Company> Filter = new FilterSpec<Company>()
        .Field("code", static c => c.Code)
        .Field("country", static c => c.Country)
        .Field("functionalCurrency", static c => c.FunctionalCurrency)
        .Field("reportingCurrency", static c => c.ReportingCurrency)
        .Field("timeZone", static c => c.TimeZone)
        .Field("isActive", static c => c.IsActive)
        .Field("fiscalCalendarId", static c => c.FiscalCalendarId)
        .Field("createdAt", static c => c.CreatedAt)
        .Field("updatedAt", static c => c.UpdatedAt)
        .CustomFields(static c => c.CustomFields);

    public async Task<Result<IReadOnlyList<CompanySummary>>> ListCompaniesAsync(string? filter, CancellationToken cancellationToken)
    {
        var filtered = Filter.Apply(db.Companies, filter);
        if (filtered.IsFailure)
        {
            return filtered.Error!;
        }

        return (await filtered.Value.OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    /// <summary>One company; <paramref name="expand"/> is a comma-separated list of expansions (<c>branches</c> embeds the company's branches).</summary>
    public async Task<CompanySummary?> GetCompanyAsync(Guid companyId, string? expand, CancellationToken cancellationToken)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return null;
        }

        var summary = Map(company);
        var expansions = (expand ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return expansions.Contains("branches", StringComparer.OrdinalIgnoreCase) ? summary with { Branches = await ListBranchesAsync(companyId, cancellationToken) } : summary;
    }

    public async Task<Result<CompanySummary>> CreateCompanyAsync(SaveCompanyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = new Company { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(company, request, isNew: true, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Companies.AnyAsync(c => c.Code == company.Code, cancellationToken))
        {
            return Error.Conflict("company.code_taken", $"A company with code '{company.Code}' already exists.");
        }

        db.Companies.Add(company);
        var minorUnits = (await db.IsoCurrencies.SingleAsync(c => c.Code == company.FunctionalCurrency, cancellationToken)).MinorUnits;
        db.CompanyCurrencies.Add(new CompanyCurrency { CompanyId = company.Id, Currency = company.FunctionalCurrency, DisplayDecimals = minorUnits, IsEnabled = true });
        if (company.ReportingCurrency is { } reporting && reporting != company.FunctionalCurrency)
        {
            var reportingUnits = (await db.IsoCurrencies.SingleAsync(c => c.Code == reporting, cancellationToken)).MinorUnits;
            db.CompanyCurrencies.Add(new CompanyCurrency { CompanyId = company.Id, Currency = reporting, DisplayDecimals = reportingUnits, IsEnabled = true });
        }

        await db.SaveChangesAsync(cancellationToken);

        ForgetLookups();
        // A company must be able to post from day one: open the fiscal year that contains today in its time zone.
        await calendars.EnsureYearCoversAsync(company.FiscalCalendarId, clock.TodayIn(company.TimeZone), cancellationToken);
        return Map(company);
    }

    public async Task<Result<CompanySummary>> UpdateCompanyAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var applied = await ApplyAsync(company, request, isNew: false, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Companies.AnyAsync(c => c.Code == company.Code && c.Id != company.Id, cancellationToken))
        {
            return Error.Conflict("company.code_taken", $"A company with code '{company.Code}' already exists.");
        }

        company.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        return Map(company);
    }

    private async Task<Result> ApplyAsync(Company company, SaveCompanyRequest request, bool isNew, CancellationToken cancellationToken)
    {
        var code = Validation.UpperCode(request.Code, "company");
        var legalName = Validation.Name(request.LegalName, "company");
        var functional = Validation.CurrencyCode(request.FunctionalCurrency, "company");
        var timeZone = Validation.TimeZone(request.TimeZone);
        var costingMethod = Validation.OneOf(request.CostingMethod, "company.costing_method", CostingMethods);
        var costingScope = Validation.OneOf(request.CostingScope, "company.costing_scope", CostingScopes);
        var revenue = Validation.OneOf(request.RevenueRecognitionPoint, "company.revenue_recognition_point", RevenueRecognitionPoints);
        var taxRounding = Validation.OneOf(request.TaxRoundingMode, "company.tax_rounding_mode", TaxRoundingModes);
        var rounding = Validation.OneOf(request.RoundingMode, "company.rounding_mode", RoundingModes);
        var negativeStock = Validation.OneOf(request.NegativeStockPolicy, "company.negative_stock_policy", NegativeStockPolicies);
        var bankRevaluation = Validation.OneOf(request.BankRevaluationMode, "company.bank_revaluation_mode", BankRevaluationModes);
        foreach (var error in new[] { code.Error, legalName.Error, functional.Error, timeZone.Error, costingMethod.Error, costingScope.Error, revenue.Error, taxRounding.Error, rounding.Error, negativeStock.Error, bankRevaluation.Error })
        {
            if (error is not null)
            {
                return error;
            }
        }

        var country = request.Country?.Trim().ToUpperInvariant() ?? string.Empty;
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            return Error.Validation("company.country_invalid", "Country is a two-letter ISO 3166 code.");
        }

        if (!await db.IsoCurrencies.AnyAsync(c => c.Code == functional.Value && c.IsActive, cancellationToken))
        {
            return Error.Validation("company.currency_unknown", $"Currency '{functional.Value}' is not in the ISO 4217 list.").WithWhy(("currency", functional.Value));
        }

        string? reporting = null;
        if (!string.IsNullOrWhiteSpace(request.ReportingCurrency))
        {
            var reportingCode = Validation.CurrencyCode(request.ReportingCurrency, "company");
            if (reportingCode.IsFailure)
            {
                return reportingCode.Error!;
            }

            if (!await db.IsoCurrencies.AnyAsync(c => c.Code == reportingCode.Value && c.IsActive, cancellationToken))
            {
                return Error.Validation("company.currency_unknown", $"Currency '{reportingCode.Value}' is not in the ISO 4217 list.").WithWhy(("currency", reportingCode.Value));
            }

            reporting = reportingCode.Value;
        }

        if (!isNew && company.FunctionalCurrency != functional.Value)
        {
            // Immutable after creation (ADR-0017): balances and open items are carried in it.
            return Error.Conflict("company.functional_currency_locked", "The functional currency cannot change once the company exists; create a new company instead.");
        }

        if (!isNew && (company.CostingMethod != costingMethod.Value || company.CostingScope != costingScope.Value))
        {
            foreach (var guard in costingGuards)
            {
                if (await guard.RefusalAsync(new CompanyId(company.Id), cancellationToken) is { } refusal)
                {
                    return refusal.WithWhy(("costingMethod", company.CostingMethod), ("costingScope", company.CostingScope));
                }
            }
        }

        var fiscalCalendarId = request.FiscalCalendarId ?? (isNew ? await calendars.DefaultCalendarIdAsync(cancellationToken) : company.FiscalCalendarId);
        if (!await db.FiscalCalendars.AnyAsync(c => c.Id == fiscalCalendarId, cancellationToken))
        {
            return Error.NotFound("fiscal_calendar", fiscalCalendarId);
        }

        var businessCalendarId = request.BusinessCalendarId ?? (isNew ? await DefaultBusinessCalendarIdAsync(cancellationToken) : company.BusinessCalendarId);
        if (!await db.BusinessCalendars.AnyAsync(c => c.Id == businessCalendarId, cancellationToken))
        {
            return Error.NotFound("business_calendar", businessCalendarId);
        }

        var values = request.CustomFields ?? (isNew ? null : JsonDocument.Parse(company.CustomFields).RootElement);
        var validated = await customFields.ValidateAsync("company", values, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        company.CustomFields = validated.Value;
        company.Code = code.Value;
        company.LegalName = legalName.Value;
        company.TradeName = request.TradeName is { Count: > 0 } ? Validation.Name(request.TradeName, "company").Value : new LocalizedText();
        company.Country = country;
        company.FunctionalCurrency = functional.Value;
        company.ReportingCurrency = reporting;
        company.FiscalCalendarId = fiscalCalendarId;
        company.BusinessCalendarId = businessCalendarId;
        company.TimeZone = timeZone.Value;
        company.DefaultLanguage = string.IsNullOrWhiteSpace(request.DefaultLanguage) ? "en" : request.DefaultLanguage.Trim().ToLowerInvariant();
        company.CostingMethod = costingMethod.Value;
        company.CostingScope = costingScope.Value;
        company.RevenueRecognitionPoint = revenue.Value;
        company.TaxRoundingMode = taxRounding.Value;
        company.RoundingMode = rounding.Value;
        company.NegativeStockPolicy = negativeStock.Value;
        company.BankRevaluationMode = bankRevaluation.Value;
        company.RegistrationNumbers = Validation.Map(request.RegistrationNumbers);
        company.Address = Validation.Map(request.Address);
        company.IsActive = request.IsActive;
        company.UpdatedAt = company.CreatedAt == default ? clock.UtcNow : company.UpdatedAt;
        return Result.Success();
    }

    private async Task<Guid> DefaultBusinessCalendarIdAsync(CancellationToken cancellationToken) =>
        await db.BusinessCalendars.Where(c => c.IsSystem).OrderBy(static c => c.Code == OrganizationDefaults.DefaultBusinessCalendar ? 0 : 1).ThenBy(static c => c.Code).Select(static c => c.Id).FirstAsync(cancellationToken);

    // ------------------------------------------------------------------ branches

    public async Task<IReadOnlyList<BranchSummary>> ListBranchesAsync(Guid companyId, CancellationToken cancellationToken) =>
        (await db.Branches.Where(b => b.CompanyId == companyId).OrderBy(static b => b.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<BranchSummary>> CreateBranchAsync(Guid companyId, SaveBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var code = Validation.UpperCode(request.Code, "branch");
        var name = Validation.Name(request.Name, "branch");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Code == code.Value, cancellationToken))
        {
            return Error.Conflict("branch.code_taken", $"Branch '{code.Value}' already exists in company '{company.Code}'.");
        }

        // A branch is also a value of the system BRANCH dimension, so branch reporting uses the dimension machinery.
        var dimension = await db.Dimensions.SingleAsync(d => d.Code == OrganizationDefaults.BranchDimension, cancellationToken);
        var valueCode = $"{company.Code}-{code.Value}";
        if (await db.DimensionValues.AnyAsync(v => v.DimensionId == dimension.Id && v.Code == valueCode, cancellationToken))
        {
            return Error.Conflict("branch.dimension_value_taken", $"Dimension value '{valueCode}' already exists on BRANCH.");
        }

        var now = clock.UtcNow;
        var value = new DimensionValue { Id = Guid.CreateVersion7(), DimensionId = dimension.Id, Code = valueCode, Name = name.Value, CompanyId = companyId, IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now };
        var branch = new Branch
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            Code = code.Value,
            Name = name.Value,
            Address = Validation.Map(request.Address),
            TaxRegistrations = Validation.Map(request.TaxRegistrations),
            DimensionValueId = value.Id,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DimensionValues.Add(value);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        return Map(branch);
    }

    public async Task<Result<BranchSummary>> UpdateBranchAsync(Guid companyId, Guid branchId, SaveBranchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var branch = await db.Branches.SingleOrDefaultAsync(b => b.CompanyId == companyId && b.Id == branchId, cancellationToken);
        if (branch is null)
        {
            return Error.NotFound("branch", branchId);
        }

        var code = Validation.UpperCode(request.Code, "branch");
        var name = Validation.Name(request.Name, "branch");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (await db.Branches.AnyAsync(b => b.CompanyId == companyId && b.Code == code.Value && b.Id != branchId, cancellationToken))
        {
            return Error.Conflict("branch.code_taken", $"Branch '{code.Value}' already exists in this company.");
        }

        var value = await db.DimensionValues.SingleAsync(v => v.Id == branch.DimensionValueId, cancellationToken);
        var company = await db.Companies.SingleAsync(c => c.Id == companyId, cancellationToken);
        branch.Code = code.Value;
        branch.Name = name.Value;
        branch.Address = Validation.Map(request.Address);
        branch.TaxRegistrations = Validation.Map(request.TaxRegistrations);
        branch.IsActive = request.IsActive;
        branch.UpdatedAt = clock.UtcNow;
        value.Code = $"{company.Code}-{code.Value}";
        value.Name = name.Value;
        value.IsActive = request.IsActive;
        value.UpdatedAt = branch.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        return Map(branch);
    }

    // ------------------------------------------------------------------ company currencies

    public async Task<Result<IReadOnlyList<CompanyCurrencySummary>>> ListCurrenciesAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var rows = await db.CompanyCurrencies.Where(c => c.CompanyId == companyId)
            .Join(db.IsoCurrencies, static c => c.Currency, static i => i.Code, static (c, i) => new { c, i.MinorUnits })
            .OrderBy(static x => x.c.Currency)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new CompanyCurrencySummary(x.c.Currency, x.MinorUnits, x.c.DisplayDecimals, x.c.CashRoundingIncrement, x.c.IsEnabled, x.c.Currency == company.FunctionalCurrency)).ToList();
    }

    public async Task<Result<CompanyCurrencySummary>> SetCurrencyAsync(Guid companyId, CompanyCurrencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var code = Validation.CurrencyCode(request.Currency, "company_currency");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var iso = await db.IsoCurrencies.SingleOrDefaultAsync(c => c.Code == code.Value, cancellationToken);
        if (iso is null)
        {
            return Error.Validation("company.currency_unknown", $"Currency '{code.Value}' is not in the ISO 4217 list.").WithWhy(("currency", code.Value));
        }

        if (request.DisplayDecimals is < 0 or > 6)
        {
            return Error.Validation("company_currency.display_decimals_invalid", "Display decimals are between 0 and 6.");
        }

        if (request.CashRoundingIncrement < 0m)
        {
            return Error.Validation("company_currency.cash_rounding_invalid", "The cash rounding increment cannot be negative.");
        }

        if (code.Value == company.FunctionalCurrency && !request.IsEnabled)
        {
            return Error.Conflict("company_currency.functional_required", "The functional currency is always enabled.");
        }

        var row = await db.CompanyCurrencies.SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Currency == code.Value, cancellationToken);
        var before = row is null ? null : new { row.DisplayDecimals, row.CashRoundingIncrement, row.IsEnabled };
        if (row is null)
        {
            row = new CompanyCurrency { CompanyId = companyId, Currency = code.Value };
            db.CompanyCurrencies.Add(row);
        }

        row.DisplayDecimals = request.DisplayDecimals ?? iso.MinorUnits;
        row.CashRoundingIncrement = request.CashRoundingIncrement;
        row.IsEnabled = request.IsEnabled;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        await audit.RecordAsync(new AuditEntry("company_currency", companyId, $"{company.Code} {code.Value}", before is null ? AuditActions.Created : AuditActions.Updated,
            Before: before, After: new { row.DisplayDecimals, row.CashRoundingIncrement, row.IsEnabled }, CompanyId: companyId), cancellationToken);
        return new CompanyCurrencySummary(row.Currency, iso.MinorUnits, row.DisplayDecimals, row.CashRoundingIncrement, row.IsEnabled, row.Currency == company.FunctionalCurrency);
    }

    // ------------------------------------------------------------------ settings

    public async Task<Result<IReadOnlyList<SettingSummary>>> ListSettingsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is { } id && !await db.Companies.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return Error.NotFound("company", id);
        }

        var rows = await db.Settings.Where(s => s.CompanyId == companyId).OrderBy(static s => s.Key).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<SettingSummary>> SetSettingAsync(Guid? companyId, string key, SettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (companyId is { } id && !await db.Companies.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return Error.NotFound("company", id);
        }

        var normalizedKey = key?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedKey.Length is 0 or > 128 || !char.IsAsciiLetterLower(normalizedKey[0]) || !normalizedKey.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.'))
        {
            return Error.Validation("setting.key_invalid", "Setting keys are lower-case dotted identifiers (inventory.default_warehouse).");
        }

        var type = Validation.OneOf(request.ValueType, "setting.value_type", SettingTypes);
        if (type.IsFailure)
        {
            return type.Error!;
        }

        var kindMatches = type.Value switch
        {
            "string" => request.Value.ValueKind == JsonValueKind.String,
            "number" => request.Value.ValueKind == JsonValueKind.Number,
            "boolean" => request.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => request.Value.ValueKind is not JsonValueKind.Undefined,
        };
        if (!kindMatches)
        {
            return Error.Validation("setting.value_type_mismatch", $"The value is not a {type.Value}.");
        }

        var row = await db.Settings.SingleOrDefaultAsync(s => s.CompanyId == companyId && s.Key == normalizedKey, cancellationToken);
        if (row is null)
        {
            row = new Setting { Id = Guid.CreateVersion7(), CompanyId = companyId, Key = normalizedKey };
            db.Settings.Add(row);
        }

        row.ValueJson = request.Value.GetRawText();
        row.ValueType = type.Value;
        row.UpdatedBy = ActorUserId;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        return Map(row);
    }

    // ------------------------------------------------------------------ contracts

    public async Task<Result> AssignChartAsync(CompanyId id, Guid? chartId, CancellationToken cancellationToken = default)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", id.Value);
        }

        if (company.ChartId == chartId)
        {
            return Result.Success();
        }

        var before = company.ChartId;
        company.ChartId = chartId;
        company.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        await audit.RecordAsync(new AuditEntry("company", company.Id, company.Code, AuditActions.Updated, Before: new { chartId = before }, After: new { chartId }), cancellationToken);
        return Result.Success();
    }

    public async Task<JsonElement?> GetAsync(Guid? companyId, string key, CancellationToken cancellationToken = default)
    {
        var normalized = key?.Trim() ?? string.Empty;
        var rows = await db.Settings.Where(s => s.Key == normalized && (s.CompanyId == null || s.CompanyId == companyId)).ToListAsync(cancellationToken);
        var row = rows.FirstOrDefault(s => s.CompanyId != null) ?? rows.FirstOrDefault();
        return row is null ? null : JsonDocument.Parse(row.ValueJson).RootElement.Clone();
    }

    public async Task<Result> AssignPostingProfileAsync(CompanyId id, Guid? profileId, CancellationToken cancellationToken = default)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", id.Value);
        }

        if (company.PostingProfileId == profileId)
        {
            return Result.Success();
        }

        var before = company.PostingProfileId;
        company.PostingProfileId = profileId;
        company.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ForgetLookups();
        await audit.RecordAsync(new AuditEntry("company", company.Id, company.Code, AuditActions.Updated, Before: new { postingProfileId = before }, After: new { postingProfileId = profileId }), cancellationToken);
        return Result.Success();
    }

    // Lookups the posting path repeats many times per request are memoised for the life of this scoped service and dropped after every write it makes.
    private readonly Dictionary<Guid, CompanyInfo?> _companyCache = new();
    private readonly Dictionary<Guid, BranchInfo?> _branchCache = new();
    private readonly Dictionary<string, Currency?> _currencyCache = new(StringComparer.Ordinal);

    private void ForgetLookups()
    {
        _companyCache.Clear();
        _branchCache.Clear();
    }

    public async Task<CompanyInfo?> FindAsync(CompanyId id, CancellationToken cancellationToken = default)
    {
        if (_companyCache.TryGetValue(id.Value, out var cached))
        {
            return cached;
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id.Value, cancellationToken);
        var info = company is null ? null : await ToInfoAsync(company, cancellationToken);
        _companyCache[id.Value] = info;
        return info;
    }

    public async Task<IReadOnlyList<CompanyInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var companies = await db.Companies.OrderBy(static c => c.Code).ToListAsync(cancellationToken);
        var infos = new List<CompanyInfo>(companies.Count);
        foreach (var company in companies)
        {
            infos.Add(await ToInfoAsync(company, cancellationToken));
        }

        return infos;
    }

    public async Task<BranchInfo?> FindBranchAsync(BranchId id, CancellationToken cancellationToken = default)
    {
        if (_branchCache.TryGetValue(id.Value, out var cached))
        {
            return cached;
        }

        var branch = await db.Branches.SingleOrDefaultAsync(b => b.Id == id.Value, cancellationToken);
        var info = branch is null ? null : new BranchInfo(new BranchId(branch.Id), new CompanyId(branch.CompanyId), branch.Code, branch.Name, branch.DimensionValueId, branch.IsActive);
        _branchCache[id.Value] = info;
        return info;
    }

    public async Task<Currency?> FindCurrencyAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (_currencyCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var iso = await db.IsoCurrencies.SingleOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        Currency? currency = iso is null ? null : Currency.Of(iso.Code, iso.MinorUnits);
        _currencyCache[normalized] = currency;
        return currency;
    }

    private async Task<CompanyInfo> ToInfoAsync(Company company, CancellationToken cancellationToken)
    {
        var functional = (await FindCurrencyAsync(company.FunctionalCurrency, cancellationToken))!.Value;
        var reporting = company.ReportingCurrency is null ? null : await FindCurrencyAsync(company.ReportingCurrency, cancellationToken);
        return new CompanyInfo(new CompanyId(company.Id), company.Code, company.LegalName, company.Country, functional, reporting, company.TimeZone, company.DefaultLanguage,
            company.RoundingMode == "half_even" ? RoundingMode.HalfEven : RoundingMode.HalfAwayFromZero,
            company.CostingMethod, company.CostingScope, company.TaxRoundingMode, company.NegativeStockPolicy, company.BankRevaluationMode,
            company.FiscalCalendarId, company.BusinessCalendarId, company.IsActive, company.ChartId, company.PostingProfileId);
    }

    // ------------------------------------------------------------------ mapping

    internal static CompanySummary Map(Company c) => new(
        c.Id, c.Code, c.LegalName.Values, c.TradeName.Values, c.Country, c.FunctionalCurrency, c.ReportingCurrency, c.TimeZone, c.DefaultLanguage,
        c.FiscalCalendarId, c.BusinessCalendarId, c.CostingMethod, c.CostingScope, c.RevenueRecognitionPoint, c.TaxRoundingMode, c.RoundingMode,
        c.NegativeStockPolicy, c.BankRevaluationMode, c.RegistrationNumbers, c.Address, c.IsActive, JsonDocument.Parse(c.CustomFields).RootElement.Clone(), c.UpdatedAt, ChartId: c.ChartId, PostingProfileId: c.PostingProfileId);

    internal static BranchSummary Map(Branch b) => new(b.Id, b.CompanyId, b.Code, b.Name.Values, b.Address, b.TaxRegistrations, b.DimensionValueId, b.IsActive);

    private static SettingSummary Map(Setting s) => new(s.Id, s.CompanyId, s.Key, JsonDocument.Parse(s.ValueJson).RootElement.Clone(), s.ValueType, s.UpdatedBy, s.UpdatedAt);
}

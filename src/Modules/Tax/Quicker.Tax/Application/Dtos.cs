namespace Quicker.Tax.Application;

public sealed record TaxTemplateSummary(string Code, string Country, string Version, IReadOnlyDictionary<string, string> Name, string Family, int Codes, int Rules, bool Installed);

public sealed record TaxRegimeSummary(
    Guid Id,
    string Code,
    string Country,
    IReadOnlyDictionary<string, string> Name,
    string Family,
    string RoundingLevel,
    string TaxPoint,
    string ReturnFrequency,
    string? EinvoicingScheme,
    string? TemplateCode,
    string? TemplateVersion,
    bool IsActive,
    int Codes,
    int Rules,
    int Registrations,
    DateTimeOffset UpdatedAt);

public sealed record SaveTaxRegimeRequest(IReadOnlyDictionary<string, string> Name, string RoundingLevel, string TaxPoint, string ReturnFrequency, string? EinvoicingScheme, bool IsActive = true);

public sealed record TaxRateDto(DateOnly ValidFrom, decimal RatePct);

public sealed record TaxCodeSummary(
    Guid Id,
    Guid RegimeId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string Treatment,
    bool IsRecoverable,
    bool IsReverseCharge,
    string AppliesTo,
    string? ExemptionReasonCode,
    IReadOnlyDictionary<string, string> ExemptionReason,
    string OutputAccountRole,
    string InputAccountRole,
    string? SalesBaseBox,
    string? SalesTaxBox,
    string? PurchaseBaseBox,
    string? PurchaseTaxBox,
    bool IsActive,
    decimal? CurrentRatePct,
    IReadOnlyList<TaxRateDto> Rates);

public sealed record SaveTaxCodeRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    string Treatment,
    IReadOnlyList<TaxRateDto> Rates,
    bool IsRecoverable = true,
    bool IsReverseCharge = false,
    string AppliesTo = "both",
    string? ExemptionReasonCode = null,
    IReadOnlyDictionary<string, string>? ExemptionReason = null,
    string OutputAccountRole = "OutputTax",
    string InputAccountRole = "InputTax",
    string? SalesBaseBox = null,
    string? SalesTaxBox = null,
    string? PurchaseBaseBox = null,
    string? PurchaseTaxBox = null,
    bool IsActive = true);

public sealed record TaxGroupSummary(Guid Id, string Kind, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveTaxGroupRequest(string Kind, string Code, IReadOnlyDictionary<string, string> Name, bool IsActive = true);

public sealed record TaxRuleSummary(
    Guid Id,
    Guid RegimeId,
    string Direction,
    Guid? ItemTaxGroupId,
    string? ItemTaxGroupCode,
    Guid? PartnerTaxGroupId,
    string? PartnerTaxGroupCode,
    string? ShipFromCountry,
    string? ShipToCountry,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    Guid TaxCodeId,
    string TaxCode,
    DateTimeOffset UpdatedAt);

public sealed record SaveTaxRuleRequest(string Direction, Guid TaxCodeId, Guid? ItemTaxGroupId = null, Guid? PartnerTaxGroupId = null, string? ShipFromCountry = null, string? ShipToCountry = null, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

public sealed record TaxRegimeDetail(TaxRegimeSummary Regime, IReadOnlyList<TaxCodeSummary> Codes, IReadOnlyList<TaxRuleSummary> Rules);

public sealed record CompanyTaxRegistrationSummary(Guid Id, Guid CompanyId, Guid RegimeId, string RegimeCode, IReadOnlyDictionary<string, string> RegimeName, string? RegistrationNumber, DateOnly? RegisteredFrom, bool IsPrimary, DateTimeOffset UpdatedAt);

public sealed record SaveCompanyTaxRegistrationRequest(Guid CompanyId, Guid RegimeId, string? RegistrationNumber, DateOnly? RegisteredFrom, bool IsPrimary = true);

public sealed record TaxExemptionSummary(Guid Id, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid RegimeId, string RegimeCode, Guid TaxCodeId, string TaxCode, string CertificateNumber, DateOnly ValidFrom, DateOnly? ValidTo, string? Notes, DateTimeOffset UpdatedAt);

public sealed record SaveTaxExemptionRequest(Guid PartnerId, Guid RegimeId, Guid TaxCodeId, string CertificateNumber, DateOnly ValidFrom, DateOnly? ValidTo = null, string? Notes = null);

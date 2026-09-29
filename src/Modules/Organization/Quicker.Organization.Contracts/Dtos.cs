using Quicker.Kernel.Text;

namespace Quicker.Organization.Contracts;

// Companies
public sealed record CreateCompanyRequest(
    string Code,
    LocalizedText LegalName,
    LocalizedText? TradeName,
    string Country,
    string FunctionalCurrency,
    string? ReportingCurrency,
    Guid? FiscalCalendarId,
    Guid? BusinessCalendarId,
    string TimeZone = "UTC",
    string DefaultLanguage = "en");

public sealed record CompanySummary(
    Guid Id,
    string Code,
    LocalizedText LegalName,
    LocalizedText? TradeName,
    string Country,
    string FunctionalCurrency,
    string? ReportingCurrency,
    string TimeZone,
    string DefaultLanguage);

// Branches
public sealed record CreateBranchRequest(
    Guid CompanyId,
    string Code,
    LocalizedText Name,
    string? Address = null);

public sealed record BranchSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    LocalizedText Name,
    bool IsActive);

// Fiscal calendars
public sealed record CreateFiscalCalendarRequest(
    string Code,
    LocalizedText Name,
    int StartMonth,
    int PeriodsPerYear = 12);

public sealed record FiscalCalendarSummary(
    Guid Id,
    string Code,
    LocalizedText Name,
    int StartMonth,
    int PeriodsPerYear);

// Currencies (read-only, seeded)
public sealed record CurrencySummary(
    string Code,
    string NumericCode,
    int MinorUnits,
    string? Symbol,
    LocalizedText Name,
    bool IsActive);

// Exchange rates
public sealed record CreateExchangeRateRequest(
    Guid RateTypeId,
    string FromCurrency,
    string ToCurrency,
    DateOnly ValidFrom,
    decimal Rate,
    string Source = "manual",
    string? Reason = null);

public sealed record ExchangeRateSummary(
    Guid Id,
    Guid RateTypeId,
    string FromCurrency,
    string ToCurrency,
    DateOnly ValidFrom,
    decimal Rate,
    string Source);

// Dimensions
public sealed record CreateDimensionRequest(
    string Code,
    LocalizedText Name,
    bool IsHierarchical = false);

public sealed record DimensionSummary(
    Guid Id,
    string Code,
    LocalizedText Name,
    bool IsSystem,
    bool IsHierarchical);

// Dimension values
public sealed record CreateDimensionValueRequest(
    Guid DimensionId,
    string Code,
    LocalizedText Name,
    Guid? ParentId = null,
    Guid? CompanyId = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null);

public sealed record DimensionValueSummary(
    Guid Id,
    Guid DimensionId,
    string Code,
    LocalizedText Name,
    Guid? ParentId,
    Guid? CompanyId,
    bool IsActive);

// Units of measure
public sealed record CreateUomRequest(
    string Code,
    LocalizedText Name,
    string Family,
    int Precision = 0);

public sealed record UomSummary(
    Guid Id,
    string Code,
    LocalizedText Name,
    string Family,
    int Precision);

// Business calendars
public sealed record CreateBusinessCalendarRequest(
    LocalizedText Name,
    Dictionary<string, bool>? WorkingDays = null);

public sealed record BusinessCalendarSummary(
    Guid Id,
    LocalizedText Name,
    Dictionary<string, bool> WorkingDays);

using System.Text.Json;

namespace Quicker.Organization.Application;

// Request and response shapes of the organization API. Names are language maps ({"en": ..., "ar": ...}).

public sealed record SaveCompanyRequest(
    string Code,
    IReadOnlyDictionary<string, string> LegalName,
    string Country,
    string FunctionalCurrency,
    string TimeZone,
    IReadOnlyDictionary<string, string>? TradeName = null,
    string? ReportingCurrency = null,
    string DefaultLanguage = "en",
    Guid? FiscalCalendarId = null,
    Guid? BusinessCalendarId = null,
    string CostingMethod = "average",
    string CostingScope = "company",
    string RevenueRecognitionPoint = "invoice",
    string TaxRoundingMode = "line",
    string RoundingMode = "half_away",
    string NegativeStockPolicy = "block",
    string BankRevaluationMode = "permanent",
    IReadOnlyDictionary<string, string>? RegistrationNumbers = null,
    IReadOnlyDictionary<string, string>? Address = null,
    bool IsActive = true,
    JsonElement? CustomFields = null);

public sealed record CompanySummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> LegalName,
    IReadOnlyDictionary<string, string> TradeName,
    string Country,
    string FunctionalCurrency,
    string? ReportingCurrency,
    string TimeZone,
    string DefaultLanguage,
    Guid FiscalCalendarId,
    Guid BusinessCalendarId,
    string CostingMethod,
    string CostingScope,
    string RevenueRecognitionPoint,
    string TaxRoundingMode,
    string RoundingMode,
    string NegativeStockPolicy,
    string BankRevaluationMode,
    IReadOnlyDictionary<string, string> RegistrationNumbers,
    IReadOnlyDictionary<string, string> Address,
    bool IsActive,
    JsonElement CustomFields,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BranchSummary>? Branches = null,
    Guid? ChartId = null,
    Guid? PostingProfileId = null);

public sealed record SaveBranchRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string>? Address = null,
    IReadOnlyDictionary<string, string>? TaxRegistrations = null,
    bool IsActive = true);

public sealed record BranchSummary(
    Guid Id,
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Address,
    IReadOnlyDictionary<string, string> TaxRegistrations,
    Guid DimensionValueId,
    bool IsActive);

public sealed record CompanyCurrencyRequest(string Currency, int? DisplayDecimals = null, decimal CashRoundingIncrement = 0m, bool IsEnabled = true);

public sealed record CompanyCurrencySummary(string Currency, int MinorUnits, int DisplayDecimals, decimal CashRoundingIncrement, bool IsEnabled, bool IsFunctional);

public sealed record SettingRequest(JsonElement Value, string ValueType);

public sealed record SettingSummary(Guid Id, Guid? CompanyId, string Key, JsonElement Value, string ValueType, Guid? UpdatedBy, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ fiscal calendars

public sealed record SaveFiscalCalendarRequest(string Code, IReadOnlyDictionary<string, string> Name, int StartMonth = 1, int PeriodsPerYear = 12);

public sealed record FiscalPeriodSummary(Guid Id, int Number, DateOnly StartsOn, DateOnly EndsOn, bool IsAdjustment);

public sealed record FiscalYearSummary(Guid Id, Guid CalendarId, string Code, DateOnly StartsOn, DateOnly EndsOn, string Status, IReadOnlyList<FiscalPeriodSummary> Periods);

public sealed record FiscalCalendarSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, int StartMonth, int PeriodsPerYear, bool IsSystem, IReadOnlyList<FiscalYearSummary> Years);

/// <summary>Opens the fiscal year that starts in <paramref name="StartYear"/> (on the calendar's start month) as <c>open</c> or <c>future</c>.</summary>
public sealed record OpenFiscalYearRequest(int StartYear, string Status = "open");

public sealed record SetPeriodStateRequest(Guid CompanyId, IReadOnlyList<string> Modules, string State, string? Reason = null);

public sealed record ReopenPeriodRequest(Guid CompanyId, IReadOnlyList<string> Modules, string Reason, string State = "open");

public sealed record PeriodStateSummary(Guid PeriodId, Guid CompanyId, string Module, string State, bool IsExplicit, Guid? ChangedBy, DateTimeOffset? ChangedAt, string? Reason);

public sealed record PeriodResolution(Guid PeriodId, Guid FiscalYearId, string FiscalYearCode, int Number, DateOnly StartsOn, DateOnly EndsOn, string YearStatus, string Module, string State);

/// <summary>One allow-posting window: for everyone when <paramref name="RoleId"/> is null, else for that role; at least one bound.</summary>
public sealed record PostingWindowRequest(Guid? RoleId, DateOnly? AllowFrom, DateOnly? AllowTo, string? Reason = null);

public sealed record PostingWindowSummary(Guid Id, Guid CompanyId, Guid? RoleId, DateOnly? AllowFrom, DateOnly? AllowTo, string? Reason, Guid? ChangedBy, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ currencies and rates

public sealed record CurrencySummary(string Code, string NumericCode, int MinorUnits, string Symbol, IReadOnlyDictionary<string, string> Name, bool IsActive);

public sealed record SaveRateTypeRequest(string Code, IReadOnlyDictionary<string, string> Name);

public sealed record RateTypeSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, bool IsSystem);

public sealed record SaveRateRequest(string RateType, string FromCurrency, string ToCurrency, DateOnly ValidFrom, decimal Rate, string? Reason = null);

public sealed record RateSummary(Guid Id, string RateType, string FromCurrency, string ToCurrency, DateOnly ValidFrom, decimal Rate, string Source, Guid? EnteredBy, string? Reason, DateTimeOffset CreatedAt);

public sealed record RateResolution(string FromCurrency, string ToCurrency, DateOnly Date, string RateType, decimal Rate, string Method, DateOnly EffectiveFrom, IReadOnlyList<Guid> RateIds);

public sealed record ImportRatesRequest(string Provider, string RateType = "spot", string? BaseCurrency = null, IReadOnlyList<string>? Currencies = null, DateOnly? Date = null);

public sealed record ImportRatesResult(string Provider, string RateType, DateOnly Date, int Imported, int Updated, int Unchanged, int Skipped, IReadOnlyList<string> Currencies);

// ------------------------------------------------------------------ dimensions

public sealed record SaveDimensionRequest(string Code, IReadOnlyDictionary<string, string> Name, bool IsHierarchical = false, int SortOrder = 0, bool IsActive = true);

public sealed record DimensionSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, bool IsSystem, bool IsHierarchical, int SortOrder, bool IsActive);

public sealed record SaveDimensionValueRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid? ParentId = null,
    Guid? CompanyId = null,
    Guid? OwnerMembershipId = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsActive = true);

public sealed record DimensionValueSummary(
    Guid Id,
    Guid DimensionId,
    Guid? ParentId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid? CompanyId,
    Guid? OwnerMembershipId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive);

public sealed record DimensionSetRequest(IReadOnlyDictionary<string, Guid> Values);

public sealed record DimensionSetSummary(Guid Id, IReadOnlyDictionary<string, Guid> Values, string Hash);

// ------------------------------------------------------------------ units of measure

public sealed record SaveUomRequest(string Code, IReadOnlyDictionary<string, string> Name, string Family, int Precision = 0, bool IsActive = true);

public sealed record UomSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string Family, int Precision, bool IsSystem, bool IsActive);

public sealed record SaveUomConversionRequest(Guid FromUomId, Guid ToUomId, decimal Numerator, decimal Denominator = 1m);

public sealed record UomConversionSummary(Guid Id, Guid FromUomId, string FromCode, Guid ToUomId, string ToCode, decimal Numerator, decimal Denominator);

public sealed record QuantityConversion(decimal Value, string FromCode, decimal Result, string ToCode, decimal Numerator, decimal Denominator, string Method);

// ------------------------------------------------------------------ business calendars

public sealed record SaveBusinessCalendarRequest(string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyList<int> WorkingDays);

public sealed record SaveHolidayRequest(DateOnly OnDate, IReadOnlyDictionary<string, string> Name);

public sealed record HolidaySummary(Guid Id, DateOnly OnDate, IReadOnlyDictionary<string, string> Name);

public sealed record BusinessCalendarSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyList<int> WorkingDays, bool IsSystem, IReadOnlyList<HolidaySummary> Holidays);

public sealed record WorkingDayComputation(DateOnly From, bool FromIsWorkingDay, int Days, string Mode, DateOnly Result);

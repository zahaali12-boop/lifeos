using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Organization.Domain;

/// <summary>control.currencies: ISO 4217 reference data, read-only for the application.</summary>
public sealed class IsoCurrency
{
    public string Code { get; set; } = string.Empty;

    public string NumericCode { get; set; } = string.Empty;

    public int MinorUnits { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsActive { get; set; } = true;
}

public sealed class FiscalCalendar : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public int StartMonth { get; set; } = 1;

    public int PeriodsPerYear { get; set; } = 12;

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<FiscalYear> Years { get; set; } = [];
}

public sealed class FiscalYear : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }

    public string Code { get; set; } = string.Empty;

    public DateOnly StartsOn { get; set; }

    public DateOnly EndsOn { get; set; }

    public string Status { get; set; } = "future";

    public Guid? ClosingEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<FiscalPeriod> Periods { get; set; } = [];

    public bool Covers(DateOnly date) => StartsOn <= date && date <= EndsOn;
}

public sealed class FiscalPeriod : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid FiscalYearId { get; set; }

    public int Number { get; set; }

    public DateOnly StartsOn { get; set; }

    public DateOnly EndsOn { get; set; }

    public bool IsAdjustment { get; set; }

    public bool Covers(DateOnly date) => !IsAdjustment && StartsOn <= date && date <= EndsOn;
}

public sealed class PeriodModuleState : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid PeriodId { get; set; }

    public Guid CompanyId { get; set; }

    public string Module { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public Guid? ChangedBy { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public string? Reason { get; set; }
}

public sealed class BusinessCalendar : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    /// <summary>Days of the week that are working days, 0 = Sunday … 6 = Saturday.</summary>
    public int[] WorkingDays { get; set; } = [];

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<Holiday> Holidays { get; set; } = [];

    public bool IsWorkingDay(DateOnly date) =>
        Array.IndexOf(WorkingDays, (int)date.DayOfWeek) >= 0 && !Holidays.Exists(h => h.OnDate == date);

    public DateOnly NextWorkingDay(DateOnly date)
    {
        var candidate = date;
        while (!IsWorkingDay(candidate))
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    public DateOnly AddWorkingDays(DateOnly date, int workingDays)
    {
        if (workingDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workingDays), "Working days must not be negative.");
        }

        var result = NextWorkingDay(date);
        for (var i = 0; i < workingDays; i++)
        {
            result = NextWorkingDay(result.AddDays(1));
        }

        return result;
    }
}

public sealed class Holiday : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CalendarId { get; set; }

    public DateOnly OnDate { get; set; }

    public LocalizedText Name { get; set; } = new();
}

public sealed class Company : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText LegalName { get; set; } = new();

    public LocalizedText TradeName { get; set; } = new();

    public string Country { get; set; } = string.Empty;

    public string FunctionalCurrency { get; set; } = string.Empty;

    public string? ReportingCurrency { get; set; }

    public Guid? ChartId { get; set; }

    public Guid FiscalCalendarId { get; set; }

    public Guid BusinessCalendarId { get; set; }

    public string TimeZone { get; set; } = "UTC";

    public string DefaultLanguage { get; set; } = "en";

    public string CostingMethod { get; set; } = "average";

    public string CostingScope { get; set; } = "company";

    public string RevenueRecognitionPoint { get; set; } = "invoice";

    public string TaxRoundingMode { get; set; } = "line";

    public string RoundingMode { get; set; } = "half_away";

    public string NegativeStockPolicy { get; set; } = "block";

    public string BankRevaluationMode { get; set; } = "permanent";

    public Guid? PostingProfileId { get; set; }

    public Dictionary<string, string> RegistrationNumbers { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Address { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Tenant-defined values validated against the Collaboration custom-field definitions for "company".</summary>
    public string CustomFields { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Branch : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public Dictionary<string, string> Address { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> TaxRegistrations { get; set; } = new(StringComparer.Ordinal);

    public Guid DimensionValueId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CompanyCurrency : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public int DisplayDecimals { get; set; }

    public decimal CashRoundingIncrement { get; set; }

    public bool IsEnabled { get; set; } = true;
}

public sealed class ExchangeRateType : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.org_exchange_rates: 1 from_currency = rate to_currency from valid_from on (ADR-0017).</summary>
public sealed class RateEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RateTypeId { get; set; }

    public string FromCurrency { get; set; } = string.Empty;

    public string ToCurrency { get; set; } = string.Empty;

    public DateOnly ValidFrom { get; set; }

    public decimal Rate { get; set; }

    public string Source { get; set; } = "manual";

    public Guid? EnteredBy { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Dimension : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsSystem { get; set; }

    public bool IsHierarchical { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DimensionValue : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid DimensionId { get; set; }

    public Guid? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public Guid? CompanyId { get; set; }

    public Guid? OwnerMembershipId { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DimensionSet : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public byte[] Hash { get; set; } = [];

    public Dictionary<string, Guid> Values { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Uom : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Family { get; set; } = "count";

    public int Precision { get; set; }

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.org_uom_conversions: quantity in from_uom × numerator / denominator = quantity in to_uom.</summary>
public sealed class UomConversionRow : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid FromUomId { get; set; }

    public Guid ToUomId { get; set; }

    public decimal Numerator { get; set; }

    public decimal Denominator { get; set; } = 1m;
}

public sealed class Setting : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? CompanyId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = "null";

    public string ValueType { get; set; } = "json";

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

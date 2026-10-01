using Quicker.Identity.Contracts;

namespace Quicker.Organization;

public static class OrganizationPermissions
{
    public const string CompanyRead = "organization.company.read";
    public const string CompanyManage = "organization.company.manage";
    public const string CalendarManage = "organization.calendar.manage";
    public const string CurrencyManage = "organization.currency.manage";
    public const string RateManage = "organization.rate.manage";
    public const string RateImport = "organization.rate.import";
    public const string DimensionManage = "organization.dimension.manage";
    public const string UomManage = "organization.uom.manage";
    public const string SettingsManage = "organization.settings.manage";

    // Period control is an accounting duty (ADR-0026) and lives under the accounting prefix so the shipped
    // accountant role and the default SoD rule (journal.post × period.reopen) cover it.
    public const string PeriodManage = "accounting.period.manage";
    public const string PeriodReopen = "accounting.period.reopen";
    public const string PeriodPostInSoftClosed = "accounting.period.post_in_soft_closed";

    public static readonly PermissionDefinition[] All =
    [
        new(CompanyRead, "organization", "Read companies, branches, calendars, currencies, rates, dimensions and units"),
        new(CompanyManage, "organization", "Create and edit companies, branches and company currencies"),
        new(CalendarManage, "organization", "Manage fiscal calendars, fiscal years, business calendars and holidays"),
        new(CurrencyManage, "organization", "Manage exchange rate types"),
        new(RateManage, "organization", "Enter and correct exchange rates"),
        new(RateImport, "organization", "Import exchange rates from a provider"),
        new(DimensionManage, "organization", "Manage dimensions and their values"),
        new(UomManage, "organization", "Manage units of measure and conversions"),
        new(SettingsManage, "organization", "Manage tenant and company settings"),
        new(PeriodManage, "accounting", "Open, soft-close and hard-close fiscal periods per module"),
        new(PeriodReopen, "accounting", "Reopen a hard-closed period (reason required, audited)", IsSensitive: true),
        new(PeriodPostInSoftClosed, "accounting", "Post into soft-closed periods"),
    ];
}

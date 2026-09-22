using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Organization.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Organization.Persistence;

public sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<IsoCurrency> IsoCurrencies => Set<IsoCurrency>();

    public DbSet<FiscalCalendar> FiscalCalendars => Set<FiscalCalendar>();

    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();

    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();

    public DbSet<PeriodModuleState> PeriodStates => Set<PeriodModuleState>();

    public DbSet<BusinessCalendar> BusinessCalendars => Set<BusinessCalendar>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<CompanyCurrency> CompanyCurrencies => Set<CompanyCurrency>();

    public DbSet<ExchangeRateType> RateTypes => Set<ExchangeRateType>();

    public DbSet<RateEntry> Rates => Set<RateEntry>();

    public DbSet<Dimension> Dimensions => Set<Dimension>();

    public DbSet<DimensionValue> DimensionValues => Set<DimensionValue>();

    public DbSet<DimensionSet> DimensionSets => Set<DimensionSet>();

    public DbSet<Uom> Uoms => Set<Uom>();

    public DbSet<UomConversionRow> UomConversions => Set<UomConversionRow>();

    public DbSet<Setting> Settings => Set<Setting>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // jsonb maps are stored as text so no dynamic JSON opt-in is needed on the shared data source; the comparer
    // makes EF detect in-place edits and keeps the audit diff exact.
    private static readonly ValueConverter<Dictionary<string, string>, string> StringMapConverter = new(
        static map => JsonSerializer.Serialize(map, JsonOptions),
        static json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, string>> StringMapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        static map => JsonSerializer.Serialize(map, JsonOptions).GetHashCode(StringComparison.Ordinal),
        static map => new Dictionary<string, string>(map, StringComparer.Ordinal));

    private static readonly ValueConverter<Dictionary<string, Guid>, string> GuidMapConverter = new(
        static map => JsonSerializer.Serialize(map, JsonOptions),
        static json => JsonSerializer.Deserialize<Dictionary<string, Guid>>(json, JsonOptions) ?? new Dictionary<string, Guid>(StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, Guid>> GuidMapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        static map => JsonSerializer.Serialize(map, JsonOptions).GetHashCode(StringComparison.Ordinal),
        static map => new Dictionary<string, Guid>(map, StringComparer.Ordinal));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IsoCurrency>(b =>
        {
            b.ToTable("currencies", "control");
            b.HasKey(static c => c.Code);
            b.Property(static c => c.Name).HasColumnName("name_i18n");
        });

        modelBuilder.Entity<FiscalCalendar>(b =>
        {
            b.ToTable("org_fiscal_calendars", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Name).HasColumnName("name_i18n");
            b.HasMany(static c => c.Years).WithOne().HasForeignKey(static y => new { y.TenantId, y.CalendarId });
            b.HasAuditTrail("fiscal_calendar", static c => c.Code);
        });

        modelBuilder.Entity<FiscalYear>(b =>
        {
            b.ToTable("org_fiscal_years", "app");
            b.HasKey(static y => new { y.TenantId, y.Id });
            b.HasMany(static y => y.Periods).WithOne().HasForeignKey(static p => new { p.TenantId, p.FiscalYearId });
            b.HasAuditTrail("fiscal_year", static y => y.Code);
        });

        modelBuilder.Entity<FiscalPeriod>(b =>
        {
            b.ToTable("org_fiscal_periods", "app");
            b.HasKey(static p => new { p.TenantId, p.Id });
        });

        modelBuilder.Entity<PeriodModuleState>(b =>
        {
            b.ToTable("org_period_module_states", "app");
            b.HasKey(static s => new { s.TenantId, s.PeriodId, s.CompanyId, s.Module });
            b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(static s => new { s.TenantId, s.PeriodId });
            b.HasOne<Company>().WithMany().HasForeignKey(static s => new { s.TenantId, s.CompanyId });
        });

        modelBuilder.Entity<BusinessCalendar>(b =>
        {
            b.ToTable("org_business_calendars", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Name).HasColumnName("name_i18n");
            b.HasMany(static c => c.Holidays).WithOne().HasForeignKey(static h => new { h.TenantId, h.CalendarId });
            b.HasAuditTrail("business_calendar", static c => c.Code);
        });

        modelBuilder.Entity<Holiday>(b =>
        {
            b.ToTable("org_holidays", "app");
            b.HasKey(static h => new { h.TenantId, h.Id });
            b.Property(static h => h.Name).HasColumnName("name_i18n");
        });

        modelBuilder.Entity<Company>(b =>
        {
            b.ToTable("org_companies", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.LegalName).HasColumnName("legal_name_i18n");
            b.Property(static c => c.TradeName).HasColumnName("trade_name_i18n");
            b.Property(static c => c.RegistrationNumbers).HasConversion(StringMapConverter, StringMapComparer).HasColumnType("jsonb");
            b.Property(static c => c.Address).HasConversion(StringMapConverter, StringMapComparer).HasColumnType("jsonb");
            b.Property(static c => c.CustomFields).HasColumnType("jsonb");
            b.HasOne<FiscalCalendar>().WithMany().HasForeignKey(static c => new { c.TenantId, c.FiscalCalendarId });
            b.HasOne<BusinessCalendar>().WithMany().HasForeignKey(static c => new { c.TenantId, c.BusinessCalendarId });
            b.HasAuditTrail("company", static c => c.Code);
        });

        modelBuilder.Entity<Branch>(b =>
        {
            b.ToTable("org_branches", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Address).HasConversion(StringMapConverter, StringMapComparer).HasColumnType("jsonb");
            b.Property(static x => x.TaxRegistrations).HasConversion(StringMapConverter, StringMapComparer).HasColumnType("jsonb");
            b.HasOne<Company>().WithMany().HasForeignKey(static x => new { x.TenantId, x.CompanyId });
            b.HasOne<DimensionValue>().WithMany().HasForeignKey(static x => new { x.TenantId, x.DimensionValueId });
            b.HasAuditTrail("branch", static x => x.Code);
        });

        modelBuilder.Entity<CompanyCurrency>(b =>
        {
            b.ToTable("org_company_currencies", "app");
            b.HasKey(static c => new { c.TenantId, c.CompanyId, c.Currency });
            b.HasOne<Company>().WithMany().HasForeignKey(static c => new { c.TenantId, c.CompanyId });
        });

        modelBuilder.Entity<ExchangeRateType>(b =>
        {
            b.ToTable("org_exchange_rate_types", "app");
            b.HasKey(static t => new { t.TenantId, t.Id });
            b.Property(static t => t.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("exchange_rate_type", static t => t.Code);
        });

        modelBuilder.Entity<RateEntry>(b =>
        {
            b.ToTable("org_exchange_rates", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.Rate).HasPrecision(24, 12);
            b.HasOne<ExchangeRateType>().WithMany().HasForeignKey(static r => new { r.TenantId, r.RateTypeId });
            b.HasAuditTrail("exchange_rate", static r => $"{r.FromCurrency}/{r.ToCurrency} {r.ValidFrom:yyyy-MM-dd}");
        });

        modelBuilder.Entity<Dimension>(b =>
        {
            b.ToTable("org_dimensions", "app");
            b.HasKey(static d => new { d.TenantId, d.Id });
            b.Property(static d => d.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("dimension", static d => d.Code);
        });

        modelBuilder.Entity<DimensionValue>(b =>
        {
            b.ToTable("org_dimension_values", "app");
            b.HasKey(static v => new { v.TenantId, v.Id });
            b.Property(static v => v.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("dimension_value", static v => v.Code);
            b.HasOne<Dimension>().WithMany().HasForeignKey(static v => new { v.TenantId, v.DimensionId });
            b.HasOne<DimensionValue>().WithMany().HasForeignKey(static v => new { v.TenantId, v.ParentId });
            b.HasOne<Company>().WithMany().HasForeignKey(static v => new { v.TenantId, v.CompanyId });
        });

        modelBuilder.Entity<DimensionSet>(b =>
        {
            b.ToTable("org_dimension_sets", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.Values).HasConversion(GuidMapConverter, GuidMapComparer).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Uom>(b =>
        {
            b.ToTable("org_uoms", "app");
            b.HasKey(static u => new { u.TenantId, u.Id });
            b.Property(static u => u.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("uom", static u => u.Code);
        });

        modelBuilder.Entity<UomConversionRow>(b =>
        {
            b.ToTable("org_uom_conversions", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Numerator).HasPrecision(24, 12);
            b.Property(static c => c.Denominator).HasPrecision(24, 12);
            b.HasOne<Uom>().WithMany().HasForeignKey(static c => new { c.TenantId, c.FromUomId });
            b.HasOne<Uom>().WithMany().HasForeignKey(static c => new { c.TenantId, c.ToUomId });
            b.HasAuditTrail("uom_conversion", static c => $"{c.FromUomId:N}->{c.ToUomId:N}");
        });

        modelBuilder.Entity<Setting>(b =>
        {
            b.ToTable("org_settings", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            b.HasOne<Company>().WithMany().HasForeignKey(static s => new { s.TenantId, s.CompanyId });
            b.HasAuditTrail("setting", static s => s.Key);
        });

        base.OnModelCreating(modelBuilder);
    }
}

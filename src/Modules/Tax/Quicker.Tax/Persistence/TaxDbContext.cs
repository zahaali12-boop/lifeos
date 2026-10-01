using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Tax.Domain;

namespace Quicker.Tax.Persistence;

public sealed class TaxDbContext(DbContextOptions<TaxDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<TaxRegime> Regimes => Set<TaxRegime>();

    public DbSet<TaxCode> Codes => Set<TaxCode>();

    public DbSet<TaxRate> Rates => Set<TaxRate>();

    public DbSet<TaxGroup> Groups => Set<TaxGroup>();

    public DbSet<TaxRule> Rules => Set<TaxRule>();

    public DbSet<TaxRegistration> Registrations => Set<TaxRegistration>();

    public DbSet<TaxExemption> Exemptions => Set<TaxExemption>();

    public DbSet<TaxReturnPeriod> ReturnPeriods => Set<TaxReturnPeriod>();

    public DbSet<TaxEntry> Entries => Set<TaxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<TaxRegime>(b =>
        {
            b.ToTable("tax_regimes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("tax_regime", static x => x.Code);
        });

        modelBuilder.Entity<TaxCode>(b =>
        {
            b.ToTable("tax_codes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.ExemptionReason).HasColumnName("exemption_reason_i18n");
            b.HasMany(static x => x.Rates).WithOne().HasForeignKey(static r => new { r.TenantId, r.TaxCodeId });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasAuditTrail("tax_code", static x => x.Code);
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("tax_rates", "app");
            b.HasKey(static x => new { x.TenantId, x.TaxCodeId, x.ValidFrom });
            b.Property(static x => x.RatePct).HasPrecision(9, 4);
        });

        modelBuilder.Entity<TaxGroup>(b =>
        {
            b.ToTable("tax_groups", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("tax_group", static x => x.Code);
        });

        modelBuilder.Entity<TaxRule>(b =>
        {
            b.ToTable("tax_determination_rules", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxCode>().WithMany().HasForeignKey(static x => new { x.TenantId, x.TaxCodeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxGroup>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ItemTaxGroupId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxGroup>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PartnerTaxGroupId }).OnDelete(DeleteBehavior.Restrict);
            b.HasAuditTrail("tax_rule", static x => x.Id.ToString());
        });

        modelBuilder.Entity<TaxRegistration>(b =>
        {
            b.ToTable("tax_registrations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasAuditTrail("tax_registration", static x => x.RegistrationNumber ?? x.Id.ToString());
        });

        modelBuilder.Entity<TaxExemption>(b =>
        {
            b.ToTable("tax_exemptions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxCode>().WithMany().HasForeignKey(static x => new { x.TenantId, x.TaxCodeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasAuditTrail("tax_exemption", static x => x.CertificateNumber);
        });

        modelBuilder.Entity<TaxReturnPeriod>(b =>
        {
            b.ToTable("tax_return_periods", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.Property(static x => x.Totals).HasColumnType("jsonb");
            b.HasAuditTrail("tax_return_period", static x => x.PeriodStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        });

        modelBuilder.Entity<TaxEntry>(b =>
        {
            b.ToTable("tax_entries", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<TaxRegime>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegimeId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxCode>().WithMany().HasForeignKey(static x => new { x.TenantId, x.TaxCodeId }).OnDelete(DeleteBehavior.Restrict);
            b.Property(static x => x.RatePct).HasPrecision(9, 4);
            b.Property(static x => x.BaseTc).HasPrecision(24, 6);
            b.Property(static x => x.TaxTc).HasPrecision(24, 6);
            b.Property(static x => x.BaseFc).HasPrecision(24, 6);
            b.Property(static x => x.TaxFc).HasPrecision(24, 6);
        });

        base.OnModelCreating(modelBuilder);
    }
}

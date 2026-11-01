using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Reporting.Domain;

namespace Quicker.Reporting.Persistence;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Report> Reports => Set<Report>();

    public DbSet<ReportShare> Shares => Set<ReportShare>();

    public DbSet<FinancialLayout> Layouts => Set<FinancialLayout>();

    public DbSet<LayoutRow> LayoutRows => Set<LayoutRow>();

    public DbSet<LayoutColumn> LayoutColumns => Set<LayoutColumn>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<FinancialLayout>(b =>
        {
            b.ToTable("rpt_financial_layouts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Description).HasColumnName("description_i18n");
            b.HasMany(static x => x.Rows).WithOne().HasForeignKey(static r => new { r.TenantId, r.LayoutId });
            b.HasMany(static x => x.Columns).WithOne().HasForeignKey(static c => new { c.TenantId, c.LayoutId });
            b.HasAuditTrail("financial_layout", static x => x.Code);
        });

        modelBuilder.Entity<LayoutRow>(b =>
        {
            b.ToTable("rpt_layout_rows", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Label).HasColumnName("label_i18n");
            b.Property(static x => x.AccountFilter).HasColumnType("jsonb");
            b.Property(static x => x.DimensionFilter).HasColumnType("jsonb");
        });

        modelBuilder.Entity<LayoutColumn>(b =>
        {
            b.ToTable("rpt_layout_columns", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Label).HasColumnName("label_i18n");
            b.Property(static x => x.Parameters).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Report>(b =>
        {
            b.ToTable("rpt_reports", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Description).HasColumnName("description_i18n");
            b.Property(static x => x.Definition).HasColumnType("jsonb");
            b.Property(static x => x.Chart).HasColumnType("jsonb");
            b.HasMany(static x => x.Shares).WithOne().HasForeignKey(static s => new { s.TenantId, s.ReportId });
            b.HasAuditTrail("saved_report", static x => x.Name.Resolve("en", "ar"));
        });

        modelBuilder.Entity<ReportShare>(b =>
        {
            b.ToTable("rpt_report_shares", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}

using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Reporting.Domain;

namespace Quicker.Reporting.Persistence;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Report> Reports => Set<Report>();

    public DbSet<ReportShare> Shares => Set<ReportShare>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
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

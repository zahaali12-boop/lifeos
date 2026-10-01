using Microsoft.EntityFrameworkCore;
using Quicker.Numbering.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Numbering.Persistence;

public sealed class NumberingDbContext(DbContextOptions<NumberingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Series> Series => Set<Series>();

    public DbSet<SeriesCounter> Counters => Set<SeriesCounter>();

    public DbSet<Allocation> Allocations => Set<Allocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Series>(b =>
        {
            b.ToTable("num_series", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.HasAuditTrail("numbering_series", static s => s.Code);
        });

        modelBuilder.Entity<SeriesCounter>(b =>
        {
            b.ToTable("num_series_counters", "app");
            b.HasKey(static c => new { c.TenantId, c.SeriesId, c.PeriodKey });
            b.HasOne<Series>().WithMany().HasForeignKey(static c => new { c.TenantId, c.SeriesId });
        });

        modelBuilder.Entity<Allocation>(b =>
        {
            b.ToTable("num_allocations", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.HasOne<Series>().WithMany().HasForeignKey(static a => new { a.TenantId, a.SeriesId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

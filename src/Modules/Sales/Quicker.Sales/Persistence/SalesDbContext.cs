using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Sales.Domain;

namespace Quicker.Sales.Persistence;

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<SalesQuotation> Quotations => Set<SalesQuotation>();

    public DbSet<SalesQuotationLine> QuotationLines => Set<SalesQuotationLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<SalesQuotation>(b =>
        {
            b.ToTable("sls_quotations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CustomerSnapshot).HasColumnType("jsonb");
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.QuotationId });
            b.HasAuditTrail("sls_quotation", static x => x.Number);
        });

        modelBuilder.Entity<SalesQuotationLine>(b =>
        {
            b.ToTable("sls_quotation_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.PriceBreakdown).HasColumnType("jsonb");
        });

        base.OnModelCreating(modelBuilder);
    }
}

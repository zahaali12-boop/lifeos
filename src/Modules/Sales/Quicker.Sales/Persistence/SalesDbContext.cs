using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Sales.Domain;

namespace Quicker.Sales.Persistence;

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<SalesQuotation> Quotations => Set<SalesQuotation>();

    public DbSet<SalesQuotationLine> QuotationLines => Set<SalesQuotationLine>();

    public DbSet<SalesOrder> Orders => Set<SalesOrder>();

    public DbSet<SalesOrderLine> OrderLines => Set<SalesOrderLine>();

    public DbSet<SalesShipment> Shipments => Set<SalesShipment>();

    public DbSet<SalesShipmentLine> ShipmentLines => Set<SalesShipmentLine>();

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

        modelBuilder.Entity<SalesOrder>(b =>
        {
            b.ToTable("sls_orders", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CustomerSnapshot).HasColumnType("jsonb");
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.OrderId });
            b.HasAuditTrail("sls_order", static x => x.Number);
        });

        modelBuilder.Entity<SalesOrderLine>(b =>
        {
            b.ToTable("sls_order_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.PriceBreakdown).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SalesShipment>(b =>
        {
            b.ToTable("sls_shipments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ShipmentId });
            b.HasAuditTrail("sls_shipment", static x => x.Number);
        });

        modelBuilder.Entity<SalesShipmentLine>(b =>
        {
            b.ToTable("sls_shipment_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.SleIds).HasColumnType("jsonb");
            b.Property(static x => x.SerialNumbers).HasColumnType("jsonb");
        });

        base.OnModelCreating(modelBuilder);
    }
}

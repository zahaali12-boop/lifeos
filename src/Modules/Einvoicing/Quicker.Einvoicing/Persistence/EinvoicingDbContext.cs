using Microsoft.EntityFrameworkCore;
using Quicker.Einvoicing.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Einvoicing.Persistence;

public sealed class EinvoicingDbContext(DbContextOptions<EinvoicingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<EinvoiceDevice> Devices => Set<EinvoiceDevice>();

    public DbSet<EinvoiceSubmission> Submissions => Set<EinvoiceSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<EinvoiceDevice>(b =>
        {
            b.ToTable("einv_devices", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Seller).HasColumnType("jsonb");
            b.Property(static x => x.ComplianceChecks).HasColumnType("jsonb");
            b.HasAuditTrail("einvoice_device", static x => x.Name);
        });

        modelBuilder.Entity<EinvoiceSubmission>(b =>
        {
            b.ToTable("einv_submissions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Warnings).HasColumnType("jsonb");
            b.Property(static x => x.Errors).HasColumnType("jsonb");
            b.HasOne<EinvoiceDevice>().WithMany().HasForeignKey(static x => new { x.TenantId, x.DeviceId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

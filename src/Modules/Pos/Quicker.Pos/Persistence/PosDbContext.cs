using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Pos.Domain;

namespace Quicker.Pos.Persistence;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<PosRegister> Registers => Set<PosRegister>();

    public DbSet<PosSession> Sessions => Set<PosSession>();

    public DbSet<PosOrder> Orders => Set<PosOrder>();

    public DbSet<PosOrderLine> OrderLines => Set<PosOrderLine>();

    public DbSet<PosPayment> Payments => Set<PosPayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PosRegister>(b =>
        {
            b.ToTable("pos_registers", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.ReceiptFooter).HasColumnName("receipt_footer_i18n");
            b.HasAuditTrail("pos_register", static x => x.Code);
        });

        modelBuilder.Entity<PosSession>(b =>
        {
            b.ToTable("pos_sessions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<PosRegister>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RegisterId });
            b.HasAuditTrail("pos_session", static x => x.Number);
        });

        modelBuilder.Entity<PosOrder>(b =>
        {
            b.ToTable("pos_orders", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.EinvoiceMessages).HasColumnType("jsonb");
            b.HasOne<PosSession>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SessionId });
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.OrderId });
            b.HasMany(static x => x.Payments).WithOne().HasForeignKey(static p => new { p.TenantId, p.OrderId });
            b.HasAuditTrail("pos_order", static x => x.Number);
        });

        modelBuilder.Entity<PosOrderLine>(b =>
        {
            b.ToTable("pos_order_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Description).HasColumnName("description_i18n");
        });

        modelBuilder.Entity<PosPayment>(b =>
        {
            b.ToTable("pos_payments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}

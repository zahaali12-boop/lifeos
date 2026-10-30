using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Receivables.Domain;

namespace Quicker.Receivables.Persistence;

public sealed class ReceivablesDbContext(DbContextOptions<ReceivablesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<ArOpenItem> OpenItems => Set<ArOpenItem>();

    public DbSet<ArSettlement> Settlements => Set<ArSettlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ArOpenItem>(b =>
        {
            b.ToTable("ar_open_items", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.DiscountPct).HasPrecision(9, 6);
            b.Property(static x => x.OriginalTc).HasPrecision(24, 6);
            b.Property(static x => x.OriginalFc).HasPrecision(24, 6);
            b.Property(static x => x.BookedRate).HasPrecision(24, 12);
            b.Property(static x => x.SettledTc).HasPrecision(24, 6);
            b.Property(static x => x.SettledFc).HasPrecision(24, 6);
            b.Property(static x => x.RemainingTc).HasPrecision(24, 6);
            b.Property(static x => x.RemainingFc).HasPrecision(24, 6);
        });

        modelBuilder.Entity<ArSettlement>(b =>
        {
            b.ToTable("ar_settlements", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcSettledItem).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcSettlingItem).HasPrecision(24, 6);
            b.Property(static x => x.SettlementRate).HasPrecision(24, 12);
            b.Property(static x => x.FxGainLossFc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTakenTc).HasPrecision(24, 6);
            b.Property(static x => x.WhtDeductedTc).HasPrecision(24, 6);
            b.Property(static x => x.WriteOffTc).HasPrecision(24, 6);
            b.Property(static x => x.BankChargeTc).HasPrecision(24, 6);
            b.HasOne<ArOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SettlingItemId });
            b.HasOne<ArOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SettledItemId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

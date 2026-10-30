using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Receivables.Domain;

namespace Quicker.Receivables.Persistence;

public sealed class ReceivablesDbContext(DbContextOptions<ReceivablesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<ArOpenItem> OpenItems => Set<ArOpenItem>();

    public DbSet<ArSettlement> Settlements => Set<ArSettlement>();

    public DbSet<ArWriteOff> WriteOffs => Set<ArWriteOff>();

    public DbSet<ArDunningLevel> DunningLevels => Set<ArDunningLevel>();

    public DbSet<ArDunningRun> DunningRuns => Set<ArDunningRun>();

    public DbSet<ArDunningLetter> DunningLetters => Set<ArDunningLetter>();

    public DbSet<ArNetting> Nettings => Set<ArNetting>();

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

        modelBuilder.Entity<ArWriteOff>(b =>
        {
            b.ToTable("ar_write_offs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.AmountFc).HasPrecision(24, 6);
            b.HasOne<ArOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OpenItemId });
        });

        modelBuilder.Entity<ArDunningLevel>(b =>
        {
            b.ToTable("ar_dunning_levels", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Text).HasColumnName("text_i18n");
            b.Property(static x => x.FeeAmount).HasPrecision(24, 6);
        });
        modelBuilder.Entity<ArDunningRun>(b =>
        {
            b.ToTable("ar_dunning_runs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.FeesFc).HasPrecision(24, 6);
        });
        modelBuilder.Entity<ArDunningLetter>(b =>
        {
            b.ToTable("ar_dunning_letters", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Items).HasColumnType("jsonb");
            b.Property(static x => x.OverdueFc).HasPrecision(24, 6);
            b.Property(static x => x.FeeAmount).HasPrecision(24, 6);
            b.HasOne<ArDunningRun>().WithMany().HasForeignKey(static x => new { x.TenantId, x.RunId });
        });
        modelBuilder.Entity<ArNetting>(b =>
        {
            b.ToTable("ar_nettings", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcReceivable).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcPayable).HasPrecision(24, 6);
            b.Property(static x => x.FxGainLossFc).HasPrecision(24, 6);
            b.HasOne<ArOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ReceivableItemId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

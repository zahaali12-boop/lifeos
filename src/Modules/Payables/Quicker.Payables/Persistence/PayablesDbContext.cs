using Microsoft.EntityFrameworkCore;
using Quicker.Payables.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Payables.Persistence;

public sealed class PayablesDbContext(DbContextOptions<PayablesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<ApOpenItem> OpenItems => Set<ApOpenItem>();

    public DbSet<ApSettlement> Settlements => Set<ApSettlement>();

    public DbSet<PaymentProposal> Proposals => Set<PaymentProposal>();

    public DbSet<PaymentProposalLine> ProposalLines => Set<PaymentProposalLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ApOpenItem>(b =>
        {
            b.ToTable("ap_open_items", "app");
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

        modelBuilder.Entity<ApSettlement>(b =>
        {
            b.ToTable("ap_settlements", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcSettledItem).HasPrecision(24, 6);
            b.Property(static x => x.AmountFcSettlingItem).HasPrecision(24, 6);
            b.Property(static x => x.SettlementRate).HasPrecision(24, 12);
            b.Property(static x => x.FxGainLossFc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTakenTc).HasPrecision(24, 6);
            b.Property(static x => x.WhtWithheldTc).HasPrecision(24, 6);
            b.Property(static x => x.WriteOffTc).HasPrecision(24, 6);
            b.Property(static x => x.BankChargeTc).HasPrecision(24, 6);
            b.HasOne<ApOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SettlingItemId });
            b.HasOne<ApOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SettledItemId });
        });

        modelBuilder.Entity<PaymentProposal>(b =>
        {
            b.ToTable("ap_payment_proposals", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.TotalTc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTc).HasPrecision(24, 6);
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ProposalId });
            b.HasAuditTrail("payment_proposal", static x => x.Number);
        });

        modelBuilder.Entity<PaymentProposalLine>(b =>
        {
            b.ToTable("ap_payment_proposal_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTc).HasPrecision(24, 6);
            b.HasOne<ApOpenItem>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OpenItemId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

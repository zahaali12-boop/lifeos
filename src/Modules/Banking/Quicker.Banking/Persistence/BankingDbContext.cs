using Microsoft.EntityFrameworkCore;
using Quicker.Banking.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Banking.Persistence;

public sealed class BankingDbContext(DbContextOptions<BankingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentLine> PaymentLines => Set<PaymentLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<BankAccount>(b =>
        {
            b.ToTable("bnk_bank_accounts", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("bank_account", static x => x.Code);
        });

        modelBuilder.Entity<BankTransaction>(b =>
        {
            b.ToTable("bnk_bank_transactions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.AmountFc).HasPrecision(24, 6);
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BankAccountId });
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("bnk_payments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.OnAccountTc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTc).HasPrecision(24, 6);
            b.Property(static x => x.WhtTc).HasPrecision(24, 6);
            b.Property(static x => x.ChargesBank).HasPrecision(24, 6);
            b.Property(static x => x.BankAmount).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.PaymentId });
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BankAccountId });
            b.HasAuditTrail("bank_payment", static x => x.Number);
        });

        modelBuilder.Entity<PaymentLine>(b =>
        {
            b.ToTable("bnk_payment_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountTc).HasPrecision(24, 6);
            b.Property(static x => x.DiscountTc).HasPrecision(24, 6);
            b.Property(static x => x.WhtTc).HasPrecision(24, 6);
        });

        base.OnModelCreating(modelBuilder);
    }
}

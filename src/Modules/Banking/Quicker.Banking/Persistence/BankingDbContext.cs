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

    public DbSet<ChequeBook> ChequeBooks => Set<ChequeBook>();

    public DbSet<Cheque> Cheques => Set<Cheque>();

    public DbSet<ChequeEvent> ChequeEvents => Set<ChequeEvent>();

    public DbSet<ChequeDeposit> ChequeDeposits => Set<ChequeDeposit>();

    public DbSet<StatementMapping> StatementMappings => Set<StatementMapping>();

    public DbSet<BankStatement> Statements => Set<BankStatement>();

    public DbSet<StatementLine> StatementLines => Set<StatementLine>();

    public DbSet<ReconciliationRule> ReconciliationRules => Set<ReconciliationRule>();

    public DbSet<ReconciliationMatch> ReconciliationMatches => Set<ReconciliationMatch>();

    public DbSet<BankReconciliation> Reconciliations => Set<BankReconciliation>();

    public DbSet<BankTransfer> Transfers => Set<BankTransfer>();

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

        modelBuilder.Entity<ChequeBook>(b =>
        {
            b.ToTable("bnk_cheque_books", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasAuditTrail("bank_cheque_book", static x => x.Prefix + x.FirstNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        modelBuilder.Entity<Cheque>(b =>
        {
            b.ToTable("bnk_cheques", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Amount).HasPrecision(24, 6);
            b.Property(static x => x.AmountFc).HasPrecision(24, 6);
            b.HasMany(static x => x.Events).WithOne().HasForeignKey(static e => new { e.TenantId, e.ChequeId });
            // The relationships are declared so the unit of work writes the document, the book and the slip before the cheque that points at them.
            b.HasOne<Payment>().WithMany().HasForeignKey(static x => new { x.TenantId, x.PaymentId });
            b.HasOne<ChequeBook>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ChequeBookId });
            b.HasOne<ChequeDeposit>().WithMany().HasForeignKey(static x => new { x.TenantId, x.DepositId });
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BankAccountId });
            b.HasAuditTrail("bank_cheque", static x => x.ChequeNumber);
        });

        modelBuilder.Entity<ChequeEvent>(b =>
        {
            b.ToTable("bnk_cheque_events", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.BankCharge).HasPrecision(24, 6);
            b.Property(static x => x.CustomerFee).HasPrecision(24, 6);
        });

        modelBuilder.Entity<ChequeDeposit>(b =>
        {
            b.ToTable("bnk_cheque_deposits", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Total).HasPrecision(24, 6);
            b.HasAuditTrail("bank_cheque_deposit", static x => x.Number);
        });

        modelBuilder.Entity<StatementMapping>(b =>
        {
            b.ToTable("bnk_statement_mappings", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Settings).HasColumnType("jsonb");
        });

        modelBuilder.Entity<BankStatement>(b =>
        {
            b.ToTable("bnk_statements", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.OpeningBalance).HasPrecision(24, 6);
            b.Property(static x => x.ClosingBalance).HasPrecision(24, 6);
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.StatementId });
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BankAccountId });
            b.HasAuditTrail("bank_statement", static x => x.FileName);
        });

        modelBuilder.Entity<StatementLine>(b =>
        {
            b.ToTable("bnk_statement_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Amount).HasPrecision(24, 6);
        });

        modelBuilder.Entity<ReconciliationRule>(b =>
        {
            b.ToTable("bnk_reconciliation_rules", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Conditions).HasColumnType("jsonb");
            b.HasAuditTrail("bank_reconciliation_rule", static x => x.Name.Resolve("en"));
        });

        modelBuilder.Entity<ReconciliationMatch>(b =>
        {
            b.ToTable("bnk_reconciliation_matches", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Confidence).HasPrecision(5, 4);
            b.HasOne<StatementLine>().WithMany().HasForeignKey(static x => new { x.TenantId, x.StatementLineId });
            b.HasOne<BankTransaction>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BankTransactionId });
        });

        modelBuilder.Entity<BankTransfer>(b =>
        {
            b.ToTable("bnk_transfers", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountSent).HasPrecision(24, 6);
            b.Property(static x => x.AmountReceived).HasPrecision(24, 6);
            b.Property(static x => x.ExchangeRate).HasPrecision(24, 12);
            b.Property(static x => x.AmountSentFc).HasPrecision(24, 6);
            b.Property(static x => x.AmountReceivedFc).HasPrecision(24, 6);
            b.Property(static x => x.ExchangeDifferenceFc).HasPrecision(24, 6);
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.FromBankAccountId });
            b.HasOne<BankAccount>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ToBankAccountId });
            b.HasAuditTrail("bank_transfer", static x => x.Number);
        });

        modelBuilder.Entity<BankReconciliation>(b =>
        {
            b.ToTable("bnk_reconciliations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.StatementBalance).HasPrecision(24, 6);
            b.Property(static x => x.BookBalance).HasPrecision(24, 6);
            b.Property(static x => x.DepositsInTransit).HasPrecision(24, 6);
            b.Property(static x => x.OutstandingPayments).HasPrecision(24, 6);
            b.Property(static x => x.UnmatchedBankItems).HasPrecision(24, 6);
            b.Property(static x => x.Difference).HasPrecision(24, 6);
            b.HasAuditTrail("bank_reconciliation", static x => x.Number);
        });

        base.OnModelCreating(modelBuilder);
    }
}

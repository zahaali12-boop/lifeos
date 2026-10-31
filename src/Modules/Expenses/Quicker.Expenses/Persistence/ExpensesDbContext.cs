using Microsoft.EntityFrameworkCore;
using Quicker.Expenses.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Expenses.Persistence;

public sealed class ExpensesDbContext(DbContextOptions<ExpensesDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<ExpenseCategory> Categories => Set<ExpenseCategory>();

    public DbSet<PettyCashFund> Funds => Set<PettyCashFund>();

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<ExpenseLine> Lines => Set<ExpenseLine>();

    public DbSet<FundReplenishment> Replenishments => Set<FundReplenishment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ExpenseCategory>(b =>
        {
            b.ToTable("exp_categories", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.MaxAmount).HasPrecision(24, 6);
            b.HasAuditTrail("expense_category", static x => x.Code);
        });

        modelBuilder.Entity<PettyCashFund>(b =>
        {
            b.ToTable("exp_funds", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.FloatAmount).HasPrecision(24, 6);
            b.HasAuditTrail("petty_cash_fund", static x => x.Code);
        });

        modelBuilder.Entity<Expense>(b =>
        {
            b.ToTable("exp_expenses", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.TotalNet).HasPrecision(24, 6);
            b.Property(static x => x.TotalTax).HasPrecision(24, 6);
            b.Property(static x => x.TotalGross).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ExpenseId });
            b.HasAuditTrail("expense", static x => x.Number ?? x.Id.ToString("N"));
        });

        modelBuilder.Entity<ExpenseLine>(b =>
        {
            b.ToTable("exp_expense_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Amount).HasPrecision(24, 6);
            b.Property(static x => x.TaxRatePct).HasPrecision(9, 4);
            b.Property(static x => x.TaxAmount).HasPrecision(24, 6);
        });

        modelBuilder.Entity<FundReplenishment>(b =>
        {
            b.ToTable("exp_replenishments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Amount).HasPrecision(24, 6);
            b.HasAuditTrail("fund_replenishment", static x => x.Number);
        });

        base.OnModelCreating(modelBuilder);
    }
}

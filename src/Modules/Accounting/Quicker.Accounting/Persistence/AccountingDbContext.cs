using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Accounting.Persistence;

public sealed class AccountingDbContext(DbContextOptions<AccountingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Chart> Charts => Set<Chart>();

    public DbSet<AccountCategory> Categories => Set<AccountCategory>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccountDimensionRule> DimensionRules => Set<AccountDimensionRule>();

    public DbSet<AccountMapping> Mappings => Set<AccountMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Chart>(b =>
        {
            b.ToTable("gl_charts", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("gl_chart", static c => c.Code);
        });

        modelBuilder.Entity<AccountCategory>(b =>
        {
            b.ToTable("gl_account_categories", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("gl_account_category", static c => c.Code);
        });

        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("gl_accounts", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.Property(static a => a.Name).HasColumnName("name_i18n");
            b.HasOne<Chart>().WithMany().HasForeignKey(static a => new { a.TenantId, a.ChartId });
            b.HasOne<Account>().WithMany().HasForeignKey(static a => new { a.TenantId, a.ParentId });
            b.HasOne<AccountCategory>().WithMany().HasForeignKey(static a => new { a.TenantId, a.CategoryId });
            b.HasMany(static a => a.DimensionRules).WithOne().HasForeignKey(static r => new { r.TenantId, r.AccountId });
            b.HasAuditTrail("gl_account", static a => a.Code);
        });

        modelBuilder.Entity<AccountDimensionRule>(b =>
        {
            b.ToTable("gl_account_dimension_rules", "app");
            b.HasKey(static r => new { r.TenantId, r.AccountId, r.DimensionId });
        });

        modelBuilder.Entity<AccountMapping>(b =>
        {
            b.ToTable("gl_account_mappings", "app");
            b.HasKey(static m => new { m.TenantId, m.AccountId, m.StatutoryChartCode });
            b.HasOne<Account>().WithMany().HasForeignKey(static m => new { m.TenantId, m.AccountId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

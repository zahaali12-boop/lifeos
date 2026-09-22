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

    public DbSet<PostingGroup> PostingGroups => Set<PostingGroup>();

    public DbSet<PostingProfile> PostingProfiles => Set<PostingProfile>();

    public DbSet<JournalEntry> Entries => Set<JournalEntry>();

    public DbSet<JournalLine> Lines => Set<JournalLine>();

    public DbSet<EntryLink> Links => Set<EntryLink>();

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

        modelBuilder.Entity<PostingGroup>(b =>
        {
            b.ToTable("gl_posting_groups", "app");
            b.HasKey(static g => new { g.TenantId, g.Id });
            b.Property(static g => g.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("gl_posting_group", static g => g.Code);
        });

        modelBuilder.Entity<PostingProfile>(b =>
        {
            b.ToTable("gl_posting_profiles", "app");
            b.HasKey(static p => new { p.TenantId, p.Id });
            b.Property(static p => p.Name).HasColumnName("name_i18n");
            b.HasMany(static p => p.Rules).WithOne().HasForeignKey(static r => new { r.TenantId, r.ProfileId });
            b.HasAuditTrail("gl_posting_profile", static p => p.Code);
        });

        modelBuilder.Entity<PostingRule>(b =>
        {
            b.ToTable("gl_posting_rules", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.HasOne<Account>().WithMany().HasForeignKey(static r => new { r.TenantId, r.AccountId });
        });

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("gl_journal_entries", "app");
            b.HasKey(static e => new { e.TenantId, e.Id });
            b.Property(static e => e.Description).HasColumnName("description_i18n");
            b.HasMany(static e => e.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.EntryId });
        });

        modelBuilder.Entity<JournalLine>(b =>
        {
            b.ToTable("gl_journal_lines", "app");
            b.HasKey(static l => new { l.TenantId, l.PostingDate, l.Id });
            b.Property(static l => l.Description).HasColumnName("description_i18n");
            b.HasOne<Account>().WithMany().HasForeignKey(static l => new { l.TenantId, l.AccountId });
        });

        modelBuilder.Entity<EntryLink>(b =>
        {
            b.ToTable("gl_entry_links", "app");
            b.HasKey(static l => new { l.TenantId, l.FromEntryId, l.ToEntryId, l.Relation });
        });

        base.OnModelCreating(modelBuilder);
    }
}

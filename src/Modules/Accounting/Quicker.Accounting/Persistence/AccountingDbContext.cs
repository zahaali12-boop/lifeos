using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    public DbSet<ManualJournal> Journals => Set<ManualJournal>();

    public DbSet<RecurringTemplate> RecurringTemplates => Set<RecurringTemplate>();

    public DbSet<DeferralSchedule> Deferrals => Set<DeferralSchedule>();

    public DbSet<RoutineRun> RoutineRuns => Set<RoutineRun>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<Dictionary<string, Guid>, string> GuidMapConverter = new(
        static v => JsonSerializer.Serialize(v, Json),
        static s => JsonSerializer.Deserialize<Dictionary<string, Guid>>(s, Json) ?? new Dictionary<string, Guid>(StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, Guid>> GuidMapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
        static v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
        static v => new Dictionary<string, Guid>(v, StringComparer.Ordinal));

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

        modelBuilder.Entity<ManualJournal>(b =>
        {
            b.ToTable("gl_manual_journals", "app");
            b.HasKey(static j => new { j.TenantId, j.Id });
            b.Property(static j => j.Description).HasColumnName("description_i18n");
            b.Property(static j => j.CustomFields).HasColumnType("jsonb");
            b.HasMany(static j => j.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.JournalId });
            b.HasAuditTrail("manual_journal", static j => j.Number ?? j.Id.ToString());
        });

        modelBuilder.Entity<ManualJournalLine>(b =>
        {
            b.ToTable("gl_manual_journal_lines", "app");
            b.HasKey(static l => new { l.TenantId, l.Id });
            b.Property(static l => l.Description).HasColumnName("description_i18n");
            b.Property(static l => l.Dimensions).HasConversion(GuidMapConverter, GuidMapComparer).HasColumnType("jsonb");
            b.HasOne<Account>().WithMany().HasForeignKey(static l => new { l.TenantId, l.AccountId });
        });

        modelBuilder.Entity<RecurringTemplate>(b =>
        {
            b.ToTable("gl_recurring_templates", "app");
            b.HasKey(static t => new { t.TenantId, t.Id });
            b.Property(static t => t.Name).HasColumnName("name_i18n");
            b.Property(static t => t.Description).HasColumnName("description_i18n");
            b.Property(static t => t.Lines).HasColumnType("jsonb");
            b.HasAuditTrail("gl_recurring_template", static t => t.Code);
        });

        modelBuilder.Entity<RoutineRun>(b =>
        {
            b.ToTable("gl_routine_runs", "app");
            b.HasKey(static r => new { r.TenantId, r.Id });
            b.Property(static r => r.Items).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DeferralSchedule>(b =>
        {
            b.ToTable("gl_deferral_schedules", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.Description).HasColumnName("description_i18n");
            b.Property(static s => s.Dimensions).HasConversion(GuidMapConverter, GuidMapComparer).HasColumnType("jsonb");
            b.HasMany(static s => s.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.ScheduleId });
        });

        modelBuilder.Entity<DeferralLine>(b =>
        {
            b.ToTable("gl_deferral_lines", "app");
            b.HasKey(static l => new { l.TenantId, l.ScheduleId, l.Sequence });
        });

        base.OnModelCreating(modelBuilder);
    }
}

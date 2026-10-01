using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Collaboration.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Collaboration.Persistence;

public sealed class CollaborationDbContext(DbContextOptions<CollaborationDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<Guid[], string> GuidArrayConverter = new(
        static a => JsonSerializer.Serialize(a, JsonOptions),
        static json => JsonSerializer.Deserialize<Guid[]>(json, JsonOptions) ?? Array.Empty<Guid>());

    private static readonly ValueComparer<Guid[]> GuidArrayComparer = new(
        static (a, b) => a != null && b != null && a.SequenceEqual(b),
        static a => a.Aggregate(0, (h, g) => HashCode.Combine(h, g)),
        static a => a.ToArray());

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<EmailLog> Emails => Set<EmailLog>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<DocumentLinkRecord> Links => Set<DocumentLinkRecord>();

    public DbSet<SavedView> Views => Set<SavedView>();

    public DbSet<CustomFieldDefinition> CustomFields => Set<CustomFieldDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("col_comments", "app");
            b.HasKey(static c => new { c.TenantId, c.Id });
            b.Property(static c => c.Mentions).HasConversion(GuidArrayConverter, GuidArrayComparer).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Activity>(b =>
        {
            b.ToTable("col_activities", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.Property(static a => a.Summary).HasColumnName("summary_i18n");
            b.Property(static a => a.Data).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DocumentLinkRecord>(b =>
        {
            b.ToTable("col_document_links", "app");
            b.HasKey(static l => new { l.TenantId, l.Id });
        });

        modelBuilder.Entity<SavedView>(b =>
        {
            b.ToTable("col_saved_views", "app");
            b.HasKey(static v => new { v.TenantId, v.Id });
            b.Property(static v => v.Definition).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CustomFieldDefinition>(b =>
        {
            b.ToTable("col_custom_fields", "app");
            b.HasKey(static f => new { f.TenantId, f.Id });
            b.Property(static f => f.Label).HasColumnName("label_i18n");
            b.Property(static f => f.Description).HasColumnName("description_i18n");
            b.Property(static f => f.Options).HasColumnType("jsonb");
            b.Property(static f => f.Rules).HasColumnType("jsonb");
            b.Property(static f => f.DefaultValue).HasColumnType("jsonb");
            b.HasAuditTrail("custom_field", static f => f.EntityType + "." + f.Key);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("col_notifications", "app");
            b.HasKey(static n => new { n.TenantId, n.Id });
            b.Property(static n => n.Title).HasColumnName("title_i18n");
            b.Property(static n => n.Body).HasColumnName("body_i18n");
            b.Property(static n => n.Data).HasColumnType("jsonb");
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("col_notification_preferences", "app");
            b.HasKey(static p => new { p.TenantId, p.Id });
        });

        modelBuilder.Entity<EmailLog>(b =>
        {
            b.ToTable("col_email_log", "app");
            b.HasKey(static e => new { e.TenantId, e.Id });
        });

        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("col_attachments", "app");
            b.HasKey(static a => new { a.TenantId, a.Id });
            b.HasAuditTrail("attachment", static a => a.FileName);
        });

        base.OnModelCreating(modelBuilder);
    }
}

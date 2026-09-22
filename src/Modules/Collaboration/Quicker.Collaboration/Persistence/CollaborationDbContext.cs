using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Collaboration.Persistence;

public sealed class CollaborationDbContext(DbContextOptions<CollaborationDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<EmailLog> Emails => Set<EmailLog>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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

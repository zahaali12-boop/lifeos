using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Integration.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Integration.Persistence;

public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<string[], string> StringArrayConverter = new(
        static a => JsonSerializer.Serialize(a, JsonOptions),
        static json => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>());

    private static readonly ValueComparer<string[]> StringArrayComparer = new(
        static (a, b) => a != null && b != null && a.SequenceEqual(b),
        static a => a.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode(StringComparison.Ordinal))),
        static a => a.ToArray());

    private static readonly ValueConverter<Dictionary<string, string>, string> MapConverter = new(
        static map => JsonSerializer.Serialize(map, JsonOptions),
        static json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, string>> MapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        static map => JsonSerializer.Serialize(map, JsonOptions).GetHashCode(StringComparison.Ordinal),
        static map => new Dictionary<string, string>(map, StringComparer.Ordinal));

    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookSubscription>(b =>
        {
            b.ToTable("int_webhook_subscriptions", "app");
            b.HasKey(static s => new { s.TenantId, s.Id });
            b.Property(static s => s.EventTypes).HasConversion(StringArrayConverter, StringArrayComparer).HasColumnType("jsonb");
            b.Property(static s => s.Filters).HasConversion(MapConverter, MapComparer).HasColumnType("jsonb");
            b.HasAuditTrail("webhook_subscription", static s => s.Name);
            b.Property(static s => s.SecretEnc).AuditRedact();
        });

        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("int_webhook_deliveries", "app");
            b.HasKey(static d => new { d.TenantId, d.Id });
            b.Property(static d => d.Payload).HasColumnType("jsonb");
            b.HasOne<WebhookSubscription>().WithMany().HasForeignKey(static d => new { d.TenantId, d.SubscriptionId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

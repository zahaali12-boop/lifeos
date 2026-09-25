using Microsoft.EntityFrameworkCore;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;
using Quicker.Pricing.Domain;

namespace Quicker.Pricing.Persistence;

public sealed class PricingDbContext(DbContextOptions<PricingDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<PriceList> PriceLists => Set<PriceList>();

    public DbSet<PriceListAssignment> Assignments => Set<PriceListAssignment>();

    public DbSet<PriceListItem> PriceListItems => Set<PriceListItem>();

    public DbSet<PriceAgreement> Agreements => Set<PriceAgreement>();

    public DbSet<DiscountRule> DiscountRules => Set<DiscountRule>();

    public DbSet<Promotion> Promotions => Set<Promotion>();

    public DbSet<PromotionUsage> PromotionUsages => Set<PromotionUsage>();

    public DbSet<PriceFloor> Floors => Set<PriceFloor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PriceList>(b =>
        {
            b.ToTable("prc_price_lists", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.ParentAdjustmentPct).HasPrecision(9, 4);
            b.Property(static x => x.RoundingIncrement).HasPrecision(24, 10);
            b.Property(static x => x.PriceSurcharge).HasPrecision(24, 10);
            b.HasAuditTrail("price_list", static x => x.Code);
        });

        modelBuilder.Entity<PriceListAssignment>(b =>
        {
            b.ToTable("prc_price_list_assignments", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<PriceListItem>(b =>
        {
            b.ToTable("prc_price_list_items", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.MinQuantity).HasPrecision(24, 9);
            b.Property(static x => x.Price).HasPrecision(24, 10);
            b.HasAuditTrail("price_list_item", static x => x.Id.ToString());
        });

        modelBuilder.Entity<PriceAgreement>(b =>
        {
            b.ToTable("prc_customer_price_agreements", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.MinQuantity).HasPrecision(24, 9);
            b.Property(static x => x.Price).HasPrecision(24, 10);
            b.Property(static x => x.DiscountPct).HasPrecision(9, 4);
            b.HasAuditTrail("price_agreement", static x => x.Reference ?? x.Id.ToString());
        });

        modelBuilder.Entity<DiscountRule>(b =>
        {
            b.ToTable("prc_discount_rules", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.MinQuantity).HasPrecision(24, 9);
            b.Property(static x => x.MinAmount).HasPrecision(24, 6);
            b.Property(static x => x.Value).HasPrecision(24, 10);
            b.HasAuditTrail("discount_rule", static x => x.Code);
        });

        modelBuilder.Entity<Promotion>(b =>
        {
            b.ToTable("prc_promotions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.BuyQuantity).HasPrecision(24, 9);
            b.Property(static x => x.GetQuantity).HasPrecision(24, 9);
            b.Property(static x => x.GetDiscountPct).HasPrecision(9, 4);
            b.Property(static x => x.BundlePrice).HasPrecision(24, 10);
            b.Property(static x => x.DiscountPct).HasPrecision(9, 4);
            b.HasMany(static x => x.Components).WithOne().HasForeignKey(static c => new { c.TenantId, c.PromotionId });
            b.HasMany(static x => x.Tiers).WithOne().HasForeignKey(static t => new { t.TenantId, t.PromotionId });
            b.HasAuditTrail("promotion", static x => x.Code);
        });

        modelBuilder.Entity<PromotionComponent>(b =>
        {
            b.ToTable("prc_promotion_components", "app");
            b.HasKey(static x => new { x.TenantId, x.PromotionId, x.ItemId });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
        });

        modelBuilder.Entity<PromotionTier>(b =>
        {
            b.ToTable("prc_promotion_tiers", "app");
            b.HasKey(static x => new { x.TenantId, x.PromotionId, x.MinQuantity });
            b.Property(static x => x.MinQuantity).HasPrecision(24, 9);
            b.Property(static x => x.DiscountPct).HasPrecision(9, 4);
        });

        modelBuilder.Entity<PromotionUsage>(b =>
        {
            b.ToTable("prc_promotion_usages", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        modelBuilder.Entity<PriceFloor>(b =>
        {
            b.ToTable("prc_price_floors", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.MinPrice).HasPrecision(24, 10);
            b.Property(static x => x.MinMarginPct).HasPrecision(9, 4);
            b.HasAuditTrail("price_floor", static x => x.Id.ToString());
        });

        base.OnModelCreating(modelBuilder);
    }
}

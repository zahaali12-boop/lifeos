using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Items.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Items.Persistence;

public sealed class ItemsDbContext(DbContextOptions<ItemsDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<ItemCategory> Categories => Set<ItemCategory>();

    public DbSet<ItemAttribute> Attributes => Set<ItemAttribute>();

    public DbSet<ItemAttributeValue> AttributeValues => Set<ItemAttributeValue>();

    public DbSet<Item> Items => Set<Item>();

    public DbSet<ItemVariant> Variants => Set<ItemVariant>();

    public DbSet<ItemUom> ItemUoms => Set<ItemUom>();

    public DbSet<ItemBarcode> Barcodes => Set<ItemBarcode>();

    public DbSet<ItemSupplier> Suppliers => Set<ItemSupplier>();

    public DbSet<ItemCompanySettings> CompanySettings => Set<ItemCompanySettings>();

    public DbSet<ItemWarehouseSettings> WarehouseSettings => Set<ItemWarehouseSettings>();

    public DbSet<Bom> Boms => Set<Bom>();

    public DbSet<BomLine> BomLines => Set<BomLine>();

    public DbSet<Substitute> Substitutes => Set<Substitute>();

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
        modelBuilder.Entity<Brand>(b =>
        {
            b.ToTable("itm_brands", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("itm_brand", static x => x.Code);
        });

        modelBuilder.Entity<ItemCategory>(b =>
        {
            b.ToTable("itm_item_categories", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasOne<ItemCategory>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ParentId });
            b.HasAuditTrail("itm_item_category", static x => x.Code);
        });

        modelBuilder.Entity<ItemAttribute>(b =>
        {
            b.ToTable("itm_attributes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasMany(static x => x.Values).WithOne().HasForeignKey(static v => new { v.TenantId, v.AttributeId });
            b.HasAuditTrail("itm_attribute", static x => x.Code);
        });

        modelBuilder.Entity<ItemAttributeValue>(b =>
        {
            b.ToTable("itm_attribute_values", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
        });

        modelBuilder.Entity<Item>(b =>
        {
            b.ToTable("itm_items", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Description).HasColumnName("description_i18n");
            b.Property(static x => x.ListPrice).HasPrecision(24, 10);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasOne<ItemCategory>().WithMany().HasForeignKey(static x => new { x.TenantId, x.CategoryId });
            b.HasOne<Brand>().WithMany().HasForeignKey(static x => new { x.TenantId, x.BrandId });
            b.HasMany(static x => x.Uoms).WithOne().HasForeignKey(static u => new { u.TenantId, u.ItemId });
            b.HasMany(static x => x.Variants).WithOne().HasForeignKey(static v => new { v.TenantId, v.ItemId });
            b.HasMany(static x => x.Suppliers).WithOne().HasForeignKey(static s => new { s.TenantId, s.ItemId });
            b.HasAuditTrail("item", static x => x.Code);
        });

        modelBuilder.Entity<ItemVariant>(b =>
        {
            b.ToTable("itm_item_variants", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.AttributeValues).HasConversion(GuidMapConverter, GuidMapComparer).HasColumnType("jsonb");
            b.HasAuditTrail("item_variant", static x => x.Sku);
        });

        modelBuilder.Entity<ItemUom>(b =>
        {
            b.ToTable("itm_item_uoms", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Numerator).HasPrecision(24, 12);
            b.Property(static x => x.Denominator).HasPrecision(24, 12);
            b.Property(static x => x.Dimensions).HasColumnType("jsonb");
            b.HasMany(static x => x.Barcodes).WithOne().HasForeignKey(static c => new { c.TenantId, c.ItemUomId });
        });

        modelBuilder.Entity<ItemBarcode>(b =>
        {
            b.ToTable("itm_item_barcodes", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasOne<ItemVariant>().WithMany().HasForeignKey(static x => new { x.TenantId, x.VariantId });
        });

        modelBuilder.Entity<ItemSupplier>(b =>
        {
            b.ToTable("itm_item_suppliers", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.LastPrice).HasPrecision(24, 10);
        });

        modelBuilder.Entity<ItemCompanySettings>(b =>
        {
            b.ToTable("itm_item_company_settings", "app");
            b.HasKey(static x => new { x.TenantId, x.ItemId, x.CompanyId });
            b.Property(static x => x.StandardCost).HasPrecision(24, 10);
        });

        modelBuilder.Entity<ItemWarehouseSettings>(b =>
        {
            b.ToTable("itm_item_warehouse_settings", "app");
            b.HasKey(static x => new { x.TenantId, x.ItemId, x.WarehouseId });
            b.Property(static x => x.ReorderPoint).HasPrecision(24, 9);
            b.Property(static x => x.MinQty).HasPrecision(24, 9);
            b.Property(static x => x.MaxQty).HasPrecision(24, 9);
            b.Property(static x => x.SafetyStock).HasPrecision(24, 9);
        });

        modelBuilder.Entity<Bom>(b =>
        {
            b.ToTable("itm_boms", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.OutputQty).HasPrecision(24, 9);
            b.HasOne<Item>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ItemId });
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.BomId });
            b.HasAuditTrail("bom", static x => $"{x.ItemId:N}/v{x.Version}");
        });

        modelBuilder.Entity<BomLine>(b =>
        {
            b.ToTable("itm_bom_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.ScrapPct).HasPrecision(9, 4);
            b.HasOne<Item>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ComponentItemId });
        });

        modelBuilder.Entity<Substitute>(b =>
        {
            b.ToTable("itm_substitutes", "app");
            b.HasKey(static x => new { x.TenantId, x.ItemId, x.SubstituteItemId });
            b.HasOne<Item>().WithMany().HasForeignKey(static x => new { x.TenantId, x.ItemId });
            b.HasOne<Item>().WithMany().HasForeignKey(static x => new { x.TenantId, x.SubstituteItemId });
        });

        base.OnModelCreating(modelBuilder);
    }
}

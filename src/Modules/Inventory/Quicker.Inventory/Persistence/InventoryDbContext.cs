using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Quicker.Inventory.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Inventory.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<Bin> Bins => Set<Bin>();

    public DbSet<StockPosting> Postings => Set<StockPosting>();

    public DbSet<StockLedgerEntry> Entries => Set<StockLedgerEntry>();

    public DbSet<StockBalance> Balances => Set<StockBalance>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<Transfer> Transfers => Set<Transfer>();

    public DbSet<TransferLine> TransferLines => Set<TransferLine>();

    public DbSet<ItemCostScope> CostScopes => Set<ItemCostScope>();

    public DbSet<StockValueEntry> ValueEntries => Set<StockValueEntry>();

    public DbSet<ItemApplication> Applications => Set<ItemApplication>();

    public DbSet<ItemCost> ItemCosts => Set<ItemCost>();

    public DbSet<CostAdjustmentRun> CostRuns => Set<CostAdjustmentRun>();

    public DbSet<StandardCostVersion> StandardCosts => Set<StandardCostVersion>();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ValueConverter<Dictionary<string, string>, string> StringMapConverter = new(
        static v => JsonSerializer.Serialize(v, Json),
        static s => JsonSerializer.Deserialize<Dictionary<string, string>>(s, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly ValueConverter<Dictionary<string, object?>, string> ObjectMapConverter = new(
        static v => JsonSerializer.Serialize(v, Json),
        static s => JsonSerializer.Deserialize<Dictionary<string, object?>>(s, Json) ?? new Dictionary<string, object?>(StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, object?>> ObjectMapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
        static v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
        static v => new Dictionary<string, object?>(v, StringComparer.Ordinal));

    private static readonly ValueComparer<Dictionary<string, string>> StringMapComparer = new(
        static (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
        static v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
        static v => new Dictionary<string, string>(v, StringComparer.Ordinal));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("inv_warehouses", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Address).HasConversion(StringMapConverter, StringMapComparer).HasColumnType("jsonb");
            b.HasMany(static x => x.Bins).WithOne().HasForeignKey(static bin => new { bin.TenantId, bin.WarehouseId });
            b.HasAuditTrail("warehouse", static x => x.Code);
        });

        modelBuilder.Entity<Bin>(b =>
        {
            b.ToTable("inv_bins", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasAuditTrail("warehouse_bin", static x => x.Code);
        });

        modelBuilder.Entity<StockPosting>(b =>
        {
            b.ToTable("inv_stock_postings", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.HasMany(static x => x.Entries).WithOne().HasForeignKey(static e => new { e.TenantId, e.PostingId });
        });

        modelBuilder.Entity<StockLedgerEntry>(b =>
        {
            b.ToTable("inv_stock_ledger_entries", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Sequence).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.EnteredQuantity).HasPrecision(24, 9);
            b.Property(static x => x.EnteredUnitCost).HasPrecision(24, 10);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(static x => new { x.TenantId, x.WarehouseId });
        });

        modelBuilder.Entity<StockBalance>(b =>
        {
            b.ToTable("inv_stock_balances", "app");
            b.HasKey(static x => new { x.TenantId, x.CompanyId, x.ItemId, x.VariantId, x.WarehouseId, x.BinId, x.LotId, x.SerialId });
            b.Property(static x => x.OnHand).HasPrecision(24, 9);
            b.Property(static x => x.Reserved).HasPrecision(24, 9);
            b.Property(static x => x.QualityHold).HasPrecision(24, 9);
        });

        modelBuilder.Entity<Reservation>(b =>
        {
            b.ToTable("inv_reservations", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.ConsumedQuantity).HasPrecision(24, 9);
            b.HasAuditTrail("stock_reservation", static x => $"{x.SourceDocumentType}/{x.SourceDocumentId:N}");
        });

        modelBuilder.Entity<Transfer>(b =>
        {
            b.ToTable("inv_transfers", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Lines).WithOne().HasForeignKey(static l => new { l.TenantId, l.TransferId });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(static x => new { x.TenantId, x.FromWarehouseId });
            b.HasAuditTrail("stock_transfer", static x => x.Number ?? x.Id.ToString("N"));
        });

        modelBuilder.Entity<TransferLine>(b =>
        {
            b.ToTable("inv_transfer_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.QtyRequested).HasPrecision(24, 9);
            b.Property(static x => x.QtyShipped).HasPrecision(24, 9);
            b.Property(static x => x.QtyReceived).HasPrecision(24, 9);
            b.Property(static x => x.Tracking).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ItemCostScope>(b =>
        {
            b.ToTable("inv_item_cost_scopes", "app");
            b.HasKey(static x => new { x.TenantId, x.CompanyId, x.ItemId, x.WarehouseId });
            b.Property(static x => x.LastCost).HasPrecision(24, 10);
        });

        modelBuilder.Entity<StockValueEntry>(b =>
        {
            b.ToTable("inv_stock_value_entries", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Ignore(static x => x.Amount);
            b.Property(static x => x.ValuedQuantity).HasPrecision(24, 9);
            b.Property(static x => x.UnitCost).HasPrecision(24, 10);
            b.Property(static x => x.CostAmountActual).HasPrecision(24, 6);
            b.Property(static x => x.CostAmountExpected).HasPrecision(24, 6);
            b.Property(static x => x.Reason).HasConversion(ObjectMapConverter, ObjectMapComparer).HasColumnType("jsonb");
            b.HasOne<StockValueEntry>().WithMany().HasForeignKey(static x => new { x.TenantId, x.AdjustsSveId });
        });

        modelBuilder.Entity<ItemApplication>(b =>
        {
            b.ToTable("inv_item_applications", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.CostAmount).HasPrecision(24, 6);
            b.HasOne<StockLedgerEntry>().WithMany().HasForeignKey(static x => new { x.TenantId, x.OutboundSleId });
            b.HasOne<StockLedgerEntry>().WithMany().HasForeignKey(static x => new { x.TenantId, x.InboundSleId });
        });

        modelBuilder.Entity<ItemCost>(b =>
        {
            b.ToTable("inv_item_costs", "app");
            b.HasKey(static x => new { x.TenantId, x.CompanyId, x.ItemId, x.WarehouseId, x.ValuationDate });
            b.Property(static x => x.Quantity).HasPrecision(24, 9);
            b.Property(static x => x.Value).HasPrecision(24, 6);
            b.Property(static x => x.AverageUnitCost).HasPrecision(24, 10);
            b.Property(static x => x.LastCost).HasPrecision(24, 10);
            b.Property(static x => x.StandardCost).HasPrecision(24, 10);
        });

        modelBuilder.Entity<CostAdjustmentRun>(b =>
        {
            b.ToTable("inv_cost_adjustment_runs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.AmountAdjusted).HasPrecision(24, 6);
        });

        modelBuilder.Entity<StandardCostVersion>(b =>
        {
            b.ToTable("inv_standard_cost_versions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.StandardCost).HasPrecision(24, 10);
            b.HasAuditTrail("standard_cost", static x => $"{x.ItemId:N}@{x.EffectiveFrom:yyyy-MM-dd}");
        });

        base.OnModelCreating(modelBuilder);
    }
}

using Microsoft.EntityFrameworkCore;
using Quicker.FixedAssets.Domain;
using Quicker.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.FixedAssets.Persistence;

public sealed class FixedAssetsDbContext(DbContextOptions<FixedAssetsDbContext> options, IUnitOfWork unitOfWork) : ModuleDbContext(options, unitOfWork)
{
    public DbSet<AssetBook> Books => Set<AssetBook>();

    public DbSet<AssetCategory> Categories => Set<AssetCategory>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<AssetBookValue> AssetBooks => Set<AssetBookValue>();

    public DbSet<ScheduleLine> Schedule => Set<ScheduleLine>();

    public DbSet<AssetTransaction> Transactions => Set<AssetTransaction>();

    public DbSet<DepreciationRun> Runs => Set<DepreciationRun>();

    public DbSet<AssetUsage> Usage => Set<AssetUsage>();

    public DbSet<AssetVerification> Verifications => Set<AssetVerification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AssetBook>(b =>
        {
            b.ToTable("fa_books", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.HasAuditTrail("asset_book", static x => x.Code);
        });

        modelBuilder.Entity<AssetCategory>(b =>
        {
            b.ToTable("fa_asset_categories", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.DefaultSalvagePct).HasPrecision(9, 4);
            b.Property(static x => x.DefaultRatePct).HasPrecision(9, 4);
            b.HasAuditTrail("asset_category", static x => x.Code);
        });

        modelBuilder.Entity<Asset>(b =>
        {
            b.ToTable("fa_assets", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Name).HasColumnName("name_i18n");
            b.Property(static x => x.Cost).HasPrecision(24, 6);
            b.Property(static x => x.Quantity).HasPrecision(18, 4);
            b.Property(static x => x.OpeningAccumulatedDepreciation).HasPrecision(24, 6);
            b.Property(static x => x.CustomFields).HasColumnType("jsonb");
            b.HasMany(static x => x.Books).WithOne().HasForeignKey(static v => new { v.TenantId, v.AssetId });
            b.HasAuditTrail("fixed_asset", static x => x.Number);
        });

        modelBuilder.Entity<AssetBookValue>(b =>
        {
            b.ToTable("fa_asset_books", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.SalvageValue).HasPrecision(24, 6);
            b.Property(static x => x.RatePct).HasPrecision(9, 4);
            b.Property(static x => x.TotalUnits).HasPrecision(24, 6);
            b.Property(static x => x.Cost).HasPrecision(24, 6);
            b.Property(static x => x.AccumulatedDepreciation).HasPrecision(24, 6);
            b.Property(static x => x.AccumulatedImpairment).HasPrecision(24, 6);
            b.Property(static x => x.RevaluationSurplus).HasPrecision(24, 6);
            b.Property(static x => x.ImpairmentRecognised).HasPrecision(24, 6);
            b.Property(static x => x.MonthsConsumed).HasPrecision(10, 4);
            b.Ignore(static x => x.NetBookValue);
            b.HasMany(static x => x.Schedule).WithOne().HasForeignKey(static s => new { s.TenantId, s.AssetBookId });
        });

        modelBuilder.Entity<ScheduleLine>(b =>
        {
            b.ToTable("fa_schedule_lines", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Weight).HasPrecision(10, 6);
            b.Property(static x => x.PlannedAmount).HasPrecision(24, 6);
            b.Property(static x => x.PostedAmount).HasPrecision(24, 6);
        });

        modelBuilder.Entity<AssetTransaction>(b =>
        {
            b.ToTable("fa_transactions", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.CostDelta).HasPrecision(24, 6);
            b.Property(static x => x.AccumulatedDepreciationDelta).HasPrecision(24, 6);
            b.Property(static x => x.ImpairmentDelta).HasPrecision(24, 6);
            b.Property(static x => x.SurplusDelta).HasPrecision(24, 6);
            b.Property(static x => x.Proceeds).HasPrecision(24, 6);
            b.Property(static x => x.GainLoss).HasPrecision(24, 6);
            b.Property(static x => x.Detail).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DepreciationRun>(b =>
        {
            b.ToTable("fa_depreciation_runs", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Total).HasPrecision(24, 6);
            b.HasAuditTrail("depreciation_run", static x => x.Number);
        });

        modelBuilder.Entity<AssetUsage>(b =>
        {
            b.ToTable("fa_usage", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
            b.Property(static x => x.Units).HasPrecision(24, 6);
        });

        modelBuilder.Entity<AssetVerification>(b =>
        {
            b.ToTable("fa_verifications", "app");
            b.HasKey(static x => new { x.TenantId, x.Id });
        });

        base.OnModelCreating(modelBuilder);
    }
}

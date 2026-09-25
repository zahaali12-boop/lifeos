using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Quicker.Kernel.Ids;

namespace Quicker.Persistence.EntityFramework;

/// <summary>Marker for tenant-scoped entities: filtered by the current tenant and stamped on insert.</summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Base DbContext for a module (ADR-0003). It never owns a connection: it is bound to the current unit of work's
/// connection and transaction so EF writes commit together with Dapper writes and the outbox. Conventions:
/// snake_case columns, composite tenant keys declared per entity, global tenant filter on <see cref="ITenantEntity"/>,
/// xmin concurrency tokens, typed-id conversions.
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    private readonly IUnitOfWork _unitOfWork;

    protected ModuleDbContext(DbContextOptions options, IUnitOfWork unitOfWork)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _unitOfWork = unitOfWork;
#pragma warning disable MA0056 // Database is virtual for mocking only; binding the transaction here is the documented EF pattern.
        Database.UseTransaction(unitOfWork.Transaction);
#pragma warning restore MA0056
    }

    /// <summary>
    /// Tenant used by the global query filter, read at query time (a DbContext member so EF parameterises it), so a
    /// unit of work that switches tenant mid-transaction filters correctly from then on.
    /// </summary>
    public Guid CurrentTenantId => _unitOfWork.Context.TenantId.Value;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<TenantId>().HaveConversion<EntityIdConverter<TenantId>>();
        configurationBuilder.Properties<UserId>().HaveConversion<EntityIdConverter<UserId>>();
        configurationBuilder.Properties<MembershipId>().HaveConversion<EntityIdConverter<MembershipId>>();
        configurationBuilder.Properties<CompanyId>().HaveConversion<EntityIdConverter<CompanyId>>();
        configurationBuilder.Properties<BranchId>().HaveConversion<EntityIdConverter<BranchId>>();
        configurationBuilder.Properties<WarehouseId>().HaveConversion<EntityIdConverter<WarehouseId>>();
        configurationBuilder.Properties<decimal>().HavePrecision(20, 6);
        configurationBuilder.Properties<Kernel.Text.LocalizedText>().HaveConversion<LocalizedTextConverter>().HaveColumnType("jsonb");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        JsonFunctions.Register(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is null)
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(entity.GetTableName() + "_" + (key.IsPrimaryKey() ? "pkey" : string.Join("_", key.Properties.Select(static p => p.Name)) + "_key")));
            }

            if (typeof(ITenantEntity).IsAssignableFrom(entity.ClrType) && entity.BaseType is null)
            {
                var parameter = Expression.Parameter(entity.ClrType, "e");
                var tenantProperty = Expression.Property(parameter, nameof(ITenantEntity.TenantId));
                var contextTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
                var body = Expression.Equal(tenantProperty, contextTenant);
                entity.SetQueryFilter(Expression.Lambda(body, parameter));
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTenant()
    {
        var tenantId = CurrentTenantId;
        if (tenantId == Guid.Empty)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }
            else if (entry.State is Microsoft.EntityFrameworkCore.EntityState.Added or Microsoft.EntityFrameworkCore.EntityState.Modified && entry.Entity.TenantId != tenantId)
            {
                throw new InvalidOperationException($"Entity {entry.Metadata.ClrType.Name} belongs to another tenant.");
            }
        }
    }

    public static string ToSnakeCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

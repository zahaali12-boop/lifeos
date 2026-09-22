using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>Warehouses (per company, by kind) and their bins; the directory other modules read.</summary>
public sealed class WarehouseService(InventoryDbContext db, ICompanyDirectory companies, IClock clock) : IWarehouseDirectory
{
    private readonly Dictionary<Guid, WarehouseInfo?> _cache = new();

    public async Task<IReadOnlyList<WarehouseSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Warehouses.AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(w => w.CompanyId == c);
        }

        var warehouses = await query.OrderBy(static w => w.Code).ToListAsync(cancellationToken);
        var ids = warehouses.Select(static w => w.Id).ToList();
        var counts = await db.Bins.Where(b => ids.Contains(b.WarehouseId)).GroupBy(static b => b.WarehouseId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return warehouses.Select(w => Map(w, counts.GetValueOrDefault(w.Id), null)).ToList();
    }

    public async Task<WarehouseSummary?> GetAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await db.Warehouses.Include(static w => w.Bins).SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);
        return warehouse is null ? null : Map(warehouse, warehouse.Bins.Count, warehouse.Bins.OrderBy(static b => b.PickSequence).ThenBy(static b => b.Code, StringComparer.Ordinal).Select(Map).ToList());
    }

    public async Task<Result<WarehouseSummary>> SaveAsync(Guid? warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, "warehouse");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "warehouse");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var kind = Validation.OneOf(request.Kind, "warehouse.kind", Validation.WarehouseKinds);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.BranchId is { } branchId)
        {
            var branch = await companies.FindBranchAsync(new BranchId(branchId), cancellationToken);
            if (branch is null || branch.CompanyId.Value != request.CompanyId)
            {
                return Error.Validation("warehouse.branch_invalid", "The branch must belong to the warehouse's company.").WithWhy(("branchId", branchId));
            }
        }

        Warehouse? warehouse = null;
        if (warehouseId is { } id)
        {
            warehouse = await db.Warehouses.Include(static w => w.Bins).SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (warehouse is null)
            {
                return Error.NotFound("warehouse", id);
            }

            if (warehouse.CompanyId != request.CompanyId)
            {
                return Error.Conflict("warehouse.company_locked", "A warehouse cannot move to another company.");
            }

            if (kind.Value == "in_transit" && request.BinsEnabled)
            {
                return Error.Validation("warehouse.transit_no_bins", "An in-transit warehouse has no bins.");
            }

            if (!request.BinsEnabled && warehouse.BinsEnabled && await db.Balances.AnyAsync(b => b.WarehouseId == id && b.OnHand != 0m && b.BinId != Guid.Empty, cancellationToken))
            {
                return Error.Conflict("warehouse.bins_in_use", "Bins hold stock; move it out before turning bins off.");
            }
        }
        else if (kind.Value == "in_transit" && request.BinsEnabled)
        {
            return Error.Validation("warehouse.transit_no_bins", "An in-transit warehouse has no bins.");
        }

        if (await db.Warehouses.AnyAsync(w => w.CompanyId == request.CompanyId && w.Code == code.Value && (warehouse == null || w.Id != warehouse.Id), cancellationToken))
        {
            return Error.Conflict("warehouse.code_taken", $"A warehouse with code '{code.Value}' already exists in the company.").WithWhy(("code", code.Value));
        }

        var now = clock.UtcNow;
        if (warehouse is null)
        {
            warehouse = new Warehouse { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = now };
            db.Warehouses.Add(warehouse);
        }

        warehouse.Code = code.Value;
        warehouse.Name = name.Value;
        warehouse.Kind = kind.Value;
        warehouse.BranchId = request.BranchId;
        warehouse.BinsEnabled = request.BinsEnabled;
        warehouse.AllowNegativeStock = request.AllowNegativeStock;
        warehouse.Address = new Dictionary<string, string>(request.Address ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        warehouse.IsActive = request.IsActive;
        warehouse.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        _cache.Remove(warehouse.Id);
        return Map(warehouse, warehouse.Bins.Count, null);
    }

    public async Task<Result<IReadOnlyList<BinSummary>>> ListBinsAsync(Guid warehouseId, CancellationToken cancellationToken)
    {
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, cancellationToken))
        {
            return Error.NotFound("warehouse", warehouseId);
        }

        // Ordered by the database (a comparer is not translatable); the collation of the code column decides ties.
        var bins = await db.Bins.Where(b => b.WarehouseId == warehouseId).OrderBy(static b => b.PickSequence).ThenBy(static b => b.Code).ToListAsync(cancellationToken);
        return bins.Select(Map).ToList();
    }

    public async Task<Result<BinSummary>> SaveBinAsync(Guid warehouseId, Guid? binId, SaveBinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);
        if (warehouse is null)
        {
            return Error.NotFound("warehouse", warehouseId);
        }

        if (!warehouse.BinsEnabled)
        {
            return Error.Conflict("warehouse.bins_disabled", "Turn bins on for the warehouse first.").WithWhy(("warehouse", warehouse.Code));
        }

        var code = Validation.UpperCode(request.Code, "bin");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var kind = Validation.OneOf(request.Kind, "bin.kind", Validation.BinKinds);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        Bin? bin = null;
        if (binId is { } id)
        {
            bin = await db.Bins.SingleOrDefaultAsync(b => b.Id == id && b.WarehouseId == warehouseId, cancellationToken);
            if (bin is null)
            {
                return Error.NotFound("bin", id);
            }
        }

        if (await db.Bins.AnyAsync(b => b.WarehouseId == warehouseId && b.Code == code.Value && (bin == null || b.Id != bin.Id), cancellationToken))
        {
            return Error.Conflict("bin.code_taken", $"A bin with code '{code.Value}' already exists in the warehouse.").WithWhy(("code", code.Value));
        }

        var now = clock.UtcNow;
        if (bin is null)
        {
            bin = new Bin { Id = Guid.CreateVersion7(), WarehouseId = warehouseId, CreatedAt = now };
            db.Bins.Add(bin);
        }

        bin.Code = code.Value;
        bin.Zone = string.IsNullOrWhiteSpace(request.Zone) ? null : request.Zone.Trim();
        bin.Kind = kind.Value;
        bin.PickSequence = request.PickSequence;
        bin.IsActive = request.IsActive;
        bin.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Map(bin);
    }

    public async Task<Result> DeleteBinAsync(Guid warehouseId, Guid binId, CancellationToken cancellationToken)
    {
        var bin = await db.Bins.SingleOrDefaultAsync(b => b.Id == binId && b.WarehouseId == warehouseId, cancellationToken);
        if (bin is null)
        {
            return Error.NotFound("bin", binId);
        }

        if (await db.Balances.AnyAsync(b => b.BinId == binId && b.OnHand != 0m, cancellationToken) || await db.Entries.AnyAsync(e => e.BinId == binId, cancellationToken))
        {
            return Error.Conflict("bin.in_use", "The bin holds or has held stock; deactivate it instead.");
        }

        db.Bins.Remove(bin);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ IWarehouseDirectory

    public async Task<WarehouseInfo?> FindAsync(Guid warehouseId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(warehouseId, out var cached))
        {
            return cached;
        }

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Id == warehouseId, cancellationToken);
        var info = warehouse is null ? null : Info(warehouse);
        _cache[warehouseId] = info;
        return info;
    }

    async Task<IReadOnlyList<WarehouseInfo>> IWarehouseDirectory.ListAsync(Guid companyId, CancellationToken cancellationToken) =>
        (await db.Warehouses.Where(w => w.CompanyId == companyId).OrderBy(static w => w.Code).ToListAsync(cancellationToken)).Select(Info).ToList();

    public async Task<BinInfo?> FindBinAsync(Guid binId, CancellationToken cancellationToken = default)
    {
        var bin = await db.Bins.SingleOrDefaultAsync(b => b.Id == binId, cancellationToken);
        return bin is null ? null : new BinInfo(bin.Id, bin.WarehouseId, bin.Code, bin.Zone, bin.Kind, bin.IsActive);
    }

    private static WarehouseInfo Info(Warehouse w) => new(w.Id, w.CompanyId, w.BranchId, w.Code, w.Name, w.Kind, w.BinsEnabled, w.AllowNegativeStock, w.IsActive);

    private static WarehouseSummary Map(Warehouse w, int binCount, IReadOnlyList<BinSummary>? bins) =>
        new(w.Id, w.CompanyId, w.BranchId, w.Code, w.Name.Values, w.Kind, w.BinsEnabled, w.AllowNegativeStock, w.Address, w.IsActive, binCount, w.UpdatedAt, bins);

    private static BinSummary Map(Bin b) => new(b.Id, b.WarehouseId, b.Code, b.Zone, b.Kind, b.PickSequence, b.IsActive, b.UpdatedAt);
}

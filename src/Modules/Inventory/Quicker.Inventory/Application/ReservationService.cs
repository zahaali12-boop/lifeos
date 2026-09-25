using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Inventory.Application;

/// <summary>
/// Reservations hold available quantity for a document. Reserving locks the balance row like a movement does, so
/// two documents cannot both reserve the last unit; a reservation never makes stock negative. The stock posting
/// engine consumes a reservation when the document moves the stock; releasing (by the document or by expiry) gives
/// the quantity back.
/// </summary>
public sealed class ReservationService(InventoryDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IItemDirectory items, IWarehouseDirectory warehouses, ICurrentPrincipal principal, IAuditSink audit, IClock clock) : IStockReservations
{
    private sealed record LockedBalance(decimal OnHand, decimal Reserved, decimal QualityHold);

    public async Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Quantity <= 0m)
        {
            return Error.Validation("reservation.quantity_invalid", "A reservation is for a positive quantity.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceDocumentType) || request.SourceDocumentId == Guid.Empty)
        {
            return Error.Validation("reservation.source_required", "A reservation names the document it holds stock for.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var item = await items.FindAsync(request.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", request.ItemId);
        }

        if (request.VariantId is { } variantId)
        {
            var variant = await items.FindVariantAsync(variantId, cancellationToken);
            if (variant is null || variant.ItemId != item.Id)
            {
                return Error.Validation("reservation.variant_invalid", "The variant must belong to the item.").WithWhy(("item", item.Code), ("variantId", variantId));
            }
        }
        else if (item.HasVariants)
        {
            return Error.Validation("reservation.variant_required", "The item has variants; name the one to reserve.").WithWhy(("item", item.Code));
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value)
        {
            return Error.Validation("reservation.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        if (warehouse.BinsEnabled && request.BinId is null)
        {
            return Error.Validation("reservation.bin_required", "The warehouse uses bins; name the bin to reserve in.").WithWhy(("warehouse", warehouse.Code));
        }

        var converted = await items.ToBaseAsync(item.Id, request.UomId ?? item.BaseUomId, request.Quantity, cancellationToken);
        if (converted.IsFailure)
        {
            return converted.Error!;
        }

        var quantity = converted.Value.Quantity;
        var uow = unitOfWork.Current;
        var locked = await LockAsync(uow, request, cancellationToken);
        var available = locked.OnHand - locked.Reserved - locked.QualityHold;
        if (available < quantity)
        {
            return Error.Conflict("stock.insufficient_to_reserve", $"Only {ItemUomMath.Normalize(available)} {item.BaseUomCode} of {item.Code} is available in {warehouse.Code}.")
                .WithWhy(("item", item.Code), ("warehouse", warehouse.Code), ("onHand", ItemUomMath.Normalize(locked.OnHand)), ("reserved", ItemUomMath.Normalize(locked.Reserved)), ("available", ItemUomMath.Normalize(available)), ("requested", ItemUomMath.Normalize(quantity)));
        }

        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE app.inv_stock_balances SET reserved = reserved + @quantity, updated_at = now()
            WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND variant_id = @variant AND warehouse_id = @warehouse AND bin_id = @bin AND lot_id = @lot AND serial_id = @serial
            """, Keys(uow, request, quantity), uow.Transaction, cancellationToken: cancellationToken));

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            ItemId = item.Id,
            VariantId = request.VariantId,
            WarehouseId = warehouse.Id,
            BinId = request.BinId,
            LotId = request.LotId,
            SerialId = request.SerialId,
            Quantity = quantity,
            SourceDocumentType = request.SourceDocumentType.Trim(),
            SourceDocumentId = request.SourceDocumentId,
            SourceLineId = request.SourceLineId,
            ExpiresOn = request.ExpiresOn,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedBy = principal.Principal?.UserId.Value,
            CreatedAt = clock.UtcNow,
        };
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync(cancellationToken);
        return Map(reservation);
    }

    public async Task<Result<ReservationInfo>> ReleaseAsync(Guid reservationId, string? reason, CancellationToken cancellationToken = default)
    {
        var reservation = await db.Reservations.SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null)
        {
            return Error.NotFound("reservation", reservationId);
        }

        if (reservation.Status != "active")
        {
            return Error.Conflict("reservation.not_active", "The reservation is already closed.").WithWhy(("status", reservation.Status));
        }

        var remaining = reservation.Quantity - reservation.ConsumedQuantity;
        var uow = unitOfWork.Current;
        await LockAsync(uow, new ReservationRequest(reservation.CompanyId, reservation.ItemId, remaining, reservation.WarehouseId, reservation.SourceDocumentType, reservation.SourceDocumentId, VariantId: reservation.VariantId, BinId: reservation.BinId, LotId: reservation.LotId, SerialId: reservation.SerialId), cancellationToken);
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            UPDATE app.inv_stock_balances SET reserved = reserved - @quantity, updated_at = now()
            WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND variant_id = @variant AND warehouse_id = @warehouse AND bin_id = @bin AND lot_id = @lot AND serial_id = @serial
            """, new
        {
            quantity = remaining,
            tenant = uow.Context.TenantId.Value,
            company = reservation.CompanyId,
            item = reservation.ItemId,
            variant = reservation.VariantId ?? Guid.Empty,
            warehouse = reservation.WarehouseId,
            bin = reservation.BinId ?? Guid.Empty,
            lot = reservation.LotId ?? Guid.Empty,
            serial = reservation.SerialId ?? Guid.Empty,
        }, uow.Transaction, cancellationToken: cancellationToken));
        reservation.Status = "released";
        reservation.ClosedAt = clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            reservation.Reason = reason.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("stock_reservation", reservation.Id, $"{reservation.SourceDocumentType}/{reservation.SourceDocumentId:N}", "released", After: new { released = ItemUomMath.Normalize(remaining), reason }, CompanyId: reservation.CompanyId), cancellationToken);
        return Map(reservation);
    }

    public async Task<IReadOnlyList<ReservationInfo>> ForDocumentAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken = default)
    {
        var type = sourceDocumentType?.Trim() ?? string.Empty;
        return (await db.Reservations.Where(r => r.SourceDocumentType == type && r.SourceDocumentId == sourceDocumentId).OrderBy(static r => r.CreatedAt).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<StockAvailability> AvailabilityAsync(Guid companyId, Guid itemId, Guid warehouseId, Guid? variantId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Balances.Where(b => b.CompanyId == companyId && b.ItemId == itemId && b.WarehouseId == warehouseId);
        if (variantId is { } v)
        {
            query = query.Where(b => b.VariantId == v);
        }

        var totals = await query.GroupBy(static b => 1).Select(static g => new { OnHand = g.Sum(static b => b.OnHand), Reserved = g.Sum(static b => b.Reserved), Hold = g.Sum(static b => b.QualityHold) }).SingleOrDefaultAsync(cancellationToken);
        var inTransit = await InTransitAsync(companyId, itemId, warehouseId, variantId, cancellationToken);
        var onHand = totals?.OnHand ?? 0m;
        var reserved = totals?.Reserved ?? 0m;
        var hold = totals?.Hold ?? 0m;
        return new StockAvailability(companyId, itemId, variantId, warehouseId, ItemUomMath.Normalize(onHand), ItemUomMath.Normalize(reserved), ItemUomMath.Normalize(hold), ItemUomMath.Normalize(inTransit), ItemUomMath.Normalize(onHand - reserved - hold));
    }

    /// <summary>Quantity shipped to this warehouse on transfers not yet received, in base units.</summary>
    internal async Task<decimal> InTransitAsync(Guid companyId, Guid itemId, Guid warehouseId, Guid? variantId, CancellationToken cancellationToken)
    {
        var lines = await (from t in db.Transfers
                           join l in db.TransferLines on t.Id equals l.TransferId
                           where t.CompanyId == companyId && t.ToWarehouseId == warehouseId && l.ItemId == itemId && (variantId == null || l.VariantId == variantId)
                                 && (t.Status == "shipped" || t.Status == "partially_received") && l.QtyShipped > l.QtyReceived
                           select new { l.UomId, Outstanding = l.QtyShipped - l.QtyReceived }).ToListAsync(cancellationToken);
        var total = 0m;
        foreach (var line in lines)
        {
            var converted = await items.ToBaseAsync(itemId, line.UomId, line.Outstanding, cancellationToken);
            total += converted.IsSuccess ? converted.Value.Quantity : 0m;
        }

        return total;
    }

    /// <summary>Releases every active reservation whose expiry date has passed; returns how many.</summary>
    public async Task<int> ExpireAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var expired = await db.Reservations.Where(r => r.Status == "active" && r.ExpiresOn != null && r.ExpiresOn < today).Select(static r => r.Id).ToListAsync(cancellationToken);
        foreach (var id in expired)
        {
            var released = await ReleaseAsync(id, "expired", cancellationToken);
            if (released.IsFailure)
            {
                throw new InvalidOperationException($"Releasing expired reservation {id} failed: {released.Error!.Code}");
            }
        }

        return expired.Count;
    }

    private static async Task<LockedBalance> LockAsync(IUnitOfWork uow, ReservationRequest request, CancellationToken cancellationToken)
    {
        var parameters = Keys(uow, request, 0m);
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app.inv_stock_balances (tenant_id, company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id)
            VALUES (@tenant, @company, @item, @variant, @warehouse, @bin, @lot, @serial)
            ON CONFLICT DO NOTHING
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        return await uow.Connection.QuerySingleAsync<LockedBalance>(new CommandDefinition("""
            SELECT on_hand AS OnHand, reserved AS Reserved, quality_hold AS QualityHold
            FROM app.inv_stock_balances
            WHERE tenant_id = @tenant AND company_id = @company AND item_id = @item AND variant_id = @variant AND warehouse_id = @warehouse AND bin_id = @bin AND lot_id = @lot AND serial_id = @serial
            FOR UPDATE
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
    }

    private static object Keys(IUnitOfWork uow, ReservationRequest request, decimal quantity) => new
    {
        quantity,
        tenant = uow.Context.TenantId.Value,
        company = request.CompanyId,
        item = request.ItemId,
        variant = request.VariantId ?? Guid.Empty,
        warehouse = request.WarehouseId,
        bin = request.BinId ?? Guid.Empty,
        lot = request.LotId ?? Guid.Empty,
        serial = request.SerialId ?? Guid.Empty,
    };

    internal static ReservationInfo Map(Reservation r) =>
        new(r.Id, r.CompanyId, r.ItemId, r.VariantId, r.WarehouseId, r.BinId, r.LotId, r.SerialId, ItemUomMath.Normalize(r.Quantity), ItemUomMath.Normalize(r.ConsumedQuantity), r.SourceDocumentType, r.SourceDocumentId, r.SourceLineId, r.Status, r.ExpiresOn, r.Reason, r.CreatedAt, r.ClosedAt);
}

public sealed record ReservationExpiryPayload(DateOnly? AsOf = null);

/// <summary>Daily platform job: releases expired reservations in every active tenant, each in its own unit of work.</summary>
public sealed class ReservationExpiryJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, ReservationService reservations, IClock clock) : IJobHandler<ReservationExpiryPayload>
{
    public static string JobType => "inventory.reservations.expire";

    public async Task<object?> ExecuteAsync(ReservationExpiryPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var today = payload?.AsOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        if (context.TenantId is not null)
        {
            return new { released = await reservations.ExpireAsync(today, cancellationToken) };
        }

        var released = 0;
        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            released += await InTenantAsync(tenant.Id.Value, (sp, ct) => sp.GetRequiredService<ReservationService>().ExpireAsync(today, ct), cancellationToken);
        }

        return new { tenants = tenants.Count, released };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

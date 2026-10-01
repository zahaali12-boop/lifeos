using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;
using Quicker.Pricing.Contracts;
using Quicker.Sales.Domain;
using Quicker.Sales.Persistence;
using Quicker.Tax.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// Sales orders (roadmap 5.4b, DOMAIN_MODEL §11): created directly or converted from an accepted quotation (its
/// frozen lines carried over, unpriced again), confirmed with stock reserved through <see cref="IStockReservations"/>
/// (a line short of stock stays backordered rather than blocking the order) and the customer's credit exposure
/// checked. An exposure over the limit routes through the generic workflow block/override mechanism exactly as
/// pur_invoices already do for match variance (ADR-0020, A-152) -- hard scenario 6. Confirming two orders of the same
/// customer at once is serialized by an advisory transaction lock on (company, partner), so exposure is always read
/// against every order truly committed first: it can never double-pass the limit (roadmap 5.4 acceptance).
/// </summary>
public sealed class OrderService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    ICustomerDirectory customers,
    IPricing pricing,
    ITaxDetermination tax,
    ITaxDirectory taxDirectory,
    IStockReservations reservations,
    IWorkflowEngine workflow,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IUnitOfWorkAccessor unitOfWork,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    IDimensionSets dimensionSets)
{
    public const string DocumentType = "sales_order";
    public const string CreditLimitBlockKind = "credit_limit";

    public async Task<IReadOnlyList<OrderSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Orders.Include(static o => o.Lines).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(o => o.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(o => o.Status == status);
        }

        if (partnerId is { } p)
        {
            query = query.Where(o => o.PartnerId == p);
        }

        var orders = await query.OrderByDescending(static o => o.CreatedAt).ToListAsync(cancellationToken);
        return await MapManyAsync(orders, cancellationToken);
    }

    public async Task<OrderSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? null : (await MapManyAsync([order], cancellationToken))[0];
    }

    public async Task<Result<OrderSummary>> CreateAsync(SaveOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var order = new SalesOrder { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, BranchId = request.BranchId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyDirectAsync(order, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var numbered = await AllocateNumberAsync(order, company, cancellationToken);
        if (numbered.IsFailure)
        {
            return numbered.Error!;
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.Created, After: new { order.Status, order.TotalGross, order.Currency }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    public async Task<Result<OrderSummary>> ConvertAsync(Guid quotationId, ConvertQuotationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quotation = await db.Quotations.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == quotationId, cancellationToken);
        if (quotation is null)
        {
            return Error.NotFound("quotation", quotationId);
        }

        if (quotation.Status != "accepted")
        {
            return Error.Conflict("quotation.not_accepted", "Only an accepted quotation is converted to an order.").WithWhy(("status", quotation.Status));
        }

        var company = await companies.FindAsync(new CompanyId(quotation.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", quotation.CompanyId);
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value)
        {
            return Error.Validation("order.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        var order = new SalesOrder
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            BranchId = quotation.BranchId,
            PartnerId = quotation.PartnerId,
            QuotationId = quotation.Id,
            Currency = quotation.Currency,
            OrderDate = request.OrderDate ?? clock.TodayIn(company.TimeZone),
            PricingDate = quotation.PricingDate,
            PriceListId = quotation.PriceListId,
            WarehouseId = warehouse.Id,
            CustomerSnapshot = quotation.CustomerSnapshot,
            Notes = quotation.Notes,
            CustomFields = quotation.CustomFields,
            TotalNet = quotation.TotalNet,
            TotalTax = quotation.TotalTax,
            TotalGross = quotation.TotalGross,
            CreatedBy = principal.Principal?.UserId.Value,
            CreatedAt = clock.UtcNow,
        };

        var lineNo = 0;
        foreach (var line in quotation.Lines.OrderBy(static l => l.LineNo))
        {
            lineNo++;
            order.Lines.Add(new SalesOrderLine
            {
                Id = Guid.CreateVersion7(),
                LineNo = lineNo,
                ItemId = line.ItemId,
                VariantId = line.VariantId,
                Description = line.Description,
                Quantity = line.Quantity,
                UomId = line.UomId,
                QuantityBase = line.QuantityBase,
                UnitPrice = line.UnitPrice,
                DiscountPct = line.DiscountPct,
                DiscountAmount = line.DiscountAmount,
                TaxCodeId = line.TaxCodeId,
                TaxRatePct = line.TaxRatePct,
                TaxReverseCharge = line.TaxReverseCharge,
                TaxRecoverable = line.TaxRecoverable,
                TaxReason = line.TaxReason,
                NetAmount = line.NetAmount,
                TaxAmount = line.TaxAmount,
                PromotionId = line.PromotionId,
                PriceBreakdown = line.PriceBreakdown,
                DimensionSetId = line.DimensionSetId,
            });
        }

        var numbered = await AllocateNumberAsync(order, company, cancellationToken);
        if (numbered.IsFailure)
        {
            return numbered.Error!;
        }

        quotation.Status = "converted";
        quotation.OrderId = order.Id;
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.Created, After: new { order.Status, order.TotalGross, order.Currency, fromQuotation = quotation.Number }, CompanyId: order.CompanyId), cancellationToken);
        await audit.RecordAsync(new AuditEntry(QuotationService.DocumentType, quotation.Id, quotation.Number, AuditActions.StateChanged, After: new { quotation.Status, orderId = order.Id }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    /// <summary>
    /// Confirms a draft or credit-held order: reserves what stock is available per line (a short line stays
    /// backordered), and checks the customer's credit exposure once, under an advisory lock on (company, partner)
    /// held for the rest of this transaction, so two concurrent confirmations for the same customer are serialized
    /// and can never both pass an exposure only one of them should (roadmap 5.4 acceptance, hard scenario 6).
    /// </summary>
    public async Task<Result<OrderSummary>> ConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (order.Status is not ("draft" or "on_hold"))
        {
            return Error.Conflict("order.not_confirmable", "Only a draft or credit-held order is confirmed.").WithWhy(("status", order.Status));
        }

        var customer = await customers.EnsureCustomerAsync(order.CompanyId, order.PartnerId, CustomerPurposes.Order, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        if (customer.Value.CreditLimit is { } limit)
        {
            var uow = unitOfWork.Current;
            await uow.Connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtext(@key))",
                new { key = $"sales_credit:{uow.Context.TenantId.Value:N}:{order.CompanyId:N}:{order.PartnerId:N}" },
                uow.Transaction,
                cancellationToken: cancellationToken));

            // open_ar contributes nothing until Receivables (5.7) exists; open_ar_plus_orders is what is actually
            // computable today (A-152), so a customer on the open_ar basis alone never blocks yet -- an honest gap.
            var confirmedElsewhere = await db.Orders.AsNoTracking()
                .Where(o => o.CompanyId == order.CompanyId && o.PartnerId == order.PartnerId && o.Status == "confirmed" && o.Currency == order.Currency && o.Id != order.Id)
                .SumAsync(static o => (decimal?)o.TotalGross, cancellationToken) ?? 0m;
            var exposure = confirmedElsewhere + order.TotalGross;
            if (exposure > limit)
            {
                var overrideId = await workflow.ConsumeOverrideAsync(CreditLimitBlockKind, DocumentType, order.Id, cancellationToken);
                if (overrideId is null)
                {
                    var why = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["exposure"] = exposure,
                        ["limit"] = limit,
                        ["basis"] = customer.Value.CreditExposureBasis,
                        ["currency"] = order.Currency,
                    };
                    var block = await workflow.RaiseBlockAsync(new BlockRequest(CreditLimitBlockKind, DocumentType, order.Id, order.CompanyId, Display(order), why, principal.Principal?.MembershipId.Value), cancellationToken);
                    if (block.IsFailure)
                    {
                        return block.Error!;
                    }

                    order.Status = "on_hold";
                    order.BlockKind = CreditLimitBlockKind;
                    order.BlockId = block.Value.BlockId;
                    order.BlockReason = block.Value.Status == BlockStatuses.Pending ? "An override has been requested." : "No approval routes this block; an authorized approver overrides it.";
                    order.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { order.Status, order.BlockKind, blockStatus = block.Value.Status, why }, CompanyId: order.CompanyId), cancellationToken);
                    return (await MapManyAsync([order], cancellationToken))[0];
                }

                order.OverrideId = overrideId;
            }
        }

        order.BlockKind = null;
        order.BlockId = null;
        order.BlockReason = null;

        foreach (var line in order.Lines.Where(static l => l.Status != "cancelled"))
        {
            if (line.DropShip)
            {
                // The supplier ships straight to the customer: nothing to reserve from the company's own warehouse.
                line.Status = "open";
                continue;
            }

            var reserved = await ReserveLineAsync(order, line, cancellationToken);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            line.QtyReserved += reserved.Value;
            var remaining = line.QuantityBase - line.QtyCancelled - line.QtyShipped - line.QtyReserved;
            line.Status = remaining > 0m ? "backordered" : "open";
        }

        order.Status = "confirmed";
        order.ConfirmedAt = clock.UtcNow;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { order.Status, order.TotalGross }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    /// <summary>Re-attempts reservation for every backordered line of a confirmed order (stock may have arrived since).</summary>
    public async Task<Result<OrderSummary>> RetryBackordersAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (order.Status != "confirmed")
        {
            return Error.Conflict("order.not_confirmed", "Only a confirmed order retries its backorders.").WithWhy(("status", order.Status));
        }

        foreach (var line in order.Lines.Where(static l => l.Status == "backordered"))
        {
            var reserved = await ReserveLineAsync(order, line, cancellationToken);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            if (reserved.Value <= 0m)
            {
                continue;
            }

            line.QtyReserved += reserved.Value;
            var remaining = line.QuantityBase - line.QtyCancelled - line.QtyShipped - line.QtyReserved;
            line.Status = remaining > 0m ? "backordered" : "open";
        }

        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { retriedBackorders = true }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    public async Task<Result<OrderSummary>> CancelAsync(Guid id, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (order.Status == "cancelled")
        {
            return Error.Conflict("order.already_cancelled", "The order is already cancelled.");
        }

        var reason = Validation.Text(request.Reason);
        if (reason is null)
        {
            return Error.Validation("order.reason_required", "Cancelling an order needs a reason.");
        }

        foreach (var active in (await reservations.ForDocumentAsync(DocumentType, order.Id, cancellationToken)).Where(static r => r.Status == "active"))
        {
            var released = await reservations.ReleaseAsync(active.Id, "Order cancelled", cancellationToken);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        foreach (var line in order.Lines)
        {
            line.QtyCancelled = line.QuantityBase - line.QtyShipped;
            line.QtyReserved = 0m;
            line.Status = "cancelled";
        }

        order.Status = "cancelled";
        order.CancelledAt = clock.UtcNow;
        order.CancelReason = reason;
        order.BlockKind = null;
        order.BlockId = null;
        order.BlockReason = null;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { order.Status, reason }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    /// <summary>Cancels part of a line's remaining, unshipped quantity: any of its reservation is released and, once
    /// confirmed, a fresh reservation is attempted for what still remains.</summary>
    public async Task<Result<OrderSummary>> CancelLineAsync(Guid id, Guid lineId, CancelOrderLineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        var line = order.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("order line", lineId);
        }

        if (order.Status == "cancelled" || line.Status == "cancelled")
        {
            return Error.Conflict("order.line_already_cancelled", "The line is already cancelled.");
        }

        if (request.Quantity <= 0m)
        {
            return Error.Validation("order.cancel_quantity_invalid", "Cancel a positive quantity.");
        }

        var toBase = await items.ToBaseAsync(line.ItemId, line.UomId, request.Quantity, cancellationToken);
        if (toBase.IsFailure)
        {
            return toBase.Error!;
        }

        var cancelBase = toBase.Value.Quantity;
        var remainingBefore = line.QuantityBase - line.QtyCancelled - line.QtyShipped;
        if (cancelBase > remainingBefore)
        {
            return Error.Conflict("order.cancel_exceeds_remaining", "Cannot cancel more than remains open on the line.").WithWhy(("remaining", remainingBefore), ("requested", cancelBase));
        }

        foreach (var active in (await reservations.ForDocumentAsync(DocumentType, order.Id, cancellationToken)).Where(r => r.SourceLineId == line.Id && r.Status == "active"))
        {
            var released = await reservations.ReleaseAsync(active.Id, "Line partially cancelled", cancellationToken);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        line.QtyReserved = 0m;
        line.QtyCancelled += cancelBase;
        var remainingAfter = line.QuantityBase - line.QtyCancelled - line.QtyShipped;
        if (remainingAfter <= 0m)
        {
            line.Status = "cancelled";
        }
        else if (order.Status == "confirmed" && !line.DropShip)
        {
            var reserved = await ReserveLineAsync(order, line, cancellationToken);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            line.QtyReserved = reserved.Value;
            line.Status = line.QtyReserved >= remainingAfter ? "open" : "backordered";
        }
        else
        {
            line.Status = "open";
        }

        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { lineId, cancelledQuantity = request.Quantity }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    /// <summary>Records the purchase order line raised for a drop-ship line, once, so the order shows what will
    /// fulfil it (DOMAIN_MODEL §11); creating that purchase order itself is a web-layer "next step" (5.4c), the same
    /// pattern Purchasing's own document flow already uses to pre-fill one document's screen from another's.</summary>
    public async Task<Result<OrderSummary>> LinkPurchaseOrderLineAsync(Guid id, Guid lineId, LinkPurchaseOrderLineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        var line = order.Lines.SingleOrDefault(l => l.Id == lineId);
        if (line is null)
        {
            return Error.NotFound("order line", lineId);
        }

        if (!line.DropShip)
        {
            return Error.Conflict("order.line_not_drop_ship", "Only a drop-ship line takes a purchase order line.");
        }

        if (line.PurchaseOrderLineId is not null)
        {
            return Error.Conflict("order.line_already_linked", "The line already names a purchase order line.").WithWhy(("purchaseOrderLineId", line.PurchaseOrderLineId));
        }

        line.PurchaseOrderLineId = request.PurchaseOrderLineId;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.Updated, After: new { lineId, purchaseOrderLineId = request.PurchaseOrderLineId }, CompanyId: order.CompanyId), cancellationToken);
        return (await MapManyAsync([order], cancellationToken))[0];
    }

    // ------------------------------------------------------------------ IWorkflowSubjectProvider support

    public async Task<SalesOrder?> LoadAsync(Guid id, CancellationToken cancellationToken) => await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<WorkflowSubject> SubjectAsync(SalesOrder order, CancellationToken cancellationToken)
    {
        var partner = await customers.FindCustomerAsync(order.CompanyId, order.PartnerId, cancellationToken);
        return new WorkflowSubject(DocumentType, order.Id, order.CompanyId, Display(order), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = order.TotalGross,
            ["currency"] = order.Currency,
            ["customerCode"] = partner?.PartnerCode,
            ["blockKind"] = order.BlockKind,
            ["lineCount"] = order.Lines.Count,
        });
    }

    /// <summary>
    /// A decision on the credit-limit block just records whether an override was granted: the order stays on_hold
    /// until it is confirmed again and consumes the override (mirrors Purchasing's InvoiceService.DecideAsync).
    /// </summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == decision.EntityId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(DocumentType, decision.EntityId);
        }

        var granted = decision.Status == WorkflowDecisions.Approved;
        order.BlockReason = granted ? "An override was granted; confirm the order again." : $"The override was refused{(decision.Comment is null ? "." : ": " + decision.Comment)}";
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, granted ? AuditActions.Override : AuditActions.StateChanged, After: new { order.Status, order.BlockKind, overrideId = decision.OverrideId, granted }, Reason: decision.Comment, CompanyId: order.CompanyId), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result<decimal>> ReserveLineAsync(SalesOrder order, SalesOrderLine line, CancellationToken cancellationToken)
    {
        var remaining = line.QuantityBase - line.QtyCancelled - line.QtyShipped - line.QtyReserved;
        if (remaining <= 0m)
        {
            return 0m;
        }

        var item = await items.FindAsync(line.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", line.ItemId);
        }

        var warehouseId = line.WarehouseId ?? order.WarehouseId;
        var full = await reservations.ReserveAsync(new ReservationRequest(order.CompanyId, line.ItemId, remaining, warehouseId, DocumentType, order.Id, line.Id, item.BaseUomId, line.VariantId), cancellationToken);
        if (full.IsSuccess)
        {
            return remaining;
        }

        if (full.Error!.Code != "stock.insufficient_to_reserve")
        {
            return full.Error!;
        }

        var availability = await reservations.AvailabilityAsync(order.CompanyId, line.ItemId, warehouseId, line.VariantId, cancellationToken);
        if (availability.Available <= 0m)
        {
            return 0m;
        }

        var toReserve = Math.Min(remaining, availability.Available);
        var partial = await reservations.ReserveAsync(new ReservationRequest(order.CompanyId, line.ItemId, toReserve, warehouseId, DocumentType, order.Id, line.Id, item.BaseUomId, line.VariantId), cancellationToken);
        return partial.IsSuccess ? toReserve : 0m;
    }

    private async Task<Result> AllocateNumberAsync(SalesOrder order, CompanyInfo company, CancellationToken cancellationToken)
    {
        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "SO-" + company.Code, "SO-{yyyy}-{seq:5}", "yearly", cancellationToken);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, order.OrderDate, order.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        order.Number = number.Value.Text;
        return Result.Success();
    }

    private async Task<Result> ApplyDirectAsync(SalesOrder order, SaveOrderRequest request, CompanyInfo company, CancellationToken cancellationToken)
    {
        var customer = await customers.EnsureCustomerAsync(company.Id.Value, request.PartnerId, CustomerPurposes.Order, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value)
        {
            return Error.Validation("order.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, customer.Value.Currency, "order", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var orderDate = request.OrderDate ?? clock.TodayIn(company.TimeZone);
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("order.lines_required", "An order has at least one line.");
        }

        var validated = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var pricingDate = request.PricingDate ?? orderDate;
        var pricingLines = new List<PricingLineRequest>();
        var resolvedItems = new List<(Guid ItemId, Guid? VariantId, string? Description, Guid? TaxCodeId, Guid? WarehouseId, bool DropShip)>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            lineNo++;
            var resolved = await Shared.ResolveLineAsync(items, "order", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!.WithWhy(("lineNo", lineNo));
            }

            var (item, unit, _) = resolved.Value;
            if (line.VariantId is { } variantId)
            {
                var variant = await items.FindVariantAsync(variantId, cancellationToken);
                if (variant is null || variant.ItemId != item.Id)
                {
                    return Error.Validation("order.variant_invalid", "The variant must belong to the item.").WithWhy(("lineNo", lineNo));
                }
            }

            if (line.DiscountPct is < 0m or > 100m)
            {
                return Error.Validation("order.discount_invalid", "A discount is between 0 and 100 percent.").WithWhy(("lineNo", lineNo));
            }

            if (line.WarehouseId is { } lineWarehouseId)
            {
                var lineWarehouse = await warehouses.FindAsync(lineWarehouseId, cancellationToken);
                if (lineWarehouse is null || lineWarehouse.CompanyId != company.Id.Value)
                {
                    return Error.Validation("order.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("lineNo", lineNo));
                }
            }

            var key = lineNo.ToString(CultureInfo.InvariantCulture);
            pricingLines.Add(new PricingLineRequest(key, item.Id, line.VariantId, unit.UomId, line.Quantity, line.UnitPrice, line.DiscountPct > 0m ? line.DiscountPct : null));
            resolvedItems.Add((item.Id, line.VariantId, Shared.Trim(line.Description), line.TaxCodeId, line.WarehouseId, line.DropShip));
        }

        var priced = await pricing.PriceAsync(new PricingRequest(company.Id.Value, request.PartnerId, currency.Value.Code, pricingDate, pricingLines, request.PriceListId), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        if (priced.Value.UnpricedLines > 0)
        {
            var problem = priced.Value.Lines.First(static l => l.Problem is not null).Problem!;
            return Error.Validation(problem.Code, problem.Message).WithWhy(("unpricedLines", priced.Value.UnpricedLines));
        }

        var pricedByKey = priced.Value.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        var taxLines = new List<TaxDocumentLine>();
        for (var i = 0; i < resolvedItems.Count; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var p = pricedByKey[key];
            taxLines.Add(new TaxDocumentLine(key, p.NetAmount, resolvedItems[i].ItemId, TaxCodeId: resolvedItems[i].TaxCodeId));
        }

        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(company.Id.Value, TaxDirections.Sales, pricingDate, currency.Value.Code, priced.Value.PricesIncludeTax ?? false, taxLines, request.PartnerId), cancellationToken);
        if (taxed.IsFailure)
        {
            return taxed.Error!;
        }

        var taxedByKey = taxed.Value.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);

        order.PartnerId = request.PartnerId;
        order.Currency = currency.Value.Code;
        order.OrderDate = orderDate;
        order.PricingDate = pricingDate;
        order.PriceListId = request.PriceListId;
        order.WarehouseId = warehouse.Id;
        order.CustomerSnapshot = JsonSerializer.Serialize(new { code = customer.Value.PartnerCode, name = customer.Value.PartnerName.Values });
        order.Notes = Shared.Trim(request.Notes);
        order.CustomFields = validated.Value;

        // The cost centre and other dimensions of each line, resolved before anything changes.
        var dimensionSetIds = new List<Guid?>(request.Lines.Count);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var dimensionSet = await Shared.DimensionSetAsync(dimensionSets, request.Lines[i].Dimensions, i + 1, cancellationToken);
            if (dimensionSet.IsFailure)
            {
                return dimensionSet.Error!;
            }

            dimensionSetIds.Add(dimensionSet.Value);
        }

        order.Lines.Clear();
        var totalNet = 0m;
        var totalTax = 0m;
        for (var i = 0; i < resolvedItems.Count; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var p = pricedByKey[key];
            var t = taxedByKey[key];
            var (itemId, variantId, description, taxCodeId, lineWarehouseId, dropShip) = resolvedItems[i];
            var line = request.Lines[i];
            var net = Shared.Round(p.NetAmount, currency.Value);
            var taxAmount = Shared.Round(t.Tax, currency.Value);
            totalNet += net;
            totalTax += taxAmount;
            order.Lines.Add(new SalesOrderLine
            {
                Id = Guid.CreateVersion7(),
                LineNo = i + 1,
                ItemId = itemId,
                VariantId = variantId,
                Description = description,
                Quantity = line.Quantity,
                UomId = p.UomId,
                QuantityBase = p.BaseQuantity,
                UnitPrice = p.UnitPrice,
                DiscountPct = line.DiscountPct,
                DiscountAmount = p.LineDiscountAmount + p.PromotionDiscountAmount + p.DocumentDiscountAmount,
                TaxCodeId = t.TaxCodeId ?? taxCodeId,
                TaxRatePct = t.RatePct,
                TaxReverseCharge = t.IsReverseCharge,
                TaxRecoverable = t.IsRecoverable,
                TaxReason = taxed.Value.Determinations.SingleOrDefault(d => d.Key == key)?.Reason,
                NetAmount = net,
                TaxAmount = taxAmount,
                PromotionId = p.PromotionId,
                PriceBreakdown = JsonSerializer.Serialize(p.Steps),
                WarehouseId = lineWarehouseId,
                DropShip = dropShip,
                DimensionSetId = dimensionSetIds[i],
            });
        }

        order.TotalNet = totalNet;
        order.TotalTax = totalTax;
        order.TotalGross = totalNet + totalTax;
        return Result.Success();
    }

    private static string Display(SalesOrder order) => $"{order.Number} · {order.TotalGross:0.##} {order.Currency}";

    private async Task<IReadOnlyList<OrderSummary>> MapManyAsync(List<SalesOrder> orders, CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var partnerKeys = orders.Select(o => (o.PartnerId, o.CompanyId)).Distinct().ToList();
        var partnersByKey = new Dictionary<(Guid PartnerId, Guid CompanyId), CustomerTermsInfo>();
        foreach (var (partnerId, companyId) in partnerKeys)
        {
            var found = await customers.FindCustomerAsync(companyId, partnerId, cancellationToken);
            if (found is not null)
            {
                partnersByKey[(partnerId, companyId)] = found;
            }
        }

        var itemIds = orders.SelectMany(static o => o.Lines).Select(static l => l.ItemId).Distinct().ToList();
        var itemsById = new Dictionary<Guid, ItemInfo>();
        var uomsByItem = new Dictionary<Guid, IReadOnlyList<ItemUomInfo>>();
        foreach (var itemId in itemIds)
        {
            var item = await items.FindAsync(itemId, cancellationToken);
            if (item is not null)
            {
                itemsById[itemId] = item;
                uomsByItem[itemId] = await items.UomsAsync(itemId, cancellationToken);
            }
        }

        var taxCodeIds = orders.SelectMany(static o => o.Lines).Select(static l => l.TaxCodeId).Where(static id => id is not null).Select(static id => id!.Value).Distinct().ToList();
        var taxCodes = taxCodeIds.Count == 0 ? new Dictionary<Guid, TaxCodeRef>() : await taxDirectory.DescribeCodesAsync(taxCodeIds, cancellationToken);

        var result = new List<OrderSummary>(orders.Count);
        foreach (var order in orders)
        {
            partnersByKey.TryGetValue((order.PartnerId, order.CompanyId), out var partner);
            var lines = new List<OrderLineSummary>(order.Lines.Count);
            foreach (var line in order.Lines.OrderBy(static l => l.LineNo))
            {
                itemsById.TryGetValue(line.ItemId, out var item);
                var uom = uomsByItem.TryGetValue(line.ItemId, out var units) ? units.FirstOrDefault(u => u.UomId == line.UomId) : null;
                string? taxCode = null;
                if (line.TaxCodeId is { } tid && taxCodes.TryGetValue(tid, out var tref))
                {
                    taxCode = tref.Code;
                }

                lines.Add(new OrderLineSummary(
                    line.Id,
                    line.LineNo,
                    line.ItemId,
                    item?.Code ?? string.Empty,
                    item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    line.VariantId,
                    line.Description,
                    line.Quantity,
                    line.UomId,
                    uom?.UomCode ?? string.Empty,
                    line.QuantityBase,
                    line.UnitPrice,
                    line.DiscountPct,
                    line.NetAmount,
                    line.TaxAmount,
                    line.PromotionId,
                    null,
                    line.WarehouseId,
                    line.QtyReserved,
                    line.QtyShipped,
                    line.QtyInvoiced,
                    line.QtyCancelled,
                    line.Status,
                    line.DropShip,
                    line.PurchaseOrderLineId,
                    line.TaxCodeId,
                    taxCode,
                    line.TaxRatePct,
                    line.TaxReverseCharge,
                    line.TaxRecoverable,
                    line.TaxReason,
                    line.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null));
            }

            result.Add(new OrderSummary(
                order.Id,
                order.CompanyId,
                order.Number,
                order.Status,
                order.PartnerId,
                partner?.PartnerCode ?? string.Empty,
                partner?.PartnerName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                order.QuotationId,
                order.Currency,
                order.OrderDate,
                order.PricingDate,
                order.PriceListId,
                order.WarehouseId,
                order.TotalNet,
                order.TotalTax,
                order.TotalGross,
                order.BlockKind,
                order.BlockReason,
                order.Notes,
                Shared.Parse(order.CustomFields),
                order.ConfirmedAt,
                order.CancelledAt,
                order.CancelReason,
                lines,
                order.UpdatedAt));
        }

        return result;
    }
}

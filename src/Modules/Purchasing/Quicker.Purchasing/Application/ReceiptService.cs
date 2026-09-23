using System.Text.Json;
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
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Goods receipts (roadmap 4.3, POSTING_RULES "Goods receipt"): a draft against one order's open lines, checked
/// against the supplier's over-receipt tolerance, posted through the stock engine at the expected cost (the order
/// price after its discount, converted at the receipt-date rate) with GRNI as the offset and the receipt as the
/// subledger item, so the GRNI balance is always the uninvoiced receipt value; reversed as a whole through the
/// stock engine's return applied to the original entries. Lots, serials and bins pass through to the engine, which
/// enforces the item's tracking.
/// </summary>
public sealed class ReceiptService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IPartnerDirectory partners,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IInventoryPosting inventory,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock) : IPurchaseReceiptDirectory
{
    public const string DocumentType = PurchaseDocumentTypes.Receipt;

    private static readonly string[] ReceivableOrderStatuses = ["approved", "sent", "partially_received"];

    private static readonly string[] ReceivableLineStatuses = ["open", "partially_received"];

    public async Task<IReadOnlyList<ReceiptSummary>> ListAsync(Guid? companyId, string? status, Guid? orderId, CancellationToken cancellationToken)
    {
        var query = db.Receipts.AsNoTracking().Include(static r => r.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (orderId is { } o)
        {
            query = query.Where(r => r.OrderId == o);
        }

        var receipts = await query.OrderByDescending(static r => r.PostingDate).ThenByDescending(static r => r.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<ReceiptSummary>(receipts.Count);
        foreach (var receipt in receipts)
        {
            result.Add(await MapAsync(receipt, cancellationToken));
        }

        return result;
    }

    /// <summary>The open lines of the company's receivable orders (approved, sent or partly received), with what the tolerance still allows.</summary>
    public async Task<IReadOnlyList<ReceivableLine>> ReceivableAsync(Guid companyId, Guid? orderId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = from l in db.OrderLines.AsNoTracking()
                    join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                    where o.CompanyId == companyId && ReceivableOrderStatuses.Contains(o.Status) && ReceivableLineStatuses.Contains(l.Status)
                    select new { Line = l, Order = o };
        if (orderId is { } id)
        {
            query = query.Where(x => x.Order.Id == id);
        }

        if (partnerId is { } p)
        {
            query = query.Where(x => x.Order.PartnerId == p);
        }

        var rows = await query.OrderBy(static x => x.Order.Number).ThenBy(static x => x.Line.LineNo).ToListAsync(cancellationToken);
        var tolerances = new Dictionary<Guid, decimal>();
        var result = new List<ReceivableLine>(rows.Count);
        foreach (var row in rows)
        {
            if (!tolerances.TryGetValue(row.Order.PartnerId, out var tolerance))
            {
                tolerance = (await partners.FindSupplierAsync(companyId, row.Order.PartnerId, cancellationToken))?.QtyTolerancePct ?? 0m;
                tolerances[row.Order.PartnerId] = tolerance;
            }

            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            var uom = (await items.UomsAsync(row.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == row.Line.UomId);
            var ordered = row.Line.Quantity - row.Line.QtyCancelled;
            var max = MaxReceivable(ordered, tolerance);
            result.Add(new ReceivableLine(row.Order.Id, row.Order.Number, row.Line.Id, row.Line.LineNo, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), item?.Tracking ?? "none",
                row.Line.UomId, uom?.UomCode ?? string.Empty, ordered, row.Line.QtyReceived, row.Line.QtyCancelled, Math.Max(0m, ordered - row.Line.QtyReceived), Math.Max(0m, max - row.Line.QtyReceived), row.Line.ExpectedDate ?? row.Order.ExpectedDate, row.Line.WarehouseId ?? row.Order.WarehouseId));
        }

        return result;
    }

    public async Task<Result<ReceiptSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking().Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return receipt is null ? Error.NotFound(DocumentType, id) : await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result<ReceiptSummary>> CreateAsync(SaveReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        if (order is null)
        {
            return Error.Validation("receipt.order_unknown", "The purchase order does not exist.").WithWhy(("orderId", request.OrderId));
        }

        var receipt = new Receipt
        {
            Id = Guid.CreateVersion7(),
            CompanyId = order.CompanyId,
            OrderId = order.Id,
            PartnerId = order.PartnerId,
            Currency = order.Currency,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        var applied = await ApplyAsync(receipt, order, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(order.CompanyId), "GRN", "GRN-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(order.CompanyId), null, receipt.PostingDate, receipt.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        receipt.Number = number.Value.Text;
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Created, After: new { receipt.Number, order = order.Number, lines = receipt.Lines.Count }, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result<ReceiptSummary>> UpdateAsync(Guid id, SaveReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await db.Receipts.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is edited.").WithWhy(("status", receipt.Status));
        }

        if (request.OrderId != receipt.OrderId)
        {
            return Error.Validation("receipt.order_locked", "A receipt stays with the order it was drafted against.").WithWhy(("orderId", receipt.OrderId));
        }

        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleAsync(o => o.Id == receipt.OrderId, cancellationToken);
        db.ReceiptLines.RemoveRange(receipt.Lines);
        receipt.Lines.Clear();
        var applied = await ApplyAsync(receipt, order, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        receipt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is deleted; a posted one is reversed.").WithWhy(("status", receipt.Status));
        }

        db.Receipts.Remove(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Deleted, CompanyId: receipt.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Posts the draft: the lines are re-checked against the order as it stands now, the stock engine books Inventory / GRNI at the expected cost, and the order's received quantities and status follow.</summary>
    public async Task<Result<ReceiptSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is posted.").WithWhy(("status", receipt.Status));
        }

        var order = await db.Orders.Include(static o => o.Lines).SingleAsync(o => o.Id == receipt.OrderId, cancellationToken);
        var request = new SaveReceiptRequest(receipt.OrderId, receipt.Lines.Select(static l => new SaveReceiptLineRequest(l.OrderLineId, l.Quantity, null, l.UomId, l.BinId, l.LotNumber, l.ExpiresOn, Serials(l.SerialNumbers))).ToList(),
            receipt.WarehouseId, receipt.PostingDate, receipt.SupplierDeliveryNote, receipt.Notes, receipt.BranchId, Shared.Parse(receipt.CustomFields));
        var kept = receipt.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
        db.ReceiptLines.RemoveRange(receipt.Lines);
        receipt.Lines.Clear();
        var applied = await ApplyAsync(receipt, order, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        foreach (var line in receipt.Lines)
        {
            if (kept.TryGetValue(line.LineNo, out var lineId))
            {
                line.Id = lineId;
            }
        }

        var stockLines = receipt.Lines.Select(l => new StockLine(l.ItemId, StockEntryTypes.PurchaseReceipt, l.Quantity, receipt.WarehouseId, l.UomId, l.VariantId, l.BinId,
            SourceLineId: l.Id, UnitCost: l.ExpectedUnitCost, CostIsExpected: true, LotNumber: l.LotNumber, ExpiresOn: l.ExpiresOn, SerialNumbers: Serials(l.SerialNumbers), PartnerId: receipt.PartnerId)).ToList();
        var posted = await inventory.PostAsync(new StockPostingRequest(receipt.CompanyId, receipt.PostingDate, DocumentType, receipt.Id, stockLines, IdempotencyKey: $"purchase_receipt:{receipt.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var entries = posted.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.ToList());
        foreach (var line in receipt.Lines)
        {
            if (entries.TryGetValue(line.Id, out var own))
            {
                // Serialised items post one entry per unit; the line's booked cost is their sum and its ledger reference the first.
                line.SleId = own[0].Id;
                line.SleIds = JsonSerializer.Serialize(own.Select(static e => e.Id).ToList(), Shared.Json);
                line.ExpectedCostAmount = own.Sum(static e => e.CostAmount);
            }
        }

        receipt.TotalExpectedCost = receipt.Lines.Sum(static l => l.ExpectedCostAmount);
        receipt.StockPostingId = posted.Value.PostingId;
        receipt.JournalEntryId = posted.Value.JournalEntryId;
        receipt.Status = "posted";
        receipt.PostedAt = clock.UtcNow;
        receipt.PostedBy = principal.Principal?.MembershipId.Value;
        receipt.UpdatedAt = clock.UtcNow;
        ApplyToOrder(order, receipt, +1);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Posted, After: new { status = receipt.Status, order = order.Number, receipt.TotalExpectedCost, journal = posted.Value.JournalNumber, orderStatus = order.Status }, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    /// <summary>Reverses a posted receipt as a whole: a purchase return applied to each original entry takes the stock out at its exact cost and mirrors the GRNI lines; the order's received quantities are given back.</summary>
    public async Task<Result<ReceiptSummary>> ReverseAsync(Guid id, ReverseReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("receipt.reason_required", "A reversal names its reason.");
        }

        var receipt = await db.Receipts.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "posted")
        {
            return Error.Conflict("receipt.not_posted", "Only a posted receipt is reversed.").WithWhy(("status", receipt.Status));
        }

        if (receipt.Lines.Any(static l => l.QtyInvoiced > 0m || l.QtyReturned > 0m))
        {
            return Error.Conflict("receipt.settled", "The receipt has been invoiced or returned in part; reverse those documents first.").WithWhy(("lines", receipt.Lines.Where(static l => l.QtyInvoiced > 0m || l.QtyReturned > 0m).Select(static l => l.LineNo)));
        }

        var company = await companies.FindAsync(new CompanyId(receipt.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < receipt.PostingDate)
        {
            return Error.Validation("receipt.reversal_date_invalid", "A receipt is reversed on or after the day it was posted.").WithWhy(("postingDate", receipt.PostingDate), ("reversalDate", reversalDate));
        }

        var stockLines = receipt.Lines.Select(l => new StockLine(l.ItemId, StockEntryTypes.PurchaseReturn, l.Quantity, receipt.WarehouseId, l.UomId, l.VariantId, l.BinId,
            SourceLineId: l.Id, AppliesToSleId: l.SleId, LotNumber: l.LotNumber, SerialNumbers: Serials(l.SerialNumbers), PartnerId: receipt.PartnerId)).ToList();
        var posted = await inventory.PostAsync(new StockPostingRequest(receipt.CompanyId, reversalDate, DocumentType, receipt.Id, stockLines, IdempotencyKey: $"purchase_receipt_reversal:{receipt.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var order = await db.Orders.Include(static o => o.Lines).SingleAsync(o => o.Id == receipt.OrderId, cancellationToken);
        ApplyToOrder(order, receipt, -1);
        receipt.Status = "reversed";
        receipt.ReversalPostingId = posted.Value.PostingId;
        receipt.ReversalReason = reason;
        receipt.ReversedAt = clock.UtcNow;
        receipt.ReversedBy = principal.Principal?.MembershipId.Value;
        receipt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Reversed, After: new { status = receipt.Status, reversalDate, journal = posted.Value.JournalNumber, orderStatus = order.Status }, Reason: reason, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    // ------------------------------------------------------------------ directory

    public async Task<IReadOnlyList<PurchaseReceiptLineInfo>> OpenLinesAsync(Guid companyId, Guid? partnerId = null, Guid? orderId = null, CancellationToken cancellationToken = default)
    {
        var query = from l in db.ReceiptLines.AsNoTracking()
                    join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                    where r.CompanyId == companyId && r.Status == "posted" && l.QtyInvoiced + l.QtyReturned < l.Quantity
                    select new { Line = l, Receipt = r };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.Receipt.PartnerId == p);
        }

        if (orderId is { } o)
        {
            query = query.Where(x => x.Receipt.OrderId == o);
        }

        var rows = await query.OrderBy(static x => x.Receipt.PostingDate).ThenBy(static x => x.Receipt.Number).ThenBy(static x => x.Line.LineNo).ToListAsync(cancellationToken);
        return rows.Select(static x => Map(x.Line, x.Receipt)).ToList();
    }

    public async Task<Result<PurchaseReceiptLineInfo>> FindLineAsync(Guid receiptLineId, CancellationToken cancellationToken = default)
    {
        var line = await db.ReceiptLines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == receiptLineId, cancellationToken);
        var receipt = line is null ? null : await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == line.ReceiptId, cancellationToken);
        return line is null || receipt is null ? Error.NotFound("purchase_receipt_line", receiptLineId) : Map(line, receipt);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Validates the request against the order as it stands and rebuilds the receipt's header and lines with the expected cost at the posting date.</summary>
    private async Task<Result> ApplyAsync(Receipt receipt, PurchaseOrder order, SaveReceiptRequest request, CancellationToken cancellationToken)
    {
        if (!ReceivableOrderStatuses.Contains(order.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("receipt.order_not_receivable", "Only an approved, sent or partly received order is received against.").WithWhy(("order", order.Number), ("status", order.Status));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("receipt.lines_required", "A receipt has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", order.CompanyId);
        }

        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        var warehouseId = request.WarehouseId ?? order.WarehouseId ?? order.Lines.Select(static l => l.WarehouseId).FirstOrDefault(static w => w is not null);
        if (warehouseId is null)
        {
            return Error.Validation("receipt.warehouse_required", "Name the warehouse the goods arrive in.");
        }

        var warehouse = await warehouses.FindAsync(warehouseId.Value, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != order.CompanyId || !warehouse.IsActive)
        {
            return Error.Validation("receipt.warehouse_invalid", "The warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", warehouseId));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        decimal rate = 1m;
        if (!string.Equals(order.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            var resolved = await rates.ResolveAsync(company.Id, order.Currency, company.FunctionalCurrency.Code, postingDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("receipt.rate_missing", "No spot rate from the order currency to the company's currency on the posting date.").WithWhy(("from", order.Currency), ("to", company.FunctionalCurrency.Code), ("date", postingDate));
            }

            rate = resolved.Value.Rate.Rate;
        }

        var tolerance = (await partners.FindSupplierAsync(order.CompanyId, order.PartnerId, cancellationToken))?.QtyTolerancePct ?? 0m;
        var lines = new List<ReceiptLine>();
        var receivedNow = new Dictionary<Guid, decimal>();
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            var orderLine = order.Lines.SingleOrDefault(x => x.Id == l.OrderLineId);
            if (orderLine is null)
            {
                return Error.Validation("receipt.order_line_unknown", "The order line does not belong to the order.").WithWhy(("lineNo", lineNo), ("orderLineId", l.OrderLineId));
            }

            if (!ReceivableLineStatuses.Contains(orderLine.Status, StringComparer.Ordinal))
            {
                return Error.Conflict("receipt.order_line_closed", "The order line is no longer open for receiving.").WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo), ("status", orderLine.Status));
            }

            var resolved = await Shared.ResolveLineAsync(items, "receipt", orderLine.ItemId, null, l.Quantity, l.Uom, l.UomId ?? orderLine.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!.WithWhy(("lineNo", lineNo));
            }

            var (item, unit, quantityBase) = resolved.Value;
            if (!item.IsStockItem)
            {
                return Error.Validation("receipt.non_stock_line", "Services and expense items are received on the supplier invoice, not on a goods receipt.").WithWhy(("lineNo", lineNo), ("item", item.Code), ("type", item.Type));
            }

            var basePerOrderUnit = orderLine.QuantityBase / orderLine.Quantity;
            var qtyInOrderUom = quantityBase / basePerOrderUnit;
            var ordered = orderLine.Quantity - orderLine.QtyCancelled;
            var alreadyNow = receivedNow.GetValueOrDefault(orderLine.Id);
            var max = MaxReceivable(ordered, tolerance);
            if (orderLine.QtyReceived + alreadyNow + qtyInOrderUom > max)
            {
                return Error.Conflict("receipt.over_receipt", "The receipt exceeds what the order line and the supplier's tolerance allow.")
                    .WithWhy(("lineNo", lineNo), ("orderLineNo", orderLine.LineNo), ("ordered", ordered), ("received", orderLine.QtyReceived + alreadyNow), ("requested", qtyInOrderUom), ("tolerancePct", tolerance), ("maxReceivable", max));
            }

            receivedNow[orderLine.Id] = alreadyNow + qtyInOrderUom;
            var unitPriceNet = orderLine.UnitPrice * (1m - orderLine.DiscountPct / 100m);
            var expectedUnitCost = unitPriceNet / basePerOrderUnit * (quantityBase / l.Quantity) * rate;
            lines.Add(new ReceiptLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = receipt.TenantId,
                ReceiptId = receipt.Id,
                LineNo = lineNo,
                OrderLineId = orderLine.Id,
                ItemId = orderLine.ItemId,
                VariantId = orderLine.VariantId,
                Quantity = l.Quantity,
                UomId = unit.UomId,
                QuantityBase = quantityBase,
                QtyInOrderUom = qtyInOrderUom,
                BinId = l.BinId,
                LotNumber = Shared.Trim(l.LotNumber),
                ExpiresOn = l.ExpiresOn,
                SerialNumbers = JsonSerializer.Serialize(l.SerialNumbers?.Select(static s => s.Trim()).Where(static s => s.Length > 0).ToList() ?? [], Shared.Json),
                UnitPrice = unitPriceNet,
                ExpectedUnitCost = expectedUnitCost,
                ExpectedCostAmount = Shared.Round(expectedUnitCost * l.Quantity, company.FunctionalCurrency),
                CreatedAt = clock.UtcNow,
            });
        }

        receipt.WarehouseId = warehouseId.Value;
        receipt.PostingDate = postingDate;
        receipt.SupplierDeliveryNote = Shared.Trim(request.SupplierDeliveryNote);
        receipt.Notes = Shared.Trim(request.Notes);
        receipt.BranchId = request.BranchId ?? order.BranchId;
        receipt.CustomFields = Shared.JsonOrEmpty(request.CustomFields);
        receipt.ExchangeRate = rate;
        receipt.TotalExpectedCost = lines.Sum(static x => x.ExpectedCostAmount);
        receipt.Lines.AddRange(lines);
        return Result.Success();
    }

    /// <summary>Adds (+1) or gives back (-1) the receipt's quantities on the order lines and sets the line and order statuses.</summary>
    private static void ApplyToOrder(PurchaseOrder order, Receipt receipt, int sign)
    {
        foreach (var group in receipt.Lines.GroupBy(static l => l.OrderLineId))
        {
            var orderLine = order.Lines.Single(l => l.Id == group.Key);
            orderLine.QtyReceived = Math.Max(0m, orderLine.QtyReceived + sign * group.Sum(static l => l.QtyInOrderUom));
            var ordered = orderLine.Quantity - orderLine.QtyCancelled;
            orderLine.Status = orderLine.QtyReceived <= 0m ? "open" : orderLine.QtyReceived >= ordered ? "received" : "partially_received";
        }

        var live = order.Lines.Where(static l => l.Status != "cancelled" && l.Status != "closed").ToList();
        if (live.Count > 0 && live.All(static l => l.Status == "received"))
        {
            order.Status = "received";
        }
        else if (live.Any(static l => l.QtyReceived > 0m))
        {
            order.Status = "partially_received";
        }
        else if (order.Status is "partially_received" or "received")
        {
            order.Status = order.SentAt is null ? "approved" : "sent";
        }
    }

    private static decimal MaxReceivable(decimal ordered, decimal tolerancePct) => ordered * (1m + tolerancePct / 100m);

    private static IReadOnlyList<string> Serials(string json) => JsonSerializer.Deserialize<List<string>>(json, Shared.Json) ?? [];

    private static PurchaseReceiptLineInfo Map(ReceiptLine l, Receipt r) =>
        new(r.Id, r.Number, l.Id, l.LineNo, r.CompanyId, r.PartnerId, r.OrderId, l.OrderLineId, l.ItemId, l.VariantId, l.UomId, l.Quantity, l.QuantityBase, l.QtyInOrderUom, l.QtyInvoiced, l.QtyReturned, l.UnitPrice, r.Currency, r.ExchangeRate, l.ExpectedCostAmount, l.InvoicedCostAmount, l.ReturnedCostAmount, r.PostingDate, r.WarehouseId, l.SleId);

    private async Task<ReceiptSummary> MapAsync(Receipt r, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleAsync(o => o.Id == r.OrderId, cancellationToken);
        var partner = await partners.FindAsync(r.PartnerId, cancellationToken);
        var warehouse = await warehouses.FindAsync(r.WarehouseId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(r.CompanyId), cancellationToken);
        var lines = new List<ReceiptLineSummary>(r.Lines.Count);
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var uom = (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            var orderLine = order.Lines.SingleOrDefault(x => x.Id == l.OrderLineId);
            lines.Add(new ReceiptLineSummary(l.Id, l.LineNo, l.OrderLineId, orderLine?.LineNo ?? 0, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.VariantId, l.Quantity, l.UomId, uom?.UomCode ?? string.Empty, l.QuantityBase, l.QtyInOrderUom,
                l.BinId, l.LotNumber, l.ExpiresOn, Serials(l.SerialNumbers), l.UnitPrice, l.ExpectedUnitCost, l.ExpectedCostAmount, l.QtyInvoiced, l.QtyReturned, l.SleId));
        }

        return new ReceiptSummary(r.Id, r.CompanyId, r.Number, r.Status, r.OrderId, order.Number, r.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), r.WarehouseId, warehouse?.Code, r.PostingDate, r.SupplierDeliveryNote,
            r.Currency, r.ExchangeRate, company?.FunctionalCurrency.Code ?? r.Currency, r.TotalExpectedCost, r.StockPostingId, r.JournalEntryId, r.ReversalReason, r.ReversedAt, r.Notes, Shared.Parse(r.CustomFields), lines, r.PostedAt, r.UpdatedAt);
    }
}

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
/// Supplier returns (roadmap 4.6, POSTING_RULES "Supplier return"): goods of one posted receipt sent back, taken out
/// of stock by the stock engine's purchase return applied to the original entries (exact cost, GRNI as the offset,
/// the return as the GRNI subledger item so the receipt's GRNI position stays exact), the receipt line's returned
/// quantity and value kept for invoices and debit notes. Reversed as a whole while nothing is credited.
/// </summary>
public sealed class ReturnService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IPartnerDirectory partners,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IInventoryPosting inventory,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = PurchaseDocumentTypes.Return;

    public async Task<IReadOnlyList<ReturnSummary>> ListAsync(Guid? companyId, string? status, Guid? receiptId, CancellationToken cancellationToken)
    {
        var query = db.Returns.AsNoTracking().Include(static r => r.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (receiptId is { } rc)
        {
            query = query.Where(r => r.ReceiptId == rc);
        }

        var returns = await query.OrderByDescending(static r => r.PostingDate).ThenByDescending(static r => r.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<ReturnSummary>(returns.Count);
        foreach (var r in returns)
        {
            result.Add(await MapAsync(r, cancellationToken));
        }

        return result;
    }

    /// <summary>Posted receipt lines with a quantity not yet returned, for the company (optionally one receipt or supplier).</summary>
    public async Task<IReadOnlyList<ReturnableLine>> ReturnableAsync(Guid companyId, Guid? receiptId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = from l in db.ReceiptLines.AsNoTracking()
                    join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                    where r.CompanyId == companyId && r.Status == "posted" && l.QtyReturned < l.Quantity
                    select new { Line = l, Receipt = r };
        if (receiptId is { } rc)
        {
            query = query.Where(x => x.Receipt.Id == rc);
        }

        if (partnerId is { } p)
        {
            query = query.Where(x => x.Receipt.PartnerId == p);
        }

        var rows = await query.OrderByDescending(static x => x.Receipt.PostingDate).ThenBy(static x => x.Receipt.Number).ThenBy(static x => x.Line.LineNo).Take(500).ToListAsync(cancellationToken);
        var result = new List<ReturnableLine>(rows.Count);
        foreach (var row in rows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(row.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == row.Line.UomId);
            var partner = await partners.FindAsync(row.Receipt.PartnerId, cancellationToken);
            result.Add(new ReturnableLine(row.Receipt.Id, row.Receipt.Number, row.Line.Id, row.Line.LineNo, row.Receipt.PartnerId, partner?.Code ?? string.Empty, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), item?.Tracking ?? "none",
                row.Line.UomId, uom?.UomCode ?? string.Empty, row.Line.Quantity, row.Line.QtyReturned, row.Line.Quantity - row.Line.QtyReturned, row.Line.LotNumber, Serials(row.Line.SerialNumbers), row.Receipt.PostingDate, row.Receipt.WarehouseId));
        }

        return result;
    }

    public async Task<Result<ReturnSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var r = await db.Returns.AsNoTracking().Include(static x => x.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return r is null ? Error.NotFound(DocumentType, id) : await MapAsync(r, cancellationToken);
    }

    public async Task<Result<ReturnSummary>> CreateAsync(SaveReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await db.Receipts.AsNoTracking().Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == request.ReceiptId, cancellationToken);
        if (receipt is null)
        {
            return Error.Validation("return.receipt_unknown", "The receipt does not exist.").WithWhy(("receiptId", request.ReceiptId));
        }

        var ret = new SupplierReturn { Id = Guid.CreateVersion7(), CompanyId = receipt.CompanyId, ReceiptId = receipt.Id, PartnerId = receipt.PartnerId, WarehouseId = receipt.WarehouseId, Currency = receipt.Currency, BranchId = receipt.BranchId, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(ret, receipt, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(receipt.CompanyId), "RTN", "RTN-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(receipt.CompanyId), null, ret.PostingDate, ret.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        ret.Number = number.Value.Text;
        db.Returns.Add(ret);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Created, After: new { ret.Number, receipt = receipt.Number, lines = ret.Lines.Count }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result<ReturnSummary>> UpdateAsync(Guid id, SaveReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ret = await db.Returns.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "draft")
        {
            return Error.Conflict("return.not_draft", "Only a draft return is edited.").WithWhy(("status", ret.Status));
        }

        if (request.ReceiptId != ret.ReceiptId)
        {
            return Error.Validation("return.receipt_locked", "A return stays with the receipt it was drafted against.");
        }

        var receipt = await db.Receipts.AsNoTracking().Include(static r => r.Lines).SingleAsync(r => r.Id == ret.ReceiptId, cancellationToken);
        db.ReturnLines.RemoveRange(ret.Lines);
        ret.Lines.Clear();
        var applied = await ApplyAsync(ret, receipt, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var ret = await db.Returns.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "draft")
        {
            return Error.Conflict("return.not_draft", "Only a draft return is deleted; a posted one is reversed.").WithWhy(("status", ret.Status));
        }

        db.Returns.Remove(ret);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Deleted, CompanyId: ret.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Posts the return: the lines are re-checked against the receipt as it stands, the stock engine takes the goods out at the receipt's exact cost, and the receipt lines remember what went back.</summary>
    public async Task<Result<ReturnSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "draft")
        {
            return Error.Conflict("return.not_draft", "Only a draft return is posted.").WithWhy(("status", ret.Status));
        }

        var receipt = await db.Receipts.Include(static r => r.Lines).SingleAsync(r => r.Id == ret.ReceiptId, cancellationToken);
        var request = new SaveReturnRequest(ret.ReceiptId, ret.Lines.Select(static l => new SaveReturnLineRequest(l.ReceiptLineId, l.Quantity, null, l.UomId, l.BinId, l.LotNumber, Serials(l.SerialNumbers), l.Reason)).ToList(), ret.PostingDate, ret.Reason, ret.SupplierRma, ret.Notes, Shared.Parse(ret.CustomFields));
        var kept = ret.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
        db.ReturnLines.RemoveRange(ret.Lines);
        ret.Lines.Clear();
        var applied = await ApplyAsync(ret, receipt, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        foreach (var line in ret.Lines)
        {
            if (kept.TryGetValue(line.LineNo, out var lineId))
            {
                line.Id = lineId;
            }
        }

        var stockLines = new List<StockLine>();
        foreach (var line in ret.Lines)
        {
            var receiptLine = receipt.Lines.Single(l => l.Id == line.ReceiptLineId);
            var sleIds = SleIdsOf(receiptLine);
            Guid? appliesTo = sleIds.Count > 0 ? sleIds[0] : null;
            stockLines.Add(new StockLine(line.ItemId, StockEntryTypes.PurchaseReturn, line.Quantity, ret.WarehouseId, line.UomId, line.VariantId, line.BinId,
                SourceLineId: line.Id, AppliesToSleId: appliesTo, LotNumber: line.LotNumber, SerialNumbers: Serials(line.SerialNumbers), PartnerId: ret.PartnerId));
        }

        var posted = await inventory.PostAsync(new StockPostingRequest(ret.CompanyId, ret.PostingDate, DocumentType, ret.Id, stockLines, IdempotencyKey: $"purchase_return:{ret.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var entries = posted.Value.Entries.Where(static e => e.SourceLineId is not null).GroupBy(static e => e.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.ToList());
        foreach (var line in ret.Lines)
        {
            if (!entries.TryGetValue(line.Id, out var own))
            {
                continue;
            }

            line.SleId = own[0].Id;
            line.SleIds = JsonSerializer.Serialize(own.Select(static e => e.Id).ToList(), Shared.Json);
            line.CostAmountFc = Math.Abs(own.Sum(static e => e.CostAmount));
            var receiptLine = receipt.Lines.Single(l => l.Id == line.ReceiptLineId);
            receiptLine.QtyReturned += line.Quantity * (receiptLine.Quantity == 0m ? 1m : line.QuantityBase / receiptLine.QuantityBase * receiptLine.Quantity / line.Quantity);
            receiptLine.ReturnedCostAmount += line.CostAmountFc;
        }

        ret.TotalCostFc = ret.Lines.Sum(static l => l.CostAmountFc);
        ret.StockPostingId = posted.Value.PostingId;
        ret.JournalEntryId = posted.Value.JournalEntryId;
        ret.Status = "posted";
        ret.PostedAt = clock.UtcNow;
        ret.PostedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Posted, After: new { status = ret.Status, receipt = receipt.Number, ret.TotalCostFc, journal = posted.Value.JournalNumber }, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>Reverses a posted return while nothing on it has been credited: the goods come back in at the same cost (a receipt applied to the return's entries) and the receipt lines forget the return.</summary>
    public async Task<Result<ReturnSummary>> ReverseAsync(Guid id, ReverseReturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("return.reason_required", "A reversal names its reason.");
        }

        var ret = await db.Returns.Include(static r => r.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (ret.Status != "posted")
        {
            return Error.Conflict("return.not_posted", "Only a posted return is reversed.").WithWhy(("status", ret.Status));
        }

        if (ret.Lines.Any(static l => l.QtyCredited > 0m))
        {
            return Error.Conflict("return.credited", "The return has been credited by a debit note; reverse the debit note first.").WithWhy(("lines", ret.Lines.Where(static l => l.QtyCredited > 0m).Select(static l => l.LineNo)));
        }

        var company = await companies.FindAsync(new CompanyId(ret.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < ret.PostingDate)
        {
            return Error.Validation("return.reversal_date_invalid", "A return is reversed on or after the day it was posted.").WithWhy(("postingDate", ret.PostingDate), ("reversalDate", reversalDate));
        }

        var stockLines = ret.Lines.Select(l => new StockLine(l.ItemId, StockEntryTypes.PurchaseReceipt, l.Quantity, ret.WarehouseId, l.UomId, l.VariantId, l.BinId,
            SourceLineId: l.Id, UnitCost: l.Quantity == 0m ? 0m : l.CostAmountFc / l.Quantity, LotNumber: l.LotNumber, SerialNumbers: Serials(l.SerialNumbers), PartnerId: ret.PartnerId)).ToList();
        var posted = await inventory.PostAsync(new StockPostingRequest(ret.CompanyId, reversalDate, DocumentType, ret.Id, stockLines, IdempotencyKey: $"purchase_return_reversal:{ret.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var receipt = await db.Receipts.Include(static r => r.Lines).SingleAsync(r => r.Id == ret.ReceiptId, cancellationToken);
        foreach (var line in ret.Lines)
        {
            var receiptLine = receipt.Lines.Single(l => l.Id == line.ReceiptLineId);
            receiptLine.QtyReturned = Math.Max(0m, receiptLine.QtyReturned - line.Quantity * (receiptLine.Quantity == 0m ? 1m : line.QuantityBase / receiptLine.QuantityBase * receiptLine.Quantity / line.Quantity));
            receiptLine.ReturnedCostAmount -= line.CostAmountFc;
        }

        ret.Status = "reversed";
        ret.ReversalPostingId = posted.Value.PostingId;
        ret.ReversalReason = reason;
        ret.ReversedAt = clock.UtcNow;
        ret.ReversedBy = principal.Principal?.MembershipId.Value;
        ret.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, ret.Id, ret.Number, AuditActions.Reversed, After: new { status = ret.Status, reversalDate, journal = posted.Value.JournalNumber }, Reason: reason, CompanyId: ret.CompanyId), cancellationToken);
        return await MapAsync(ret, cancellationToken);
    }

    /// <summary>Return lines of posted returns with a quantity not yet credited, for debit notes.</summary>
    public async Task<IReadOnlyList<InvoicableLine>> CreditableAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken)
    {
        var rows = await (from l in db.ReturnLines.AsNoTracking()
                          join r in db.Returns.AsNoTracking() on new { l.TenantId, Id = l.ReturnId } equals new { r.TenantId, r.Id }
                          join rl in db.ReceiptLines.AsNoTracking() on new { l.TenantId, Id = l.ReceiptLineId } equals new { rl.TenantId, rl.Id }
                          where r.CompanyId == companyId && r.PartnerId == partnerId && r.Status == "posted" && l.QtyCredited < l.Quantity
                          orderby r.PostingDate, r.Number, l.LineNo
                          select new { Line = l, Return = r, ReceiptLine = rl }).ToListAsync(cancellationToken);
        var result = new List<InvoicableLine>(rows.Count);
        foreach (var row in rows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(row.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == row.Line.UomId);
            result.Add(new InvoicableLine("return", row.Line.ReceiptLineId, null, null, null, null, row.Line.LineNo, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), row.Line.UomId, uom?.UomCode ?? string.Empty,
                row.Line.Quantity, row.Line.QtyCredited, row.Line.Quantity - row.Line.QtyCredited, row.ReceiptLine.UnitPrice, row.Return.Currency, row.Return.PostingDate, null, null, row.Line.Id, row.Return.Number, ReturnId: row.Return.Id));
        }

        return result;
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result> ApplyAsync(SupplierReturn ret, Receipt receipt, SaveReturnRequest request, CancellationToken cancellationToken)
    {
        if (receipt.Status != "posted")
        {
            return Error.Conflict("return.receipt_not_posted", "Goods are returned from a posted receipt.").WithWhy(("receipt", receipt.Number), ("status", receipt.Status));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("return.lines_required", "A return has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(receipt.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", receipt.CompanyId);
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        if (postingDate < receipt.PostingDate)
        {
            return Error.Validation("return.before_receipt", "A return is posted on or after the receipt it returns.").WithWhy(("receipt", receipt.Number), ("receiptDate", receipt.PostingDate), ("postingDate", postingDate));
        }

        var warehouse = await warehouses.FindAsync(receipt.WarehouseId, cancellationToken);
        if (warehouse is null || !warehouse.IsActive)
        {
            return Error.Validation("return.warehouse_invalid", "The receipt's warehouse is no longer active.").WithWhy(("warehouseId", receipt.WarehouseId));
        }

        var lines = new List<SupplierReturnLine>();
        var returningNow = new Dictionary<Guid, decimal>();
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            var receiptLine = receipt.Lines.SingleOrDefault(x => x.Id == l.ReceiptLineId);
            if (receiptLine is null)
            {
                return Error.Validation("return.receipt_line_unknown", "The receipt line does not belong to the receipt.").WithWhy(("lineNo", lineNo), ("receiptLineId", l.ReceiptLineId));
            }

            var resolved = await Shared.ResolveLineAsync(items, "return", receiptLine.ItemId, null, l.Quantity, l.Uom, l.UomId ?? receiptLine.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!.WithWhy(("lineNo", lineNo));
            }

            var (_, unit, quantityBase) = resolved.Value;
            var inReceiptUom = quantityBase / (receiptLine.QuantityBase / receiptLine.Quantity);
            var already = returningNow.GetValueOrDefault(receiptLine.Id);
            if (receiptLine.QtyReturned + already + inReceiptUom > receiptLine.Quantity)
            {
                return Error.Conflict("return.over_return", "More than was received would go back.").WithWhy(("lineNo", lineNo), ("receiptLineNo", receiptLine.LineNo), ("received", receiptLine.Quantity), ("returned", receiptLine.QtyReturned + already), ("requested", inReceiptUom));
            }

            returningNow[receiptLine.Id] = already + inReceiptUom;
            lines.Add(new SupplierReturnLine
            {
                Id = Guid.CreateVersion7(),
                TenantId = ret.TenantId,
                ReturnId = ret.Id,
                LineNo = lineNo,
                ReceiptLineId = receiptLine.Id,
                ItemId = receiptLine.ItemId,
                VariantId = receiptLine.VariantId,
                Quantity = l.Quantity,
                UomId = unit.UomId,
                QuantityBase = quantityBase,
                BinId = l.BinId ?? receiptLine.BinId,
                LotNumber = Shared.Trim(l.LotNumber) ?? receiptLine.LotNumber,
                SerialNumbers = JsonSerializer.Serialize(l.SerialNumbers?.Select(static s => s.Trim()).Where(static s => s.Length > 0).ToList() ?? [], Shared.Json),
                Reason = Shared.Trim(l.Reason),
                CreatedAt = clock.UtcNow,
            });
        }

        ret.PostingDate = postingDate;
        ret.Reason = Shared.Trim(request.Reason);
        ret.SupplierRma = Shared.Trim(request.SupplierRma);
        ret.Notes = Shared.Trim(request.Notes);
        ret.CustomFields = custom.Value;
        ret.Lines.AddRange(lines);
        return Result.Success();
    }

    private static IReadOnlyList<Guid> SleIdsOf(ReceiptLine line)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(line.SleIds, Shared.Json) ?? [];
        return ids.Count > 0 ? ids : line.SleId is { } single ? [single] : [];
    }

    private static IReadOnlyList<string> Serials(string json) => JsonSerializer.Deserialize<List<string>>(json, Shared.Json) ?? [];

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private async Task<ReturnSummary> MapAsync(SupplierReturn r, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking().Include(static x => x.Lines).SingleAsync(x => x.Id == r.ReceiptId, cancellationToken);
        var partner = await partners.FindAsync(r.PartnerId, cancellationToken);
        var warehouse = await warehouses.FindAsync(r.WarehouseId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(r.CompanyId), cancellationToken);
        var lines = new List<ReturnLineSummary>(r.Lines.Count);
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            var receiptLine = receipt.Lines.SingleOrDefault(x => x.Id == l.ReceiptLineId);
            lines.Add(new ReturnLineSummary(l.Id, l.LineNo, l.ReceiptLineId, receiptLine?.LineNo ?? 0, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), l.Quantity, l.UomId, uom?.UomCode ?? string.Empty, l.QuantityBase, l.BinId, l.LotNumber, Serials(l.SerialNumbers), l.Reason, l.CostAmountFc, l.CreditedAmountFc, l.QtyCredited, l.SleId));
        }

        return new ReturnSummary(r.Id, r.CompanyId, r.Number, r.Status, r.ReceiptId, receipt.Number, r.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? Empty(), r.WarehouseId, warehouse?.Code, r.PostingDate, r.Currency, company?.FunctionalCurrency.Code ?? r.Currency, r.Reason, r.SupplierRma, r.TotalCostFc, r.StockPostingId, r.ReversalReason, r.Notes, Shared.Parse(r.CustomFields), lines, r.PostedAt, r.UpdatedAt);
    }
}

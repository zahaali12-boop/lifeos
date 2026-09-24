using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Supplier invoices (roadmap 4.4, POSTING_RULES "Supplier invoice"): lines against posted receipt lines (three-way),
/// order lines of services (two-way) or free expense lines; matched against the supplier's price and quantity
/// tolerances and against earlier invoices with the same supplier reference; a breach blocks the invoice until a
/// workflow override lets it through; approval runs the on-submit workflow; posting re-prices the receipt entries
/// through the costing engine (expected cost replaced by the invoice price, sold quantities adjusted through COGS),
/// books Dr GRNI / Dr expense / Cr WHT payable / Cr AP in the invoice currency, creates one payable open item per
/// payment-terms instalment and consumes the order's budget commitments. Reversal mirrors all of it.
/// </summary>
public sealed class InvoiceService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IPartnerDirectory partners,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IInventoryCosting costing,
    IPostingService posting,
    IWorkflowEngine workflow,
    LandedCostService landedCosts,
    ReturnService returns,
    IPayables payables,
    IDimensionSets dimensionSets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = PurchaseDocumentTypes.Invoice;

    /// <summary>invoice and expense are payables; a debit_note is the supplier's credit for returned goods (or a credit on expenses) and books the other way round.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["invoice", "expense", "debit_note"];

    public static readonly IReadOnlyList<string> LineKinds = ["receipt", "order", "expense", "charge", "return"];

    public static readonly IReadOnlyList<string> BlockKinds = ["price_variance", "qty_variance", "duplicate_suspect"];

    private static readonly string[] LiveStatuses = ["pending_approval", "blocked", "approved", "posted"];

    public async Task<IReadOnlyList<InvoiceSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Invoices.AsNoTracking().Include(static i => i.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(i => i.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        if (partnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        var invoices = await query.OrderByDescending(static i => i.PostingDate).ThenByDescending(static i => i.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<InvoiceSummary>(invoices.Count);
        foreach (var invoice in invoices)
        {
            result.Add(await MapAsync(invoice, cancellationToken));
        }

        return result;
    }

    /// <summary>What a supplier can still invoice: posted receipt lines with an uninvoiced quantity, and open service lines of its orders (received on the invoice).</summary>
    public async Task<IReadOnlyList<InvoicableLine>> InvoicableAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken)
    {
        var result = new List<InvoicableLine>();
        var receiptRows = await (from l in db.ReceiptLines.AsNoTracking()
                                 join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                                 join ol in db.OrderLines.AsNoTracking() on new { l.TenantId, Id = l.OrderLineId } equals new { ol.TenantId, ol.Id }
                                 join o in db.Orders.AsNoTracking() on new { r.TenantId, Id = r.OrderId } equals new { o.TenantId, o.Id }
                                 where r.CompanyId == companyId && r.PartnerId == partnerId && r.Status == "posted" && l.QtyInvoiced + l.QtyReturned < l.Quantity
                                 orderby r.PostingDate, r.Number, l.LineNo
                                 select new { Line = l, Receipt = r, OrderLine = ol, Order = o }).ToListAsync(cancellationToken);
        foreach (var row in receiptRows)
        {
            var (item, uom) = await ItemAsync(row.Line.ItemId, row.Line.UomId, cancellationToken);
            result.Add(new InvoicableLine("receipt", row.Line.Id, row.Receipt.Number, row.OrderLine.Id, row.Order.Number, row.Order.Id, row.Line.LineNo, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), row.Line.UomId, uom?.UomCode ?? string.Empty,
                row.Line.Quantity, row.Line.QtyInvoiced, row.Line.Quantity - row.Line.QtyInvoiced - row.Line.QtyReturned, row.Line.UnitPrice, row.Receipt.Currency, row.Receipt.PostingDate, ReceiptId: row.Receipt.Id));
        }

        var orderRows = await (from l in db.OrderLines.AsNoTracking()
                               join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                               where o.CompanyId == companyId && o.PartnerId == partnerId && (o.Status == "approved" || o.Status == "sent" || o.Status == "partially_received" || o.Status == "received") && l.Status != "cancelled" && l.Status != "closed" && l.QtyInvoiced + l.QtyCancelled < l.Quantity
                               orderby o.Number, l.LineNo
                               select new { Line = l, Order = o }).ToListAsync(cancellationToken);
        foreach (var row in orderRows)
        {
            var (item, uom) = await ItemAsync(row.Line.ItemId, row.Line.UomId, cancellationToken);
            if (item is null || item.IsStockItem)
            {
                continue;
            }

            result.Add(new InvoicableLine("order", null, null, row.Line.Id, row.Order.Number, row.Order.Id, row.Line.LineNo, row.Line.ItemId, item.Code, item.Name.Values, row.Line.UomId, uom?.UomCode ?? string.Empty,
                row.Line.Quantity - row.Line.QtyCancelled, row.Line.QtyInvoiced, row.Line.Quantity - row.Line.QtyCancelled - row.Line.QtyInvoiced, row.Line.UnitPrice * (1m - row.Line.DiscountPct / 100m), row.Order.Currency, null));
        }

        result.AddRange(await landedCosts.OpenChargesAsync(companyId, partnerId, cancellationToken));
        result.AddRange(await returns.CreditableAsync(companyId, partnerId, cancellationToken));
        return result;
    }

    public async Task<Result<InvoiceSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return invoice is null ? Error.NotFound(DocumentType, id) : await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result<InvoiceSummary>> CreateAsync(SaveInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoice = new Invoice { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, PartnerId = request.PartnerId, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(invoice, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(invoice.CompanyId), "PI", "PI-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(invoice.CompanyId), null, invoice.PostingDate, invoice.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        invoice.Number = number.Value.Text;
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Created, After: new { invoice.Number, invoice.Kind, invoice.TotalGross, invoice.Currency, lines = invoice.Lines.Count }, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result<InvoiceSummary>> UpdateAsync(Guid id, SaveInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoice = await db.Invoices.Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status is not ("draft" or "rejected" or "blocked"))
        {
            return Error.Conflict("invoice.not_editable", "Only a draft, rejected or blocked invoice is edited.").WithWhy(("status", invoice.Status));
        }

        if (request.CompanyId != invoice.CompanyId)
        {
            return Error.Validation("invoice.company_locked", "An invoice stays in the company it was drafted for.");
        }

        db.InvoiceLines.RemoveRange(invoice.Lines);
        invoice.Lines.Clear();
        invoice.PartnerId = request.PartnerId;
        var applied = await ApplyAsync(invoice, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        invoice.Status = "draft";
        invoice.BlockKind = null;
        invoice.BlockReason = null;
        invoice.BlockId = null;
        invoice.RejectionReason = null;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status is not ("draft" or "rejected" or "blocked"))
        {
            return Error.Conflict("invoice.not_editable", "Only a draft, rejected or blocked invoice is deleted; a posted one is reversed.").WithWhy(("status", invoice.Status));
        }

        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Deleted, CompanyId: invoice.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Submits: the lines are matched; a breach beyond tolerance blocks the invoice unless an override exists; then the active definition decides, none approves at once.</summary>
    public async Task<Result<InvoiceSummary>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status is not ("draft" or "rejected" or "blocked"))
        {
            return Error.Conflict("invoice.not_submittable", "Only a draft, rejected or blocked invoice is submitted.").WithWhy(("status", invoice.Status));
        }

        var supplier = await partners.EnsureSupplierAsync(invoice.CompanyId, invoice.PartnerId, SupplierPurposes.Purchase, cancellationToken);
        if (supplier.IsFailure)
        {
            return supplier.Error!;
        }

        var match = await MatchAsync(invoice, supplier.Value, cancellationToken);
        if (match.IsFailure)
        {
            return match.Error!;
        }

        var result = match.Value;
        db.MatchResults.Add(result);
        if (result.Status != "matched")
        {
            var overrideId = await workflow.ConsumeOverrideAsync(result.Status, DocumentType, invoice.Id, cancellationToken);
            if (overrideId is null)
            {
                var why = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["matchStatus"] = result.Status,
                    ["priceVarianceAmount"] = result.PriceVarianceAmount,
                    ["priceVariancePct"] = result.PriceVariancePct,
                    ["qtyVariance"] = result.QtyVariance,
                    ["priceTolerancePct"] = result.PriceTolerancePct,
                    ["qtyTolerancePct"] = result.QtyTolerancePct,
                    ["supplierInvoiceNumber"] = invoice.SupplierInvoiceNumber,
                    ["amount"] = invoice.TotalGross,
                    ["currency"] = invoice.Currency,
                };
                var block = await workflow.RaiseBlockAsync(new BlockRequest(result.Status, DocumentType, invoice.Id, invoice.CompanyId, Display(invoice), why, principal.Principal?.MembershipId.Value), cancellationToken);
                if (block.IsFailure)
                {
                    return block.Error!;
                }

                invoice.Status = "blocked";
                invoice.BlockKind = result.Status;
                invoice.BlockId = block.Value.BlockId;
                invoice.BlockReason = block.Value.Status == BlockStatuses.Pending ? "An override has been requested." : "No approval routes this block; fix the invoice or the order.";
                invoice.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.StateChanged, After: new { status = invoice.Status, blockKind = invoice.BlockKind, blockStatus = block.Value.Status, why }, CompanyId: invoice.CompanyId), cancellationToken);
                return await MapAsync(invoice, cancellationToken);
            }

            result.OverrideId = overrideId;
        }

        invoice.BlockKind = null;
        invoice.BlockReason = null;
        invoice.BlockId = null;
        invoice.SubmittedAt = clock.UtcNow;
        invoice.SubmittedBy = principal.Principal?.UserId.Value;
        invoice.UpdatedAt = clock.UtcNow;
        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(await SubjectAsync(invoice, result, cancellationToken), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.Pending)
            {
                invoice.Status = "pending_approval";
                invoice.ApprovalRequestId = outcome.Value.RequestId;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.StateChanged, After: new { status = invoice.Status, approvalRequestId = invoice.ApprovalRequestId, rule = outcome.Value.RuleName?.Values }, CompanyId: invoice.CompanyId), cancellationToken);
                return await MapAsync(invoice, cancellationToken);
            }
        }

        await ApproveCoreAsync(invoice, null, cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var invoice = await db.Invoices.Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == decision.EntityId, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, decision.EntityId);
        }

        if (decision.OverrideId is not null || invoice.Status == "blocked")
        {
            // A decision on a block: the engine records the override; the invoice stays blocked until it is submitted again and consumes it.
            var granted = decision.Status == WorkflowDecisions.Approved;
            invoice.BlockReason = granted ? "An override was granted; submit the invoice again." : $"The override was refused{(decision.Comment is null ? "." : ": " + decision.Comment)}";
            invoice.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, granted ? AuditActions.Override : AuditActions.StateChanged, After: new { status = invoice.Status, blockKind = invoice.BlockKind, overrideId = decision.OverrideId, granted }, Reason: decision.Comment, CompanyId: invoice.CompanyId), cancellationToken);
            return Result.Success();
        }

        if (invoice.Status != "pending_approval" || invoice.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("invoice.not_pending", "The invoice is no longer awaiting this approval request.").WithWhy(("status", invoice.Status));
        }

        if (decision.Status == WorkflowDecisions.Approved)
        {
            await ApproveCoreAsync(invoice, decision.Comment, cancellationToken);
            return Result.Success();
        }

        var rejected = decision.Status == WorkflowDecisions.Rejected;
        invoice.Status = rejected ? "rejected" : "draft";
        invoice.RejectionReason = rejected ? decision.Comment ?? "Not approved." : null;
        invoice.ApprovalRequestId = null;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, rejected ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status = invoice.Status, reason = invoice.RejectionReason }, CompanyId: invoice.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Posts an approved invoice: receipts re-priced, the journal booked, payables opened, commitments consumed.</summary>
    public async Task<Result<InvoiceSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "approved")
        {
            return Error.Conflict("invoice.not_approved", "Only an approved invoice is posted.").WithWhy(("status", invoice.Status));
        }

        var company = await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", invoice.CompanyId);
        }

        var supplier = await partners.FindSupplierAsync(invoice.CompanyId, invoice.PartnerId, cancellationToken);
        var rate = await RateAsync(company, invoice.Currency, invoice.PostingDate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error!;
        }

        invoice.ExchangeRate = rate.Value;
        var receiptLineIds = invoice.Lines.Where(static l => l.ReceiptLineId is not null).Select(static l => l.ReceiptLineId!.Value).ToList();
        var receiptLines = await db.ReceiptLines.Where(l => receiptLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var receiptIds = receiptLines.Values.Select(static l => l.ReceiptId).Distinct().ToList();
        var receipts = await db.Receipts.Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(static r => r.Id, cancellationToken);
        var orderLineIds = invoice.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).Distinct().ToList();
        var orderLines = await db.OrderLines.Where(l => orderLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var returnLineIds = invoice.Lines.Where(static l => l.ReturnLineId is not null).Select(static l => l.ReturnLineId!.Value).ToList();
        var returnLines = await db.ReturnLines.Where(l => returnLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var returnIds = returnLines.Values.Select(static l => l.ReturnId).Distinct().ToList();
        var supplierReturns = await db.Returns.Where(r => returnIds.Contains(r.Id)).ToDictionaryAsync(static r => r.Id, cancellationToken);
        var sign = invoice.Kind == "debit_note" ? -1m : 1m;
        var sameCurrency = string.Equals(invoice.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        var currency = await Shared.CurrencyAsync(companies, invoice.Currency, company.FunctionalCurrency.Code, "invoice", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var returnCostFc = new Dictionary<Guid, decimal>();

        // 1. The costing engine settles each receipt entry at the invoice price (in the company's currency, per received unit).
        foreach (var line in invoice.Lines.Where(static l => l.Kind == "receipt"))
        {
            var receiptLine = receiptLines[line.ReceiptLineId!.Value];
            var actualUnitCost = line.UnitPrice * (1m - line.DiscountPct / 100m) * rate.Value;
            foreach (var sleId in SleIds(receiptLine))
            {
                var adjusted = await costing.AdjustInboundCostAsync(new InboundCostAdjustmentRequest(sleId, "invoice", DocumentType, invoice.Id, actualUnitCost, null, invoice.PostingDate, $"Supplier invoice {invoice.Number}", $"purchase_invoice:{invoice.Id}:{line.Id}:{sleId}"), cancellationToken);
                if (adjusted.IsFailure)
                {
                    return adjusted.Error!.WithWhy(("lineNo", line.LineNo));
                }

                // The entries the engine created on the receipt entry itself (reversal of the expected cost, the actual cost) move the booked value by their sum.
                receiptLine.ExpectedCostAmount += adjusted.Value.ValueEntries.Sum(static v => v.CostAmountActual + v.CostAmountExpected);
            }
        }

        // 2. The journal in the invoice currency at the invoice rate. Lines that land in profit and loss carry the line's
        // dimensions; goods and landed-cost lines relieve balance-sheet clearing accounts, as the receipt booked them.
        var postingLines = new List<PostingLine>();
        var dimensionsBySet = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>?>();
        async Task<IReadOnlyDictionary<string, Guid>?> DimensionsOf(InvoiceLine line)
        {
            if (line.DimensionSetId is not { } setId)
            {
                return null;
            }

            if (!dimensionsBySet.TryGetValue(setId, out var values))
            {
                values = await dimensionSets.GetAsync(setId, cancellationToken);
                dimensionsBySet[setId] = values;
            }

            return values;
        }

        foreach (var line in invoice.Lines)
        {
            switch (line.Kind)
            {
                case "receipt":
                    {
                        var receiptLine = receiptLines[line.ReceiptLineId!.Value];
                        var receipt = receipts[receiptLine.ReceiptId];
                        var item = await items.FindAsync(receiptLine.ItemId, cancellationToken);
                        postingLines.Add(new PostingLine(AccountRoles.GRNI, line.NetAmount, new PostingKeys(DocumentType, item?.ItemPostingGroupId, supplier?.PostingGroupId, WarehouseId: receipt.WarehouseId), SubledgerType: SubledgerTypes.GoodsReceivedNotInvoiced, SubledgerRef: receipt.Id, PartnerId: invoice.PartnerId));
                        break;
                    }

                case "order":
                    {
                        var item = line.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
                        postingLines.Add(new PostingLine(line.AccountRole ?? AccountRoles.PurchaseExpense, line.NetAmount, new PostingKeys(DocumentType, item?.ItemPostingGroupId, supplier?.PostingGroupId), Dimensions: await DimensionsOf(line), PartnerId: invoice.PartnerId, Description: line.Description is null ? null : LocalizedText.Bilingual(line.Description, line.Description)));
                        break;
                    }

                case "charge":
                    {
                        var charge = await db.LandedCostCharges.AsNoTracking().SingleAsync(x => x.Id == line.LandedCostChargeId, cancellationToken);
                        postingLines.Add(new PostingLine(AccountRoles.LandedCostClearing, line.NetAmount, new PostingKeys(DocumentType, null, supplier?.PostingGroupId), SubledgerType: SubledgerTypes.GoodsReceivedNotInvoiced, SubledgerRef: charge.LandedCostId, PartnerId: invoice.PartnerId, Description: line.Description is null ? null : LocalizedText.Bilingual(line.Description, line.Description)));
                        break;
                    }

                case "return":
                    {
                        // The return relieved GRNI at the goods' cost with the return as subledger reference; the supplier's credit clears that
                        // cost and what it credits above or below it is a purchase price variance (the goods are gone, nothing is left to re-price).
                        var returnLine = returnLines[line.ReturnLineId!.Value];
                        var ret = supplierReturns[returnLine.ReturnId];
                        var item = await items.FindAsync(returnLine.ItemId, cancellationToken);
                        var remainingQty = returnLine.Quantity - returnLine.QtyCredited - invoice.Lines.Where(l => l.ReturnLineId == returnLine.Id && l.LineNo < line.LineNo).Sum(static l => l.Quantity);
                        var remainingCostFc = returnLine.CostAmountFc - returnLine.CreditedAmountFc - returnCostFc.Where(kv => invoice.Lines.Single(l => l.Id == kv.Key).ReturnLineId == returnLine.Id).Sum(static kv => kv.Value);
                        var costShareFc = remainingQty <= 0m ? 0m : line.Quantity >= remainingQty ? remainingCostFc : Shared.Round(remainingCostFc * line.Quantity / remainingQty, company.FunctionalCurrency);
                        var costTc = sameCurrency ? costShareFc : Shared.Round(costShareFc / rate.Value, currency.Value);
                        returnCostFc[line.Id] = costShareFc;
                        postingLines.Add(new PostingLine(AccountRoles.GRNI, -costTc, new PostingKeys(DocumentType, item?.ItemPostingGroupId, supplier?.PostingGroupId, WarehouseId: ret.WarehouseId), SubledgerType: SubledgerTypes.GoodsReceivedNotInvoiced, SubledgerRef: ret.Id, PartnerId: invoice.PartnerId));
                        var variance = line.NetAmount - costTc;
                        if (variance != 0m)
                        {
                            postingLines.Add(new PostingLine(AccountRoles.PurchasePriceVariance, -variance, new PostingKeys(DocumentType, item?.ItemPostingGroupId, supplier?.PostingGroupId, WarehouseId: ret.WarehouseId), Dimensions: await DimensionsOf(line), PartnerId: invoice.PartnerId, Description: LocalizedText.Bilingual($"Return {ret.Number} credited at {line.UnitPrice:0.##} against cost", $"إشعار على المرتجع {ret.Number} بسعر {line.UnitPrice:0.##} مقابل الكلفة")));
                        }

                        break;
                    }

                default:
                    postingLines.Add(new PostingLine(line.AccountRole ?? AccountRoles.PurchaseExpense, sign * line.NetAmount, new PostingKeys(DocumentType, null, supplier?.PostingGroupId), Dimensions: await DimensionsOf(line), PartnerId: invoice.PartnerId, Description: line.Description is null ? null : LocalizedText.Bilingual(line.Description, line.Description)));
                    break;
            }
        }

        if (invoice.TotalTax > 0m)
        {
            postingLines.Add(new PostingLine(AccountRoles.InputTax, sign * invoice.TotalTax, new PostingKeys(DocumentType), PartnerId: invoice.PartnerId));
        }

        if (invoice.TotalWht > 0m)
        {
            postingLines.Add(new PostingLine(AccountRoles.WhtPayable, -invoice.TotalWht, new PostingKeys(DocumentType), PartnerId: invoice.PartnerId));
        }

        postingLines.Add(new PostingLine(AccountRoles.AP, -sign * invoice.TotalPayable, new PostingKeys(DocumentType, PartnerPostingGroupId: supplier?.PostingGroupId), SubledgerType: SubledgerTypes.Payables, SubledgerRef: invoice.Id, PartnerId: invoice.PartnerId, DueDate: invoice.DueDate));
        var reference = invoice.SupplierInvoiceNumber is null ? string.Empty : $" ({invoice.SupplierInvoiceNumber})";
        var description = invoice.Kind == "debit_note"
            ? LocalizedText.Bilingual($"Supplier debit note {invoice.Number}{reference}", $"إشعار مدين للمورد {invoice.Number}{reference}")
            : LocalizedText.Bilingual($"Supplier invoice {invoice.Number}{reference}", $"فاتورة مورد {invoice.Number}{reference}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "purchasing", DocumentType, invoice.Id, invoice.PostingDate, invoice.Currency, postingLines, invoice.Number, invoice.DocumentDate, description,
            invoice.BranchId is { } b ? new BranchId(b) : null, RateTypes.Spot, sameCurrency ? null : rate.Value, sameCurrency ? null : "Invoice rate", $"purchase_invoice:{invoice.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // 3. Receipt lines: what this invoice settled, allocated from the GRNI lines actually booked so the invariant holds to the minor unit.
        foreach (var group in invoice.Lines.Where(static l => l.Kind == "receipt").GroupBy(l => receiptLines[l.ReceiptLineId!.Value].ReceiptId))
        {
            var bookedFc = posted.Value.Lines.Where(l => l.SubledgerType == SubledgerTypes.GoodsReceivedNotInvoiced && l.SubledgerRef == group.Key).Sum(static l => l.DebitFc - l.CreditFc);
            var lines = group.ToList();
            var shares = RoundingPolicy.Default.Allocate(new Money(bookedFc, company.FunctionalCurrency), lines.Select(static l => l.NetAmount).ToList());
            for (var i = 0; i < lines.Count; i++)
            {
                var receiptLine = receiptLines[lines[i].ReceiptLineId!.Value];
                lines[i].NetAmountFc = shares[i].Amount;
                receiptLine.QtyInvoiced += lines[i].Quantity;
                receiptLine.InvoicedCostAmount += shares[i].Amount;
                var orderLine = orderLines[receiptLine.OrderLineId];
                orderLine.QtyInvoiced += receiptLine.QtyInOrderUom * lines[i].Quantity / receiptLine.Quantity;
            }
        }

        foreach (var line in invoice.Lines.Where(static l => l.Kind == "order"))
        {
            var orderLine = orderLines[line.OrderLineId!.Value];
            orderLine.QtyInvoiced += line.Quantity;
            line.NetAmountFc = Shared.Round(line.NetAmount * rate.Value, company.FunctionalCurrency);
        }

        foreach (var line in invoice.Lines.Where(static l => l.Kind == "expense"))
        {
            line.NetAmountFc = Shared.Round(line.NetAmount * rate.Value, company.FunctionalCurrency);
        }

        // Return lines: what the supplier credited, allocated from the GRNI credits actually booked per return.
        foreach (var group in invoice.Lines.Where(static l => l.Kind == "return").GroupBy(l => returnLines[l.ReturnLineId!.Value].ReturnId))
        {
            var bookedFc = posted.Value.Lines.Where(l => l.SubledgerType == SubledgerTypes.GoodsReceivedNotInvoiced && l.SubledgerRef == group.Key).Sum(static l => l.CreditFc - l.DebitFc);
            var lines = group.ToList();
            var weights = lines.Select(l => returnCostFc.GetValueOrDefault(l.Id)).ToList();
            var shares = weights.Sum() == 0m ? lines.Select(_ => new Money(0m, company.FunctionalCurrency)).ToList() : RoundingPolicy.Default.Allocate(new Money(bookedFc, company.FunctionalCurrency), weights);
            for (var i = 0; i < lines.Count; i++)
            {
                var returnLine = returnLines[lines[i].ReturnLineId!.Value];
                lines[i].NetAmountFc = Shared.Round(lines[i].NetAmount * rate.Value, company.FunctionalCurrency);
                returnLine.QtyCredited += lines[i].Quantity;
                returnLine.CreditedAmountFc += shares[i].Amount;
            }
        }

        // Charge lines settle their landed-cost estimates at what was booked on the clearing account; a difference lands on the same receipt lines.
        foreach (var group in invoice.Lines.Where(static l => l.Kind == "charge").GroupBy(static l => l.LandedCostChargeId!.Value))
        {
            var chargeLines = group.ToList();
            var bookedFc = Shared.Round(chargeLines.Sum(static l => l.NetAmount) * rate.Value, company.FunctionalCurrency);
            var shares = RoundingPolicy.Default.Allocate(new Money(bookedFc, company.FunctionalCurrency), chargeLines.Select(static l => l.NetAmount).ToList());
            for (var i = 0; i < chargeLines.Count; i++)
            {
                chargeLines[i].NetAmountFc = shares[i].Amount;
            }

            var settled = await landedCosts.SettleChargeAsync(group.Key, chargeLines[0].Id, bookedFc, invoice.PostingDate, $"Charge invoice {invoice.Number}", +1, cancellationToken);
            if (settled.IsFailure)
            {
                return settled.Error!;
            }
        }

        // 4. Commitments consumed by what the invoice books against each order line.
        var commitments = await db.Commitments.Where(c => orderLineIds.Contains(c.OrderLineId) && c.Status != "released").ToListAsync(cancellationToken);
        foreach (var group in invoice.Lines.Where(static l => l.OrderLineId is not null || l.ReceiptLineId is not null).GroupBy(l => l.OrderLineId ?? receiptLines[l.ReceiptLineId!.Value].OrderLineId))
        {
            var commitment = commitments.FirstOrDefault(c => c.OrderLineId == group.Key);
            if (commitment is null)
            {
                continue;
            }

            commitment.ConsumedRc += group.Sum(static l => l.NetAmountFc);
            commitment.Status = commitment.ConsumedRc >= commitment.AmountRc ? "consumed" : "open";
            commitment.UpdatedAt = clock.UtcNow;
        }

        // 5. Payables: one open item per instalment of the payment terms.
        var instalments = await InstalmentsAsync(invoice, cancellationToken);
        if (instalments.IsFailure)
        {
            return instalments.Error!;
        }

        var sequence = 0;
        foreach (var instalment in instalments.Value)
        {
            sequence++;
            await payables.OpenAsync(new NewOpenItem(invoice.CompanyId, invoice.PartnerId, invoice.Kind == "debit_note" ? PayableKinds.DebitNote : PayableKinds.Invoice, DocumentType, invoice.Id, invoice.Number, sequence, invoice.SupplierInvoiceNumber,
                invoice.PostingDate, invoice.DocumentDate, instalment.DueOn, instalment.DiscountUntil, instalment.DiscountPct, invoice.Currency, sign * instalment.Amount, sign * Shared.Round(instalment.Amount * rate.Value, company.FunctionalCurrency), rate.Value, posted.Value.EntryId, invoice.BranchId), cancellationToken);
        }

        invoice.DueDate ??= instalments.Value.Count > 0 ? instalments.Value[0].DueOn : invoice.DocumentDate;
        invoice.JournalEntryId = posted.Value.EntryId;
        invoice.Status = "posted";
        invoice.PostedAt = clock.UtcNow;
        invoice.PostedBy = principal.Principal?.MembershipId.Value;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Posted, After: new { status = invoice.Status, journal = posted.Value.Number, invoice.TotalGross, invoice.TotalWht, invoice.TotalPayable, invoice.Currency, instalments = sequence }, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    /// <summary>Reverses a posted invoice: the journal mirrored, the receipt entries priced back to what they carried before, payables closed, commitments and quantities given back.</summary>
    public async Task<Result<InvoiceSummary>> ReverseAsync(Guid id, ReverseInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("invoice.reason_required", "A reversal names its reason.");
        }

        var invoice = await db.Invoices.Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "posted")
        {
            return Error.Conflict("invoice.not_posted", "Only a posted invoice is reversed.").WithWhy(("status", invoice.Status));
        }

        var company = await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        var itemsReversed = await payables.ReverseDocumentAsync(DocumentType, invoice.Id, reversalDate, cancellationToken);
        if (itemsReversed.IsFailure)
        {
            return itemsReversed.Error!.Code == "payables.item_settled" ? Error.Conflict("invoice.settled", "The invoice has been paid or credited in part; reverse those settlements first.").WithWhy(("items", itemsReversed.Error!.Why)) : itemsReversed.Error!;
        }

        var reversed = await posting.ReverseAsync(invoice.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var receiptLineIds = invoice.Lines.Where(static l => l.ReceiptLineId is not null).Select(static l => l.ReceiptLineId!.Value).ToList();
        var receiptLines = await db.ReceiptLines.Where(l => receiptLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var orderLineIds = invoice.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).Concat(receiptLines.Values.Select(static l => l.OrderLineId)).Distinct().ToList();
        var orderLines = await db.OrderLines.Where(l => orderLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        foreach (var line in invoice.Lines.Where(static l => l.Kind == "receipt"))
        {
            var receiptLine = receiptLines[line.ReceiptLineId!.Value];
            foreach (var sleId in SleIds(receiptLine))
            {
                var adjusted = await costing.AdjustInboundCostAsync(new InboundCostAdjustmentRequest(sleId, "invoice", DocumentType, invoice.Id, receiptLine.ExpectedUnitCost, null, reversed.Value.PostingDate, $"Supplier invoice {invoice.Number} reversed: {reason}", $"purchase_invoice_reversal:{invoice.Id}:{line.Id}:{sleId}"), cancellationToken);
                if (adjusted.IsFailure)
                {
                    return adjusted.Error!.WithWhy(("lineNo", line.LineNo));
                }

                receiptLine.ExpectedCostAmount += adjusted.Value.ValueEntries.Sum(static v => v.CostAmountActual + v.CostAmountExpected);
            }
            receiptLine.QtyInvoiced -= line.Quantity;
            receiptLine.InvoicedCostAmount -= line.NetAmountFc;
            orderLines[receiptLine.OrderLineId].QtyInvoiced -= receiptLine.QtyInOrderUom * line.Quantity / receiptLine.Quantity;
        }

        foreach (var line in invoice.Lines.Where(static l => l.Kind == "order"))
        {
            orderLines[line.OrderLineId!.Value].QtyInvoiced -= line.Quantity;
        }

        var returnLineIds = invoice.Lines.Where(static l => l.ReturnLineId is not null).Select(static l => l.ReturnLineId!.Value).ToList();
        var returnLines = await db.ReturnLines.Where(l => returnLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        foreach (var line in invoice.Lines.Where(static l => l.Kind == "return"))
        {
            var returnLine = returnLines[line.ReturnLineId!.Value];
            returnLine.QtyCredited -= line.Quantity;
            returnLine.CreditedAmountFc -= line.NetAmountFc;
        }

        foreach (var line in invoice.Lines.Where(static l => l.Kind == "charge"))
        {
            var unsettled = await landedCosts.SettleChargeAsync(line.LandedCostChargeId!.Value, line.Id, 0m, reversed.Value.PostingDate, $"Charge invoice {invoice.Number} reversed: {reason}", -1, cancellationToken);
            if (unsettled.IsFailure)
            {
                return unsettled.Error!;
            }
        }

        var commitments = await db.Commitments.Where(c => orderLineIds.Contains(c.OrderLineId) && c.Status != "released").ToListAsync(cancellationToken);
        foreach (var group in invoice.Lines.Where(static l => l.OrderLineId is not null || l.ReceiptLineId is not null).GroupBy(l => l.OrderLineId ?? receiptLines[l.ReceiptLineId!.Value].OrderLineId))
        {
            var commitment = commitments.FirstOrDefault(c => c.OrderLineId == group.Key);
            if (commitment is null)
            {
                continue;
            }

            commitment.ConsumedRc = Math.Max(0m, commitment.ConsumedRc - group.Sum(static l => l.NetAmountFc));
            commitment.Status = commitment.ConsumedRc >= commitment.AmountRc ? "consumed" : "open";
            commitment.UpdatedAt = clock.UtcNow;
        }

        invoice.Status = "reversed";
        invoice.ReversalEntryId = reversed.Value.EntryId;
        invoice.ReversalReason = reason;
        invoice.ReversedAt = clock.UtcNow;
        invoice.ReversedBy = principal.Principal?.MembershipId.Value;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Reversed, After: new { status = invoice.Status, reversalDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task ApproveCoreAsync(Invoice invoice, string? comment, CancellationToken cancellationToken)
    {
        invoice.Status = "approved";
        invoice.ApprovedAt = clock.UtcNow;
        invoice.ApprovalRequestId = null;
        invoice.RejectionReason = null;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Approved, After: new { status = invoice.Status, invoice.TotalGross, invoice.Currency }, Reason: comment, CompanyId: invoice.CompanyId), cancellationToken);
    }

    /// <summary>Validates the request and rebuilds the header and lines: currency and rate, totals, withholding at invoice, due date from the terms.</summary>
    private async Task<Result> ApplyAsync(Invoice invoice, SaveInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (!Kinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("invoice.kind_invalid", "The kind is invoice, expense or debit_note.").WithWhy(("kind", request.Kind));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("invoice.lines_required", "An invoice has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("invoice.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var supplier = await partners.FindSupplierAsync(request.CompanyId, request.PartnerId, cancellationToken);
        if (supplier is null)
        {
            return Error.Validation("supplier.not_registered", "The partner is not registered as a supplier of the company.").WithWhy(("partnerId", request.PartnerId));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, supplier.Currency, "invoice", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var documentDate = request.DocumentDate ?? clock.TodayIn(company.TimeZone);
        var postingDate = request.PostingDate ?? documentDate;
        var rate = await RateAsync(company, currency.Value.Code, postingDate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error!;
        }

        // A debit note carries no withholding: the tax was withheld on the invoice it credits and is settled with the authority as withheld.
        var whtCodeId = request.ApplyWht && request.Kind != "debit_note" ? request.WhtCodeId ?? supplier.WhtCodeId : null;
        WhtCodeInfo? wht = null;
        if (whtCodeId is { } w)
        {
            wht = await partners.FindWhtCodeAsync(w, cancellationToken);
            if (wht is null || !wht.IsActive)
            {
                return Error.Validation("invoice.wht_code_invalid", "The withholding code does not exist or is inactive.").WithWhy(("whtCodeId", w));
            }
        }

        var lines = new List<InvoiceLine>();
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            if (!LineKinds.Contains(l.Kind, StringComparer.Ordinal))
            {
                return Error.Validation("invoice.line_kind_invalid", "A line is against a receipt, an order or an expense account.").WithWhy(("lineNo", lineNo), ("kind", l.Kind));
            }

            if (request.Kind == "expense" && l.Kind != "expense")
            {
                return Error.Validation("invoice.expense_lines_only", "An expense invoice carries expense lines only.").WithWhy(("lineNo", lineNo));
            }

            if (request.Kind == "debit_note" && l.Kind is not ("return" or "expense"))
            {
                return Error.Validation("invoice.debit_note_lines_only", "A debit note credits returned goods or expense accounts only.").WithWhy(("lineNo", lineNo), ("kind", l.Kind));
            }

            if (request.Kind != "debit_note" && l.Kind == "return")
            {
                return Error.Validation("invoice.return_needs_debit_note", "Returned goods are credited on a debit note.").WithWhy(("lineNo", lineNo));
            }

            if (l.Quantity <= 0m || l.UnitPrice < 0m || l.DiscountPct is < 0m or > 100m)
            {
                return Error.Validation("invoice.line_invalid", "Quantities are positive, prices not negative and discounts between 0 and 100.").WithWhy(("lineNo", lineNo));
            }

            var line = new InvoiceLine { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, LineNo = lineNo, Kind = l.Kind, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPct = l.DiscountPct, Description = Shared.Trim(l.Description), CreatedAt = clock.UtcNow };
            var explicitDimensions = l.Dimensions is { Count: > 0 } || l.DimensionSetId is not null;
            if (explicitDimensions && l.Kind is "receipt" or "charge")
            {
                return Error.Validation("invoice.dimensions_not_applicable", "Goods and landed-cost lines settle balance-sheet accounts; their dimensions come from the receipt and the stock.").WithWhy(("lineNo", lineNo), ("kind", l.Kind));
            }

            if (l.Dimensions is { Count: > 0 } values)
            {
                if (l.DimensionSetId is not null)
                {
                    return Error.Validation("invoice.dimensions_ambiguous", "A line gives its dimension values or a dimension set, not both.").WithWhy(("lineNo", lineNo));
                }

                var set = await dimensionSets.GetOrCreateAsync(values, cancellationToken);
                if (set.IsFailure)
                {
                    return set.Error!.WithWhy(("lineNo", lineNo));
                }

                line.DimensionSetId = set.Value;
            }
            else if (l.DimensionSetId is { } setId)
            {
                if (await dimensionSets.GetAsync(setId, cancellationToken) is null)
                {
                    return Error.Validation("invoice.dimension_set_unknown", "The dimension set does not exist.").WithWhy(("lineNo", lineNo), ("dimensionSetId", setId));
                }

                line.DimensionSetId = setId;
            }

            switch (l.Kind)
            {
                case "receipt":
                    {
                        if (l.ReceiptLineId is null)
                        {
                            return Error.Validation("invoice.receipt_line_required", "A receipt line names the receipt line it invoices.").WithWhy(("lineNo", lineNo));
                        }

                        var receiptLine = await db.ReceiptLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.ReceiptLineId, cancellationToken);
                        var receipt = receiptLine is null ? null : await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.Id == receiptLine.ReceiptId, cancellationToken);
                        if (receiptLine is null || receipt is null || receipt.CompanyId != request.CompanyId || receipt.PartnerId != request.PartnerId)
                        {
                            return Error.Validation("invoice.receipt_line_unknown", "The receipt line does not belong to this supplier and company.").WithWhy(("lineNo", lineNo), ("receiptLineId", l.ReceiptLineId));
                        }

                        if (receipt.Status != "posted")
                        {
                            return Error.Conflict("invoice.receipt_not_posted", "Only posted receipts are invoiced.").WithWhy(("lineNo", lineNo), ("receipt", receipt.Number), ("status", receipt.Status));
                        }

                        if (!string.Equals(receipt.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A matched line is invoiced in the order's currency.").WithWhy(("lineNo", lineNo), ("orderCurrency", receipt.Currency), ("invoiceCurrency", currency.Value.Code));
                        }

                        line.ReceiptLineId = receiptLine.Id;
                        line.OrderLineId = receiptLine.OrderLineId;
                        line.ItemId = receiptLine.ItemId;
                        line.UomId = receiptLine.UomId;
                        line.ExpectedUnitPrice = receiptLine.UnitPrice;
                        break;
                    }

                case "order":
                    {
                        if (l.OrderLineId is null)
                        {
                            return Error.Validation("invoice.order_line_required", "An order line names the order line it invoices.").WithWhy(("lineNo", lineNo));
                        }

                        var orderLine = await db.OrderLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.OrderLineId, cancellationToken);
                        var order = orderLine is null ? null : await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderLine.OrderId, cancellationToken);
                        if (orderLine is null || order is null || order.CompanyId != request.CompanyId || order.PartnerId != request.PartnerId)
                        {
                            return Error.Validation("invoice.order_line_unknown", "The order line does not belong to this supplier and company.").WithWhy(("lineNo", lineNo), ("orderLineId", l.OrderLineId));
                        }

                        var item = await items.FindAsync(orderLine.ItemId, cancellationToken);
                        if (item is null || item.IsStockItem)
                        {
                            return Error.Validation("invoice.stock_line_needs_receipt", "A stock item is invoiced against its goods receipt (three-way match).").WithWhy(("lineNo", lineNo), ("item", item?.Code));
                        }

                        if (order.Status is "draft" or "pending_approval" or "rejected" or "cancelled" or "closed")
                        {
                            return Error.Conflict("invoice.order_not_open", "The order is not open for invoicing.").WithWhy(("lineNo", lineNo), ("order", order.Number), ("status", order.Status));
                        }

                        if (!string.Equals(order.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A matched line is invoiced in the order's currency.").WithWhy(("lineNo", lineNo), ("orderCurrency", order.Currency), ("invoiceCurrency", currency.Value.Code));
                        }

                        line.OrderLineId = orderLine.Id;
                        line.ItemId = orderLine.ItemId;
                        line.UomId = orderLine.UomId;
                        line.AccountRole = AccountRoles.PurchaseExpense;
                        line.ExpectedUnitPrice = orderLine.UnitPrice * (1m - orderLine.DiscountPct / 100m);
                        if (!explicitDimensions)
                        {
                            line.DimensionSetId = orderLine.DimensionSetId;
                        }

                        break;
                    }

                case "charge":
                    {
                        if (l.LandedCostChargeId is null)
                        {
                            return Error.Validation("invoice.charge_required", "A charge line names the landed-cost charge it settles.").WithWhy(("lineNo", lineNo));
                        }

                        var charge = await db.LandedCostCharges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.LandedCostChargeId, cancellationToken);
                        var doc = charge is null ? null : await db.LandedCosts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == charge.LandedCostId, cancellationToken);
                        if (charge is null || doc is null || doc.CompanyId != request.CompanyId)
                        {
                            return Error.Validation("invoice.charge_unknown", "The landed-cost charge does not belong to the company.").WithWhy(("lineNo", lineNo), ("chargeId", l.LandedCostChargeId));
                        }

                        if (doc.Status != "posted" || !charge.IsEstimate)
                        {
                            return Error.Conflict("invoice.charge_not_open", "Only an estimated charge of a posted landed-cost document is settled by an invoice.").WithWhy(("lineNo", lineNo), ("document", doc.Number), ("status", doc.Status), ("isEstimate", charge.IsEstimate));
                        }

                        if (charge.PartnerId is { } expectedPartner && expectedPartner != request.PartnerId)
                        {
                            return Error.Validation("invoice.charge_other_supplier", "The charge was expected from another supplier.").WithWhy(("lineNo", lineNo), ("document", doc.Number));
                        }

                        if (lines.Any(x => x.LandedCostChargeId == charge.Id))
                        {
                            return Error.Validation("invoice.charge_duplicate", "A charge is settled once on an invoice.").WithWhy(("lineNo", lineNo));
                        }

                        line.LandedCostChargeId = charge.Id;
                        line.AccountRole = AccountRoles.LandedCostClearing;
                        line.Description ??= $"Landed cost {doc.Number} line {charge.LineNo}";
                        break;
                    }

                case "return":
                    {
                        if (l.ReturnLineId is null)
                        {
                            return Error.Validation("invoice.return_line_required", "A return line names the return line it credits.").WithWhy(("lineNo", lineNo));
                        }

                        var returnLine = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.ReturnLineId, cancellationToken);
                        var ret = returnLine is null ? null : await db.Returns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == returnLine.ReturnId, cancellationToken);
                        if (returnLine is null || ret is null || ret.CompanyId != request.CompanyId || ret.PartnerId != request.PartnerId)
                        {
                            return Error.Validation("invoice.return_line_unknown", "The return line does not belong to this supplier and company.").WithWhy(("lineNo", lineNo), ("returnLineId", l.ReturnLineId));
                        }

                        if (ret.Status != "posted")
                        {
                            return Error.Conflict("invoice.return_not_posted", "Only posted returns are credited.").WithWhy(("lineNo", lineNo), ("return", ret.Number), ("status", ret.Status));
                        }

                        if (!string.Equals(ret.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A matched line is invoiced in the order's currency.").WithWhy(("lineNo", lineNo), ("orderCurrency", ret.Currency), ("invoiceCurrency", currency.Value.Code));
                        }

                        var creditable = returnLine.Quantity - returnLine.QtyCredited + lines.Where(x => x.ReturnLineId == returnLine.Id).Sum(static x => -x.Quantity);
                        if (l.Quantity > creditable)
                        {
                            return Error.Validation("invoice.return_over_credited", "More is credited than was returned.").WithWhy(("lineNo", lineNo), ("return", ret.Number), ("creditable", creditable), ("quantity", l.Quantity));
                        }

                        var receiptLine = await db.ReceiptLines.AsNoTracking().SingleAsync(x => x.Id == returnLine.ReceiptLineId, cancellationToken);
                        line.ReturnLineId = returnLine.Id;
                        line.ItemId = returnLine.ItemId;
                        line.UomId = returnLine.UomId;
                        line.ExpectedUnitPrice = receiptLine.UnitPrice;
                        break;
                    }

                default:
                    {
                        var role = Shared.Trim(l.AccountRole) ?? AccountRoles.PurchaseExpense;
                        if (AccountRoles.ControlSubledgers.ContainsKey(role))
                        {
                            return Error.Validation("invoice.control_role", "An expense line does not post to a control account.").WithWhy(("lineNo", lineNo), ("role", role));
                        }

                        if (line.Description is null)
                        {
                            return Error.Validation("invoice.description_required", "An expense line says what was bought.").WithWhy(("lineNo", lineNo));
                        }

                        line.AccountRole = role;
                        break;
                    }
            }

            line.NetAmount = Shared.Round(l.Quantity * l.UnitPrice * (1m - l.DiscountPct / 100m), currency.Value);
            line.TaxAmount = 0m;
            lines.Add(line);
        }

        var totalNet = lines.Sum(static x => x.NetAmount);
        var totalTax = lines.Sum(static x => x.TaxAmount);
        var totalWht = 0m;
        if (wht is not null && wht.WithholdAt == WithholdingPoints.Invoice)
        {
            var aboveThreshold = wht.ThresholdAmount is not { } threshold || !string.Equals(wht.ThresholdCurrency, currency.Value.Code, StringComparison.Ordinal) || totalNet >= threshold;
            if (aboveThreshold)
            {
                totalWht = Shared.Round(totalNet * wht.RatePct / 100m, currency.Value);
                var shares = RoundingPolicy.Default.Allocate(new Money(totalWht, currency.Value), lines.Select(static x => x.NetAmount).ToList());
                for (var i = 0; i < lines.Count; i++)
                {
                    lines[i].WhtAmount = shares[i].Amount;
                }
            }
        }

        invoice.Kind = request.Kind;
        invoice.SupplierInvoiceNumber = Shared.Trim(request.SupplierInvoiceNumber);
        invoice.DocumentDate = documentDate;
        invoice.PostingDate = postingDate;
        invoice.Currency = currency.Value.Code;
        invoice.ExchangeRate = rate.Value;
        invoice.PaymentTermsId = request.PaymentTermsId ?? supplier.PaymentTermsId;
        invoice.WhtCodeId = totalWht > 0m ? whtCodeId : null;
        invoice.TotalNet = totalNet;
        invoice.TotalTax = totalTax;
        invoice.TotalWht = totalWht;
        invoice.TotalGross = totalNet + totalTax;
        invoice.TotalPayable = invoice.TotalGross - totalWht;
        invoice.Notes = Shared.Trim(request.Notes);
        invoice.BranchId = request.BranchId;
        invoice.CustomFields = custom.Value;
        invoice.Lines.AddRange(lines);
        var instalments = await InstalmentsAsync(invoice, cancellationToken);
        if (instalments.IsFailure)
        {
            return instalments.Error!;
        }

        invoice.DueDate = instalments.Value.Count > 0 ? instalments.Value[0].DueOn : documentDate;
        return Result.Success();
    }

    private sealed record Instalment(DateOnly DueOn, decimal Amount, DateOnly? DiscountUntil, decimal DiscountPct);

    private async Task<Result<IReadOnlyList<Instalment>>> InstalmentsAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        // A debit note is due at once: it is applied to invoices or refunded, never scheduled.
        if (invoice.PaymentTermsId is not { } termsId || invoice.Kind == "debit_note")
        {
            return new List<Instalment> { new(invoice.DocumentDate, invoice.TotalPayable, null, 0m) };
        }

        var schedule = await partners.ScheduleAsync(invoice.CompanyId, termsId, invoice.DocumentDate, null, invoice.TotalPayable, invoice.Currency, cancellationToken);
        if (schedule.IsFailure)
        {
            return schedule.Error!;
        }

        return schedule.Value.Instalments.Select(i => new Instalment(i.DueOn, i.Amount, schedule.Value.EarlyDiscountUntil, schedule.Value.EarlyDiscountPct)).ToList();
    }

    /// <summary>Three-way (receipt) and two-way (order) matching against the supplier's tolerances, and the duplicate check on the supplier's reference.</summary>
    private async Task<Result<MatchResult>> MatchAsync(Invoice invoice, SupplierTermsInfo supplier, CancellationToken cancellationToken)
    {
        var result = new MatchResult { Id = Guid.CreateVersion7(), InvoiceId = invoice.Id, PriceTolerancePct = supplier.PriceTolerancePct, QtyTolerancePct = supplier.QtyTolerancePct, MatchedAt = clock.UtcNow };
        var details = new List<object>();
        var priceBreach = false;
        var qtyBreach = false;
        foreach (var line in invoice.Lines)
        {
            line.PriceVariancePct = null;
            line.QtyVariance = null;
            if (line.Kind is "expense" or "charge" or "return")
            {
                continue;
            }

            decimal remaining;
            decimal ordered;
            if (line.Kind == "receipt")
            {
                var receiptLine = await db.ReceiptLines.AsNoTracking().SingleAsync(x => x.Id == line.ReceiptLineId, cancellationToken);
                remaining = receiptLine.Quantity - receiptLine.QtyInvoiced - receiptLine.QtyReturned;
                ordered = receiptLine.Quantity;
            }
            else
            {
                var orderLine = await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == line.OrderLineId, cancellationToken);
                remaining = orderLine.Quantity - orderLine.QtyCancelled - orderLine.QtyInvoiced;
                ordered = orderLine.Quantity - orderLine.QtyCancelled;
            }

            var qtyVariance = line.Quantity - remaining;
            var qtyAllowed = ordered * supplier.QtyTolerancePct / 100m;
            var expected = line.ExpectedUnitPrice ?? 0m;
            var actual = line.UnitPrice * (1m - line.DiscountPct / 100m);
            var priceVariancePct = RoundingPolicy.Default.Round(expected == 0m ? (actual == 0m ? 0m : 100m) : (actual - expected) / expected * 100m, 6);
            var priceVarianceAmount = (actual - expected) * line.Quantity;
            line.QtyVariance = qtyVariance > 0m ? qtyVariance : 0m;
            line.PriceVariancePct = priceVariancePct;
            var lineQtyBreach = qtyVariance > qtyAllowed && qtyVariance > 0m;
            var linePriceBreach = Math.Abs(priceVariancePct) > supplier.PriceTolerancePct;
            qtyBreach |= lineQtyBreach;
            priceBreach |= linePriceBreach;
            result.PriceVarianceAmount += priceVarianceAmount;
            result.QtyVariance += line.QtyVariance ?? 0m;
            details.Add(new { lineNo = line.LineNo, kind = line.Kind, expectedUnitPrice = expected, unitPrice = actual, priceVariancePct, priceVarianceAmount, remaining, quantity = line.Quantity, qtyVariance = line.QtyVariance, qtyAllowed, priceBreach = linePriceBreach, qtyBreach = lineQtyBreach });
        }

        var matchedNet = invoice.Lines.Where(static l => l.Kind is not ("expense" or "charge" or "return")).Sum(static l => l.NetAmount);
        result.PriceVariancePct = RoundingPolicy.Default.Round(matchedNet == 0m ? 0m : result.PriceVarianceAmount / (matchedNet - result.PriceVarianceAmount == 0m ? 1m : matchedNet - result.PriceVarianceAmount) * 100m, 6);
        var reference = invoice.SupplierInvoiceNumber is { } given ? given.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) : null;
        var duplicate = reference is not null
            && await db.Invoices.AsNoTracking().AnyAsync(i => i.Id != invoice.Id && i.PartnerId == invoice.PartnerId && i.SupplierInvoiceNumber != null && EF.Functions.ILike(i.SupplierInvoiceNumber, reference, "\\") && LiveStatuses.Contains(i.Status), cancellationToken);
        result.Status = duplicate ? "duplicate_suspect" : priceBreach ? "price_variance" : qtyBreach ? "qty_variance" : "matched";
        result.Details = JsonSerializer.Serialize(details, Shared.Json);
        return result;
    }

    private async Task<Result<decimal>> RateAsync(CompanyInfo company, string currency, DateOnly date, CancellationToken cancellationToken)
    {
        if (string.Equals(currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return 1m;
        }

        var resolved = await rates.ResolveAsync(company.Id, currency, company.FunctionalCurrency.Code, date, RateTypes.Spot, cancellationToken);
        return resolved.IsFailure
            ? Error.Validation("invoice.rate_missing", "No spot rate from the invoice currency to the company's currency on the posting date.").WithWhy(("from", currency), ("to", company.FunctionalCurrency.Code), ("date", date))
            : resolved.Value.Rate.Rate;
    }

    internal async Task<WorkflowSubject> SubjectAsync(Invoice invoice, MatchResult? match, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(invoice.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken);
        var latest = match ?? await db.MatchResults.AsNoTracking().Where(m => m.InvoiceId == invoice.Id).OrderByDescending(static m => m.MatchedAt).FirstOrDefaultAsync(cancellationToken);
        return new WorkflowSubject(DocumentType, invoice.Id, invoice.CompanyId, Display(invoice), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = invoice.TotalGross,
            ["currency"] = invoice.Currency,
            ["amountRc"] = company is null ? invoice.TotalGross : Shared.Round(invoice.TotalGross * invoice.ExchangeRate, company.FunctionalCurrency),
            ["supplierCode"] = partner?.Code,
            ["kind"] = invoice.Kind,
            ["matchStatus"] = latest?.Status ?? "matched",
            ["hasVariance"] = latest is not null && latest.Status != "matched",
            ["hasOverride"] = latest?.OverrideId is not null,
            ["lineCount"] = invoice.Lines.Count,
        });
    }

    public async Task<Invoice?> LoadAsync(Guid id, CancellationToken cancellationToken) => await db.Invoices.AsNoTracking().Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    private static string Display(Invoice invoice) => $"{invoice.Number} · {invoice.TotalGross:0.##} {invoice.Currency}" + (invoice.SupplierInvoiceNumber is null ? string.Empty : $" · {invoice.SupplierInvoiceNumber}");

    private static IReadOnlyList<Guid> SleIds(ReceiptLine line)
    {
        var ids = JsonSerializer.Deserialize<List<Guid>>(line.SleIds, Shared.Json) ?? [];
        return ids.Count > 0 ? ids : line.SleId is { } single ? [single] : [];
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private async Task<(ItemInfo? Item, ItemUomInfo? Uom)> ItemAsync(Guid itemId, Guid uomId, CancellationToken cancellationToken)
    {
        var item = await items.FindAsync(itemId, cancellationToken);
        var uom = item is null ? null : (await items.UomsAsync(itemId, cancellationToken)).FirstOrDefault(u => u.UomId == uomId);
        return (item, uom);
    }

    private async Task<InvoiceSummary> MapAsync(Invoice i, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(i.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(i.CompanyId), cancellationToken);
        var terms = i.PaymentTermsId is { } t ? await db.Database.SqlQuery<string>($"SELECT code AS \"Value\" FROM app.ptr_payment_terms WHERE id = {t}").FirstOrDefaultAsync(cancellationToken) : null;
        var wht = i.WhtCodeId is { } w ? await partners.FindWhtCodeAsync(w, cancellationToken) : null;
        var receiptLineIds = i.Lines.Where(static l => l.ReceiptLineId is not null).Select(static l => l.ReceiptLineId!.Value).ToList();
        var receiptNumbers = receiptLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.ReceiptLines.AsNoTracking() join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id } where receiptLineIds.Contains(l.Id) select new { l.Id, r.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var orderLineIds = i.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).ToList();
        var orderNumbers = orderLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.OrderLines.AsNoTracking() join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id } where orderLineIds.Contains(l.Id) select new { l.Id, o.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var chargeIds = i.Lines.Where(static l => l.LandedCostChargeId is not null).Select(static l => l.LandedCostChargeId!.Value).ToList();
        var chargeDocs = chargeIds.Count == 0 ? new Dictionary<Guid, string>() : await (from c in db.LandedCostCharges.AsNoTracking() join d in db.LandedCosts.AsNoTracking() on new { c.TenantId, Id = c.LandedCostId } equals new { d.TenantId, d.Id } where chargeIds.Contains(c.Id) select new { c.Id, d.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var returnLineIds = i.Lines.Where(static l => l.ReturnLineId is not null).Select(static l => l.ReturnLineId!.Value).ToList();
        var returnNumbers = returnLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.ReturnLines.AsNoTracking() join r in db.Returns.AsNoTracking() on new { l.TenantId, Id = l.ReturnId } equals new { r.TenantId, r.Id } where returnLineIds.Contains(l.Id) select new { l.Id, r.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var lines = new List<InvoiceLineSummary>(i.Lines.Count);
        foreach (var l in i.Lines.OrderBy(static l => l.LineNo))
        {
            var (item, uom) = l.ItemId is { } itemId && l.UomId is { } uomId ? await ItemAsync(itemId, uomId, cancellationToken) : (null, null);
            lines.Add(new InvoiceLineSummary(l.Id, l.LineNo, l.Kind, l.ReceiptLineId, l.ReceiptLineId is { } rl ? receiptNumbers.GetValueOrDefault(rl) : null, l.OrderLineId, l.OrderLineId is { } ol ? orderNumbers.GetValueOrDefault(ol) : null, l.ItemId, item?.Code, item?.Name.Values, l.AccountRole, l.Description,
                l.Quantity, l.UomId, uom?.UomCode, l.UnitPrice, l.DiscountPct, l.NetAmount, l.TaxAmount, l.WhtAmount, l.ExpectedUnitPrice, l.PriceVariancePct, l.QtyVariance, l.DimensionSetId, l.LandedCostChargeId, l.LandedCostChargeId is { } ch ? chargeDocs.GetValueOrDefault(ch) : null,
                l.ReturnLineId, l.ReturnLineId is { } rt ? returnNumbers.GetValueOrDefault(rt) : null,
                l.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null));
        }

        var matches = (await db.MatchResults.AsNoTracking().Where(m => m.InvoiceId == i.Id).OrderByDescending(static m => m.MatchedAt).ToListAsync(cancellationToken))
            .Select(static m => new MatchResultSummary(m.Id, m.Status, m.PriceTolerancePct, m.QtyTolerancePct, m.PriceVarianceAmount, m.PriceVariancePct, m.QtyVariance, Shared.Parse(m.Details), m.OverrideId, m.MatchedAt)).ToList();
        var openItems = await payables.ItemsOfAsync(DocumentType, i.Id, cancellationToken);
        return new InvoiceSummary(i.Id, i.CompanyId, i.Number, i.Kind, i.Status, i.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? Empty(), i.SupplierInvoiceNumber, i.DocumentDate, i.PostingDate, i.DueDate, i.Currency, i.ExchangeRate, company?.FunctionalCurrency.Code ?? i.Currency,
            i.PaymentTermsId, terms, i.WhtCodeId, wht?.Code, i.TotalNet, i.TotalTax, i.TotalWht, i.TotalGross, i.TotalPayable, i.BlockKind, i.BlockReason, i.BlockId, i.ApprovalRequestId, i.RejectionReason, i.JournalEntryId, i.ReversalEntryId, i.ReversalReason, i.Notes, Shared.Parse(i.CustomFields), lines, matches, openItems, i.SubmittedAt, i.PostedAt, i.UpdatedAt);
    }
}

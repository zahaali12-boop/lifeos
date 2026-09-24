using Microsoft.EntityFrameworkCore;
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
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Landed costs (roadmap 4.5, hard scenario 2): charge types; documents whose charges are allocated to posted receipt
/// lines by value, weight, volume or quantity with largest-remainder rounding, then posted through the costing engine
/// (Dr Inventory for what is on hand, the cost adjustment run carrying the sold portion to COGS, Cr the landed-cost
/// clearing account with the document as its subledger item); each allocation keeps the on-hand and sold split. The
/// charge invoices settle the clearing account (invoice line kind <c>charge</c>); a difference between estimate and
/// invoice is allocated the same way. Reversal takes the allocations back through the engine.
/// </summary>
public sealed class LandedCostService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IPartnerDirectory partners,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IInventoryCosting costing,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = PurchaseDocumentTypes.LandedCost;

    public static readonly IReadOnlyList<string> Bases = ["value", "weight", "volume", "quantity"];

    // ------------------------------------------------------------------ charge types

    public async Task<IReadOnlyList<ChargeTypeSummary>> ChargeTypesAsync(CancellationToken cancellationToken) =>
        (await db.ChargeTypes.AsNoTracking().OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<ChargeTypeSummary>> SaveChargeTypeAsync(Guid? id, SaveChargeTypeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Shared.Trim(request.Code)?.ToUpperInvariant();
        if (code is null || code.Length > 20)
        {
            return Error.Validation("charge_type.code_invalid", "A charge type has a code of up to 20 characters.");
        }

        if (!Bases.Contains(request.DefaultAllocationBasis, StringComparer.Ordinal))
        {
            return Error.Validation("charge_type.basis_invalid", "The allocation basis is value, weight, volume or quantity.").WithWhy(("basis", request.DefaultAllocationBasis), ("allowed", Bases));
        }

        var name = new LocalizedText(request.Name ?? new Dictionary<string, string>(StringComparer.Ordinal));
        if (name.Values.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("charge_type.name_required", "A charge type has a name in at least one language.");
        }

        var existing = id is { } i ? await db.ChargeTypes.SingleOrDefaultAsync(c => c.Id == i, cancellationToken) : null;
        if (id is not null && existing is null)
        {
            return Error.NotFound("charge_type", id);
        }

        if (await db.ChargeTypes.AnyAsync(c => c.Code == code && (existing == null || c.Id != existing.Id), cancellationToken))
        {
            return Error.Conflict("charge_type.code_taken", "Another charge type uses this code.").WithWhy(("code", code));
        }

        if (existing is { IsSystem: true } && existing.Code != code)
        {
            return Error.Validation("charge_type.system_locked", "A system charge type keeps its code.").WithWhy(("code", existing.Code));
        }

        var row = existing ?? new ChargeType { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        row.Code = code;
        row.Name = name;
        row.DefaultAllocationBasis = request.DefaultAllocationBasis;
        row.IsActive = request.IsActive;
        row.UpdatedAt = clock.UtcNow;
        if (existing is null)
        {
            db.ChargeTypes.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    // ------------------------------------------------------------------ documents

    public async Task<IReadOnlyList<LandedCostSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.LandedCosts.AsNoTracking().Include(static d => d.Charges).Include(static d => d.Allocations).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(d => d.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var docs = await query.OrderByDescending(static d => d.PostingDate).ThenByDescending(static d => d.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<LandedCostSummary>(docs.Count);
        foreach (var doc in docs)
        {
            result.Add(await MapAsync(doc, cancellationToken));
        }

        return result;
    }

    /// <summary>Posted receipt lines of stock items the company can allocate charges to, with each basis value.</summary>
    public async Task<IReadOnlyList<AllocatableReceiptLine>> AllocatableAsync(Guid companyId, Guid? partnerId, DateOnly? from, CancellationToken cancellationToken)
    {
        var query = from l in db.ReceiptLines.AsNoTracking()
                    join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                    where r.CompanyId == companyId && r.Status == "posted"
                    select new { Line = l, Receipt = r };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.Receipt.PartnerId == p);
        }

        if (from is { } f)
        {
            query = query.Where(x => x.Receipt.PostingDate >= f);
        }

        var rows = await query.OrderByDescending(static x => x.Receipt.PostingDate).ThenBy(static x => x.Receipt.Number).ThenBy(static x => x.Line.LineNo).Take(500).ToListAsync(cancellationToken);
        var result = new List<AllocatableReceiptLine>(rows.Count);
        foreach (var row in rows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(row.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == row.Line.UomId);
            var partner = await partners.FindAsync(row.Receipt.PartnerId, cancellationToken);
            result.Add(new AllocatableReceiptLine(row.Line.Id, row.Receipt.Number, row.Receipt.Id, row.Receipt.PostingDate, row.Receipt.PartnerId, partner?.Code ?? string.Empty, row.Line.LineNo, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(),
                row.Line.Quantity, uom?.UomCode ?? string.Empty, row.Line.QuantityBase, row.Line.ExpectedCostAmount, item?.WeightKg, item?.VolumeM3));
        }

        return result;
    }

    public async Task<Result<LandedCostSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await LoadAsync(id, false, cancellationToken);
        return doc is null ? Error.NotFound(DocumentType, id) : await MapAsync(doc, cancellationToken);
    }

    public async Task<Result<LandedCostSummary>> CreateAsync(SaveLandedCostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = new LandedCostDocument { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(doc, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(doc.CompanyId), "LC", "LC-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(doc.CompanyId), null, doc.PostingDate, doc.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        doc.Number = number.Value.Text;
        db.LandedCosts.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, doc.Id, doc.Number, AuditActions.Created, After: new { doc.Number, doc.TotalAmount, doc.Currency, charges = doc.Charges.Count, receiptLines = request.ReceiptLineIds.Count }, CompanyId: doc.CompanyId), cancellationToken);
        return await MapAsync(doc, cancellationToken);
    }

    public async Task<Result<LandedCostSummary>> UpdateAsync(Guid id, SaveLandedCostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = await LoadAsync(id, true, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (doc.Status != "draft")
        {
            return Error.Conflict("landed_cost.not_draft", "Only a draft landed-cost document is edited.").WithWhy(("status", doc.Status));
        }

        if (request.CompanyId != doc.CompanyId)
        {
            return Error.Validation("landed_cost.company_locked", "A landed-cost document stays in its company.");
        }

        db.LandedCostAllocations.RemoveRange(doc.Allocations);
        db.LandedCostCharges.RemoveRange(doc.Charges);
        doc.Allocations.Clear();
        doc.Charges.Clear();
        var applied = await ApplyAsync(doc, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        doc.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(doc, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await LoadAsync(id, true, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (doc.Status != "draft")
        {
            return Error.Conflict("landed_cost.not_draft", "Only a draft landed-cost document is deleted; a posted one is reversed.").WithWhy(("status", doc.Status));
        }

        db.LandedCosts.Remove(doc);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, doc.Id, doc.Number, AuditActions.Deleted, CompanyId: doc.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Posts the document: every allocation goes onto its receipt entries through the costing engine, which books the on-hand part to stock and pushes the sold part to consumption.</summary>
    public async Task<Result<LandedCostSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var doc = await LoadAsync(id, true, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (doc.Status != "draft")
        {
            return Error.Conflict("landed_cost.not_draft", "Only a draft landed-cost document is posted.").WithWhy(("status", doc.Status));
        }

        var applied = await ApplyAllocationsAsync(doc, +1, doc.PostingDate, $"Landed cost {doc.Number}", "post", cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        doc.Status = "posted";
        doc.PostedAt = clock.UtcNow;
        doc.PostedBy = principal.Principal?.MembershipId.Value;
        doc.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, doc.Id, doc.Number, AuditActions.Posted, After: new { status = doc.Status, doc.TotalAmountFc, doc.OnHandPortionFc, doc.SoldPortionFc }, CompanyId: doc.CompanyId), cancellationToken);
        return await MapAsync(doc, cancellationToken);
    }

    /// <summary>Reverses a posted document while none of its charges has been invoiced: the allocations go back through the engine.</summary>
    public async Task<Result<LandedCostSummary>> ReverseAsync(Guid id, ReverseLandedCostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("landed_cost.reason_required", "A reversal names its reason.");
        }

        var doc = await LoadAsync(id, true, cancellationToken);
        if (doc is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (doc.Status != "posted")
        {
            return Error.Conflict("landed_cost.not_posted", "Only a posted landed-cost document is reversed.").WithWhy(("status", doc.Status));
        }

        if (doc.Charges.Any(static c => !c.IsEstimate))
        {
            return Error.Conflict("landed_cost.settled", "A charge has been invoiced; reverse the charge invoice first.").WithWhy(("charges", doc.Charges.Where(static c => !c.IsEstimate).Select(static c => c.LineNo)));
        }

        var company = await companies.FindAsync(new CompanyId(doc.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < doc.PostingDate)
        {
            return Error.Validation("landed_cost.reversal_date_invalid", "A landed cost is reversed on or after the day it was posted.").WithWhy(("postingDate", doc.PostingDate), ("reversalDate", reversalDate));
        }

        var applied = await ApplyAllocationsAsync(doc, -1, reversalDate, $"Landed cost {doc.Number} reversed: {reason}", "reverse", cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        doc.Status = "reversed";
        doc.ReversalReason = reason;
        doc.ReversedAt = clock.UtcNow;
        doc.ReversedBy = principal.Principal?.MembershipId.Value;
        doc.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, doc.Id, doc.Number, AuditActions.Reversed, After: new { status = doc.Status, reversalDate }, Reason: reason, CompanyId: doc.CompanyId), cancellationToken);
        return await MapAsync(doc, cancellationToken);
    }

    /// <summary>A charge invoice settled this charge at <paramref name="invoicedFc"/>: the difference to the estimate is allocated onto the same receipt lines, in the same proportions.</summary>
    internal async Task<Result> SettleChargeAsync(Guid chargeId, Guid invoiceLineId, decimal invoicedFc, DateOnly postingDate, string reason, int sign, CancellationToken cancellationToken)
    {
        var charge = await db.LandedCostCharges.SingleOrDefaultAsync(c => c.Id == chargeId, cancellationToken);
        var doc = charge is null ? null : await LoadAsync(charge.LandedCostId, true, cancellationToken);
        if (charge is null || doc is null)
        {
            return Error.NotFound("landed_cost_charge", chargeId);
        }

        if (doc.Status != "posted")
        {
            return Error.Conflict("landed_cost.not_posted", "Charges of a posted landed-cost document are what invoices settle.").WithWhy(("document", doc.Number), ("status", doc.Status));
        }

        if (sign > 0 && !charge.IsEstimate)
        {
            return Error.Conflict("landed_cost.charge_settled", "The charge has already been invoiced.").WithWhy(("document", doc.Number), ("lineNo", charge.LineNo));
        }

        var company = await companies.FindAsync(new CompanyId(doc.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", doc.CompanyId);
        }

        var delta = sign > 0 ? invoicedFc - charge.AmountFc : -(charge.InvoicedAmountFc - charge.AmountFc);
        if (delta != 0m)
        {
            var allocations = doc.Allocations.Where(a => a.ChargeId == charge.Id).ToList();
            var shares = RoundingPolicy.Default.Allocate(new Money(delta, company.FunctionalCurrency), allocations.Select(static a => a.AllocatedAmountFc <= 0m ? 1m : a.AllocatedAmountFc).ToList());
            for (var i = 0; i < allocations.Count; i++)
            {
                if (shares[i].Amount == 0m)
                {
                    continue;
                }

                var pushed = await AdjustAsync(allocations[i], shares[i].Amount, doc, postingDate, reason, $"{(sign > 0 ? "settle" : "unsettle")}:{invoiceLineId}", cancellationToken);
                if (pushed.IsFailure)
                {
                    return pushed.Error!;
                }

                allocations[i].AllocatedAmountFc += shares[i].Amount;
                allocations[i].SoldPortionFc += pushed.Value;
                allocations[i].OnHandPortionFc += shares[i].Amount - pushed.Value;
                doc.TotalAmountFc += shares[i].Amount;
                doc.SoldPortionFc += pushed.Value;
                doc.OnHandPortionFc += shares[i].Amount - pushed.Value;
            }
        }

        charge.IsEstimate = sign < 0;
        charge.SupplierInvoiceLineId = sign > 0 ? invoiceLineId : null;
        charge.InvoicedAmountFc = sign > 0 ? invoicedFc : 0m;
        doc.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Charges still estimated (not yet invoiced) of posted documents, for the invoice screen; optionally the ones a supplier is expected to invoice.</summary>
    public async Task<IReadOnlyList<InvoicableLine>> OpenChargesAsync(Guid companyId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var rows = await (from c in db.LandedCostCharges.AsNoTracking()
                          join d in db.LandedCosts.AsNoTracking() on new { c.TenantId, Id = c.LandedCostId } equals new { d.TenantId, d.Id }
                          where d.CompanyId == companyId && d.Status == "posted" && c.IsEstimate && (partnerId == null || c.PartnerId == partnerId)
                          orderby d.PostingDate, d.Number, c.LineNo
                          select new { Charge = c, Doc = d }).ToListAsync(cancellationToken);
        var types = await db.ChargeTypes.AsNoTracking().ToDictionaryAsync(static t => t.Id, cancellationToken);
        return rows.Select(x => new InvoicableLine("charge", null, null, null, null, null, x.Charge.LineNo, null, types.GetValueOrDefault(x.Charge.ChargeTypeId)?.Code ?? string.Empty, types.GetValueOrDefault(x.Charge.ChargeTypeId)?.Name.Values ?? Empty(), null, string.Empty,
            1m, 0m, 1m, x.Charge.Amount, x.Doc.Currency, x.Doc.PostingDate, x.Charge.Id, x.Doc.Number, LandedCostId: x.Doc.Id)).ToList();
    }

    // ------------------------------------------------------------------ internals

    private async Task<LandedCostDocument?> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.LandedCosts.AsQueryable() : db.LandedCosts.AsNoTracking();
        return await query.Include(static d => d.Charges.OrderBy(static c => c.LineNo)).Include(static d => d.Allocations).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    private async Task<Result> ApplyAsync(LandedCostDocument doc, SaveLandedCostRequest request, CancellationToken cancellationToken)
    {
        if (request.Charges is null || request.Charges.Count == 0)
        {
            return Error.Validation("landed_cost.charges_required", "A landed-cost document has at least one charge.");
        }

        if (request.ReceiptLineIds is null || request.ReceiptLineIds.Count == 0)
        {
            return Error.Validation("landed_cost.receipt_lines_required", "Name the receipt lines the charges are allocated to.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("landed_cost.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, company.FunctionalCurrency.Code, "landed_cost", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        var rate = 1m;
        if (!string.Equals(currency.Value.Code, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            var resolved = await rates.ResolveAsync(company.Id, currency.Value.Code, company.FunctionalCurrency.Code, postingDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("landed_cost.rate_missing", "No spot rate from the document currency to the company's currency on the posting date.").WithWhy(("from", currency.Value.Code), ("to", company.FunctionalCurrency.Code), ("date", postingDate));
            }

            rate = resolved.Value.Rate.Rate;
        }

        var ids = request.ReceiptLineIds.Distinct().ToList();
        var receiptLines = await (from l in db.ReceiptLines.AsNoTracking()
                                  join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id }
                                  where ids.Contains(l.Id)
                                  select new { Line = l, Receipt = r }).ToListAsync(cancellationToken);
        if (receiptLines.Count != ids.Count)
        {
            return Error.Validation("landed_cost.receipt_line_unknown", "A receipt line does not exist.").WithWhy(("missing", ids.Except(receiptLines.Select(static x => x.Line.Id))));
        }

        foreach (var x in receiptLines)
        {
            if (x.Receipt.CompanyId != request.CompanyId || x.Receipt.Status != "posted")
            {
                return Error.Conflict("landed_cost.receipt_not_posted", "Charges are allocated to posted receipts of the company.").WithWhy(("receipt", x.Receipt.Number), ("status", x.Receipt.Status));
            }

            if (x.Receipt.PostingDate > postingDate)
            {
                return Error.Validation("landed_cost.before_receipt", "A landed cost is posted on or after the receipts it lands on.").WithWhy(("receipt", x.Receipt.Number), ("receiptDate", x.Receipt.PostingDate), ("postingDate", postingDate));
            }
        }

        var itemInfos = new Dictionary<Guid, ItemInfo?>();
        foreach (var itemId in receiptLines.Select(static x => x.Line.ItemId).Distinct())
        {
            itemInfos[itemId] = await items.FindAsync(itemId, cancellationToken);
        }

        var types = await db.ChargeTypes.AsNoTracking().ToDictionaryAsync(static t => t.Id, cancellationToken);
        var charges = new List<LandedCostCharge>();
        var allocations = new List<LandedCostAllocation>();
        var lineNo = 0;
        foreach (var c in request.Charges)
        {
            lineNo++;
            if (!types.TryGetValue(c.ChargeTypeId, out var type) || !type.IsActive)
            {
                return Error.Validation("landed_cost.charge_type_invalid", "The charge type does not exist or is inactive.").WithWhy(("lineNo", lineNo), ("chargeTypeId", c.ChargeTypeId));
            }

            if (c.Amount <= 0m)
            {
                return Error.Validation("landed_cost.amount_invalid", "A charge amount is positive.").WithWhy(("lineNo", lineNo));
            }

            var basis = Shared.Trim(c.AllocationBasis) ?? type.DefaultAllocationBasis;
            if (!Bases.Contains(basis, StringComparer.Ordinal))
            {
                return Error.Validation("landed_cost.basis_invalid", "The allocation basis is value, weight, volume or quantity.").WithWhy(("lineNo", lineNo), ("basis", basis));
            }

            if (c.PartnerId is { } partnerId && await partners.FindAsync(partnerId, cancellationToken) is null)
            {
                return Error.Validation("landed_cost.partner_unknown", "The charge's supplier does not exist.").WithWhy(("lineNo", lineNo), ("partnerId", partnerId));
            }

            var charge = new LandedCostCharge
            {
                Id = Guid.CreateVersion7(),
                TenantId = doc.TenantId,
                LandedCostId = doc.Id,
                LineNo = lineNo,
                ChargeTypeId = type.Id,
                PartnerId = c.PartnerId,
                Description = Shared.Trim(c.Description),
                Amount = Shared.Round(c.Amount, currency.Value),
                AllocationBasis = basis,
                IsEstimate = true,
                CreatedAt = clock.UtcNow,
            };
            charge.AmountFc = Shared.Round(charge.Amount * rate, company.FunctionalCurrency);
            var weights = new List<decimal>();
            foreach (var x in receiptLines)
            {
                var item = itemInfos[x.Line.ItemId];
                var basisValue = basis switch
                {
                    "value" => x.Line.ExpectedCostAmount,
                    "quantity" => x.Line.QuantityBase,
                    "weight" => (item?.WeightKg ?? 0m) * x.Line.QuantityBase,
                    _ => (item?.VolumeM3 ?? 0m) * x.Line.QuantityBase,
                };
                if (basisValue <= 0m)
                {
                    return Error.Validation("landed_cost.basis_missing", $"A receipt line has no {basis} to allocate by.").WithWhy(("lineNo", lineNo), ("basis", basis), ("receipt", x.Receipt.Number), ("receiptLineNo", x.Line.LineNo), ("item", item?.Code));
                }

                weights.Add(basisValue);
            }

            var shares = RoundingPolicy.Default.Allocate(new Money(charge.AmountFc, company.FunctionalCurrency), weights);
            for (var i = 0; i < receiptLines.Count; i++)
            {
                allocations.Add(new LandedCostAllocation { Id = Guid.CreateVersion7(), TenantId = doc.TenantId, LandedCostId = doc.Id, ChargeId = charge.Id, ReceiptLineId = receiptLines[i].Line.Id, BasisValue = weights[i], AllocatedAmountFc = shares[i].Amount, CreatedAt = clock.UtcNow });
            }

            charges.Add(charge);
        }

        doc.PostingDate = postingDate;
        doc.Currency = currency.Value.Code;
        doc.ExchangeRate = rate;
        doc.Reference = Shared.Trim(request.Reference);
        doc.Notes = Shared.Trim(request.Notes);
        doc.CustomFields = custom.Value;
        doc.TotalAmount = charges.Sum(static c => c.Amount);
        doc.TotalAmountFc = charges.Sum(static c => c.AmountFc);
        doc.OnHandPortionFc = 0m;
        doc.SoldPortionFc = 0m;
        doc.Charges.AddRange(charges);
        doc.Allocations.AddRange(allocations);
        return Result.Success();
    }

    /// <summary>Pushes every allocation (or its reversal) through the engine and records the on-hand and sold split.</summary>
    private async Task<Result> ApplyAllocationsAsync(LandedCostDocument doc, int sign, DateOnly date, string reason, string step, CancellationToken cancellationToken)
    {
        var onHand = 0m;
        var sold = 0m;
        foreach (var allocation in doc.Allocations)
        {
            if (allocation.AllocatedAmountFc == 0m)
            {
                continue;
            }

            var pushed = await AdjustAsync(allocation, sign * allocation.AllocatedAmountFc, doc, date, reason, step, cancellationToken);
            if (pushed.IsFailure)
            {
                return pushed.Error!;
            }

            if (sign > 0)
            {
                allocation.SoldPortionFc = pushed.Value;
                allocation.OnHandPortionFc = allocation.AllocatedAmountFc - pushed.Value;
                onHand += allocation.OnHandPortionFc;
                sold += allocation.SoldPortionFc;
            }
        }

        doc.OnHandPortionFc = sign > 0 ? onHand : 0m;
        doc.SoldPortionFc = sign > 0 ? sold : 0m;
        return Result.Success();
    }

    /// <summary>One landed-cost adjustment on every stock entry of the receipt line (split by quantity for serialised lines); returns what the engine's run carried to consumption.</summary>
    private async Task<Result<decimal>> AdjustAsync(LandedCostAllocation allocation, decimal amountFc, LandedCostDocument doc, DateOnly date, string reason, string step, CancellationToken cancellationToken)
    {
        var receiptLine = await db.ReceiptLines.AsNoTracking().SingleAsync(l => l.Id == allocation.ReceiptLineId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(doc.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", doc.CompanyId);
        }

        var sleIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(receiptLine.SleIds, Shared.Json) ?? [];
        if (sleIds.Count == 0 && receiptLine.SleId is { } single)
        {
            sleIds.Add(single);
        }

        if (sleIds.Count == 0)
        {
            return Error.Conflict("landed_cost.receipt_line_unposted", "The receipt line has no stock entry to carry the cost.").WithWhy(("receiptLineId", receiptLine.Id));
        }

        var shares = RoundingPolicy.Default.Allocate(new Money(amountFc, company.FunctionalCurrency), sleIds.Select(static _ => 1m).ToList());
        var sold = 0m;
        for (var i = 0; i < sleIds.Count; i++)
        {
            if (shares[i].Amount == 0m)
            {
                continue;
            }

            var adjusted = await costing.AdjustInboundCostAsync(new InboundCostAdjustmentRequest(sleIds[i], "landed_cost", DocumentType, doc.Id, null, shares[i].Amount, date, reason, $"landed_cost:{doc.Id}:{allocation.Id}:{sleIds[i]}:{step}"), cancellationToken);
            if (adjusted.IsFailure)
            {
                return adjusted.Error!.WithWhy(("receiptLineId", receiptLine.Id));
            }

            allocation.AdjustmentRunId = adjusted.Value.Run?.Id ?? allocation.AdjustmentRunId;
            sold += adjusted.Value.Run?.AmountAdjusted ?? 0m;
        }

        return sold;
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private static ChargeTypeSummary Map(ChargeType c) => new(c.Id, c.Code, c.Name.Values, c.DefaultAllocationBasis, c.IsSystem, c.IsActive, c.UpdatedAt);

    private async Task<LandedCostSummary> MapAsync(LandedCostDocument d, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(d.CompanyId), cancellationToken);
        var types = await db.ChargeTypes.AsNoTracking().ToDictionaryAsync(static t => t.Id, cancellationToken);
        var charges = new List<LandedCostChargeSummary>(d.Charges.Count);
        foreach (var c in d.Charges.OrderBy(static c => c.LineNo))
        {
            var partner = c.PartnerId is { } p ? await partners.FindAsync(p, cancellationToken) : null;
            charges.Add(new LandedCostChargeSummary(c.Id, c.LineNo, c.ChargeTypeId, types.GetValueOrDefault(c.ChargeTypeId)?.Code ?? string.Empty, c.PartnerId, partner?.Code, c.Description, c.Amount, c.AmountFc, c.AllocationBasis, c.IsEstimate, c.SupplierInvoiceLineId, c.InvoicedAmountFc));
        }

        var receiptLineIds = d.Allocations.Select(static a => a.ReceiptLineId).Distinct().ToList();
        var receiptLines = receiptLineIds.Count == 0 ? [] : await (from l in db.ReceiptLines.AsNoTracking() join r in db.Receipts.AsNoTracking() on new { l.TenantId, Id = l.ReceiptId } equals new { r.TenantId, r.Id } where receiptLineIds.Contains(l.Id) select new { Line = l, r.Number }).ToListAsync(cancellationToken);
        var allocations = new List<LandedCostAllocationSummary>(d.Allocations.Count);
        foreach (var a in d.Allocations.OrderBy(a => d.Charges.FirstOrDefault(c => c.Id == a.ChargeId)?.LineNo ?? 0).ThenBy(static a => a.CreatedAt))
        {
            var charge = d.Charges.First(c => c.Id == a.ChargeId);
            var line = receiptLines.First(x => x.Line.Id == a.ReceiptLineId);
            var item = await items.FindAsync(line.Line.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(line.Line.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == line.Line.UomId);
            allocations.Add(new LandedCostAllocationSummary(a.Id, a.ChargeId, charge.LineNo, types.GetValueOrDefault(charge.ChargeTypeId)?.Code ?? string.Empty, a.ReceiptLineId, line.Number, line.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(), line.Line.Quantity, uom?.UomCode ?? string.Empty, a.BasisValue, a.AllocatedAmountFc, a.OnHandPortionFc, a.SoldPortionFc));
        }

        return new LandedCostSummary(d.Id, d.CompanyId, d.Number, d.Status, d.PostingDate, d.Currency, d.ExchangeRate, company?.FunctionalCurrency.Code ?? d.Currency, d.TotalAmount, d.TotalAmountFc, d.OnHandPortionFc, d.SoldPortionFc, d.Reference, d.Notes, d.ReversalReason, Shared.Parse(d.CustomFields), charges, allocations, d.PostedAt, d.UpdatedAt);
    }
}

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
using Quicker.Workflow.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Purchase requisitions: what a department needs, estimated, submitted through the workflow engine (or approved at
/// once when no definition is active), and turned into purchase order drafts per supplier.
/// </summary>
public sealed class RequisitionService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IPartnerDirectory partners,
    IMemberDirectory members,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IWorkflowEngine workflow,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    PurchaseOrderService orders)
{
    public const string DocumentType = PurchaseDocumentTypes.Requisition;

    public async Task<IReadOnlyList<RequisitionSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Requisitions.Include(static r => r.Lines).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(r => r.Status == s);
        }

        var rows = await query.OrderByDescending(static r => r.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<RequisitionSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<RequisitionSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.Include(static r => r.Lines).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return requisition is null ? null : await MapAsync(requisition, cancellationToken);
    }

    public async Task<Result<RequisitionSummary>> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var requisition = new Requisition
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            RequesterMembershipId = principal.Principal?.MembershipId.Value,
            Currency = company.FunctionalCurrency.Code,
            CreatedBy = principal.Principal?.UserId.Value,
            CreatedAt = clock.UtcNow,
        };
        var applied = await ApplyAsync(requisition, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "REQ-" + company.Code, "REQ-{yyyy}-{seq:5}", "yearly", cancellationToken);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, clock.TodayIn(company.TimeZone), requisition.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        requisition.Number = number.Value.Text;
        db.Requisitions.Add(requisition);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(requisition, cancellationToken);
    }

    public async Task<Result<RequisitionSummary>> UpdateAsync(Guid id, SaveRequisitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null)
        {
            return Error.NotFound("purchase_requisition", id);
        }

        if (requisition.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("requisition.not_draft", "Only a draft (or rejected) requisition can be edited.").WithWhy(("status", requisition.Status));
        }

        if (requisition.CompanyId != request.CompanyId)
        {
            return Error.Conflict("requisition.company_locked", "A requisition cannot move to another company.");
        }

        var company = (await companies.FindAsync(new CompanyId(requisition.CompanyId), cancellationToken))!;
        var applied = await ApplyAsync(requisition, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        requisition.Status = "draft";
        requisition.RejectionReason = null;
        requisition.ApprovalRequestId = null;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(requisition, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Requisition requisition, SaveRequisitionRequest request, CompanyInfo company, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("requisition.lines_required", "A requisition has at least one line.");
        }

        var validated = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var lines = new List<RequisitionLine>();
        var lineNo = 0;
        var total = 0m;
        foreach (var line in request.Lines)
        {
            var entity = new RequisitionLine { Id = Guid.CreateVersion7(), RequisitionId = requisition.Id, LineNo = ++lineNo, Description = Shared.Trim(line.Description), EstimatedPrice = line.EstimatedPrice, WarehouseId = line.WarehouseId, DimensionSetId = line.DimensionSetId, SuggestedSupplierId = line.SuggestedSupplierId };
            if (line.ItemId is not null || !string.IsNullOrWhiteSpace(line.ItemCode))
            {
                var resolved = await Shared.ResolveLineAsync(items, "requisition", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, cancellationToken);
                if (resolved.IsFailure)
                {
                    return resolved.Error!;
                }

                entity.ItemId = resolved.Value.Item.Id;
                entity.Quantity = line.Quantity;
                entity.UomId = resolved.Value.Unit.UomId;
                entity.QuantityBase = resolved.Value.QuantityBase;
            }
            else
            {
                // A free-text line (a service or something not in the item master yet) needs a description and a unit.
                if (entity.Description is null || line.UomId is null || line.Quantity <= 0m)
                {
                    return Error.Validation("requisition.line_invalid", "A line names an item, or a description with a unit and a positive quantity.").WithWhy(("lineNo", lineNo));
                }

                entity.Quantity = line.Quantity;
                entity.UomId = line.UomId.Value;
                entity.QuantityBase = line.Quantity;
            }

            if (line.EstimatedPrice is { } price && price < 0m)
            {
                return Error.Validation("requisition.price_invalid", "An estimated price is zero or more.").WithWhy(("lineNo", lineNo));
            }

            if (line.WarehouseId is { } w)
            {
                var warehouse = await warehouses.FindAsync(w, cancellationToken);
                if (warehouse is null || warehouse.CompanyId != company.Id.Value)
                {
                    return Error.Validation("requisition.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("lineNo", lineNo));
                }
            }

            if (line.SuggestedSupplierId is { } supplier && await partners.FindSupplierAsync(company.Id.Value, supplier, cancellationToken) is null)
            {
                return Error.Validation("requisition.supplier_not_registered", "The suggested supplier is not a supplier of this company.").WithWhy(("lineNo", lineNo), ("partnerId", supplier));
            }

            total += Shared.Round(entity.Quantity * (entity.EstimatedPrice ?? 0m), company.FunctionalCurrency);
            lines.Add(entity);
        }

        requisition.BranchId = request.BranchId;
        requisition.DepartmentValueId = request.DepartmentValueId;
        requisition.NeededBy = request.NeededBy;
        requisition.Justification = Shared.Trim(request.Justification);
        requisition.CustomFields = validated.Value;
        requisition.TotalEstimated = total;
        requisition.Lines.Clear();
        requisition.Lines.AddRange(lines);
        requisition.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Submits: the active definition decides, and without one the requisition is approved at once (ASSUMPTIONS A-108).</summary>
    public async Task<Result<RequisitionSummary>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null)
        {
            return Error.NotFound("purchase_requisition", id);
        }

        if (requisition.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("requisition.not_draft", "Only a draft requisition is submitted.").WithWhy(("status", requisition.Status));
        }

        requisition.SubmittedBy = principal.Principal?.UserId.Value;
        requisition.SubmittedAt = clock.UtcNow;
        requisition.UpdatedAt = clock.UtcNow;
        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(Subject(requisition), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.Pending)
            {
                requisition.Status = "pending_approval";
                requisition.ApprovalRequestId = outcome.Value.RequestId;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(DocumentType, requisition.Id, requisition.Number, AuditActions.StateChanged, After: new { status = requisition.Status, approvalRequestId = requisition.ApprovalRequestId }, CompanyId: requisition.CompanyId), cancellationToken);
                return await MapAsync(requisition, cancellationToken);
            }
        }

        await ApproveCoreAsync(requisition, null, cancellationToken);
        return await MapAsync(requisition, cancellationToken);
    }

    private async Task ApproveCoreAsync(Requisition requisition, string? comment, CancellationToken cancellationToken)
    {
        requisition.Status = "approved";
        requisition.ApprovedAt = clock.UtcNow;
        requisition.ApprovalRequestId = null;
        requisition.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, requisition.Id, requisition.Number, AuditActions.Approved, After: new { status = requisition.Status, totalEstimated = requisition.TotalEstimated }, Reason: comment, CompanyId: requisition.CompanyId), cancellationToken);
    }

    /// <summary>The workflow's decision (ADR-0020).</summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == decision.EntityId, cancellationToken);
        if (requisition is null)
        {
            return Error.NotFound("purchase_requisition", decision.EntityId);
        }

        if (requisition.Status != "pending_approval" || requisition.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("requisition.not_pending", "The requisition is no longer awaiting this approval request.").WithWhy(("status", requisition.Status));
        }

        if (decision.Status == WorkflowDecisions.Approved)
        {
            await ApproveCoreAsync(requisition, decision.Comment, cancellationToken);
            return Result.Success();
        }

        var rejected = decision.Status == WorkflowDecisions.Rejected;
        requisition.Status = rejected ? "rejected" : "draft";
        requisition.RejectionReason = rejected ? decision.Comment ?? "Not approved." : null;
        requisition.ApprovalRequestId = null;
        requisition.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, requisition.Id, requisition.Number, rejected ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status = requisition.Status, reason = requisition.RejectionReason }, CompanyId: requisition.CompanyId), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<RequisitionSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null)
        {
            return Error.NotFound("purchase_requisition", id);
        }

        if (requisition.Status is "ordered" or "cancelled")
        {
            return Error.Conflict("requisition.not_cancellable", "An ordered requisition is closed by its orders.").WithWhy(("status", requisition.Status));
        }

        if (requisition.Lines.Any(static l => l.QtyOrdered > 0m))
        {
            return Error.Conflict("requisition.partly_ordered", "Cancel the orders raised from this requisition first.");
        }

        if (requisition.ApprovalRequestId is not null)
        {
            var withdrawn = await workflow.CancelAsync(DocumentType, requisition.Id, "The requisition was cancelled.", cancellationToken);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }
        }

        requisition.Status = "cancelled";
        requisition.ApprovalRequestId = null;
        requisition.UpdatedAt = clock.UtcNow;
        foreach (var line in requisition.Lines)
        {
            line.Status = "cancelled";
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, requisition.Id, requisition.Number, AuditActions.StateChanged, After: new { status = requisition.Status }, CompanyId: requisition.CompanyId), cancellationToken);
        return await MapAsync(requisition, cancellationToken);
    }

    /// <summary>Purchase order drafts from the open lines of an approved requisition, one per supplier (the suggested supplier, or the one given for all).</summary>
    public async Task<Result<OrdersCreated>> CreateOrdersAsync(Guid id, CreateOrdersFromRequisitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (requisition is null)
        {
            return Error.NotFound("purchase_requisition", id);
        }

        if (requisition.Status != "approved")
        {
            return Error.Conflict("requisition.not_approved", "Only an approved requisition is ordered.").WithWhy(("status", requisition.Status));
        }

        var chosen = requisition.Lines.Where(l => l.Status == "open" && l.QtyOrdered < l.Quantity && (request.LineIds is null || request.LineIds.Contains(l.Id))).ToList();
        if (chosen.Count == 0)
        {
            return Error.Conflict("requisition.nothing_to_order", "Every chosen line is already ordered.");
        }

        if (chosen.Any(static l => l.ItemId is null))
        {
            return Error.Validation("requisition.free_text_line", "Free-text lines are ordered from the purchase order screen with an item.").WithWhy(("lineNos", chosen.Where(static l => l.ItemId is null).Select(static l => l.LineNo)));
        }

        var groups = chosen.GroupBy(l => request.PartnerId ?? l.SuggestedSupplierId).ToList();
        if (groups.Any(static g => g.Key is null))
        {
            return Error.Validation("requisition.supplier_required", "Name the supplier for the lines without a suggested one.").WithWhy(("lineNos", groups.Where(static g => g.Key is null).SelectMany(static g => g.Select(static l => l.LineNo))));
        }

        var created = new List<PurchaseOrderSummary>();
        foreach (var group in groups)
        {
            var lines = group.Select(l => new SavePurchaseOrderLineRequest(l.ItemId, null, null, l.Description, l.Quantity - l.QtyOrdered, null, l.UomId, l.EstimatedPrice ?? 0m, 0m, requisition.NeededBy, l.WarehouseId ?? request.WarehouseId, l.DimensionSetId, l.Id)).ToList();
            var order = await orders.CreateAsync(new SavePurchaseOrderRequest(requisition.CompanyId, group.Key!.Value, lines, null, null, requisition.NeededBy, null, null, request.WarehouseId, null, requisition.BranchId, requisition.Justification), cancellationToken, requisition.Id);
            if (order.IsFailure)
            {
                return order.Error!;
            }

            foreach (var line in group)
            {
                line.QtyOrdered = line.Quantity;
                line.Status = "ordered";
            }

            created.Add(order.Value);
        }

        if (requisition.Lines.All(static l => l.Status is "ordered" or "cancelled"))
        {
            requisition.Status = "ordered";
        }

        requisition.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, requisition.Id, requisition.Number, AuditActions.StateChanged, After: new { status = requisition.Status, orders = created.Select(static o => o.Number) }, CompanyId: requisition.CompanyId), cancellationToken);
        return new OrdersCreated(created);
    }

    /// <summary>Called by the order when it is cancelled: the requisition lines open again.</summary>
    internal async Task ReleaseOrderedAsync(Guid requisitionId, IEnumerable<(Guid LineId, decimal Quantity)> released, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == requisitionId, cancellationToken);
        if (requisition is null)
        {
            return;
        }

        foreach (var (lineId, quantity) in released)
        {
            var line = requisition.Lines.SingleOrDefault(l => l.Id == lineId);
            if (line is null)
            {
                continue;
            }

            line.QtyOrdered = Math.Max(0m, line.QtyOrdered - quantity);
            line.Status = line.QtyOrdered >= line.Quantity ? "ordered" : "open";
        }

        if (requisition.Status == "ordered" && requisition.Lines.Any(static l => l.Status == "open"))
        {
            requisition.Status = "approved";
        }

        requisition.UpdatedAt = clock.UtcNow;
    }

    internal static WorkflowSubject Subject(Requisition r) => new(DocumentType, r.Id, r.CompanyId, $"{r.Number} · {r.Lines.Count} line(s) · {r.TotalEstimated:0.##} {r.Currency}", new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["amount"] = r.TotalEstimated,
        ["currency"] = r.Currency,
        ["lineCount"] = r.Lines.Count,
        ["neededBy"] = r.NeededBy,
        ["department"] = r.DepartmentValueId?.ToString(),
        ["hasWarehouse"] = r.Lines.Any(static l => l.WarehouseId != null),
    });

    internal async Task<Requisition?> LoadAsync(Guid id, CancellationToken cancellationToken) => await db.Requisitions.Include(static r => r.Lines).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    private async Task<RequisitionSummary> MapAsync(Requisition r, CancellationToken cancellationToken)
    {
        var requester = r.RequesterMembershipId is { } m ? await members.FindAsync(new MembershipId(m), cancellationToken) : null;
        var lines = new List<RequisitionLineSummary>(r.Lines.Count);
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = l.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
            var unit = item is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            var supplier = l.SuggestedSupplierId is { } s ? await partners.FindAsync(s, cancellationToken) : null;
            lines.Add(new RequisitionLineSummary(l.Id, l.LineNo, l.ItemId, item?.Code, item?.Name.Values, l.Description, l.Quantity, l.UomId, unit?.UomCode ?? string.Empty, l.QuantityBase, l.EstimatedPrice, l.WarehouseId, l.DimensionSetId, l.SuggestedSupplierId, supplier?.Code, l.QtyOrdered, l.Status));
        }

        return new RequisitionSummary(r.Id, r.CompanyId, r.Number, r.Status, r.RequesterMembershipId, requester?.DisplayName, r.NeededBy, r.Justification, r.Currency, r.TotalEstimated, r.ApprovalRequestId, r.RejectionReason, r.DepartmentValueId, Shared.Parse(r.CustomFields), lines, r.SubmittedAt, r.ApprovedAt, r.UpdatedAt);
    }
}

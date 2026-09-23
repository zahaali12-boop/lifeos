using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Domain;
using Quicker.Inventory.Persistence;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Inventory.Application;

// ------------------------------------------------------------------ reason codes

public sealed record SaveReasonCodeRequest(string Code, IReadOnlyDictionary<string, string> Name, string AppliesTo = "adjustment", string? AccountRoleOverride = null, bool RequiresNote = false, bool IsActive = true);

public sealed record ReasonCodeSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string AppliesTo, string? AccountRoleOverride, bool RequiresNote, bool IsActive, DateTimeOffset UpdatedAt);

/// <summary>Reason codes for adjustments, scrap, counts, returns, write-offs and transfer shortages; a reason may override the account the movement offsets.</summary>
public sealed class ReasonCodeService(InventoryDbContext db, IClock clock)
{
    public static readonly IReadOnlyList<string> AppliesTo = ["adjustment", "count", "return", "scrap", "write_off", "shortage"];

    public static readonly IReadOnlyList<string> OverridableRoles = ["InventoryAdjustment", "CountVariance", "Scrap", "InventoryWriteDown", "Cogs"];

    public async Task<IReadOnlyList<ReasonCodeSummary>> ListAsync(string? appliesTo, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.ReasonCodes.AsQueryable();
        if (!string.IsNullOrWhiteSpace(appliesTo))
        {
            var kind = appliesTo.Trim().ToLowerInvariant();
            query = query.Where(r => r.AppliesTo == kind);
        }

        if (!includeInactive)
        {
            query = query.Where(static r => r.IsActive);
        }

        return (await query.OrderBy(static r => r.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<ReasonCodeSummary>> CreateAsync(SaveReasonCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = new ReasonCode { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(reason, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.ReasonCodes.Add(reason);
        await db.SaveChangesAsync(cancellationToken);
        return Map(reason);
    }

    public async Task<Result<ReasonCodeSummary>> UpdateAsync(Guid id, SaveReasonCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = await db.ReasonCodes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (reason is null)
        {
            return Error.NotFound("reason_code", id);
        }

        var applied = await ApplyAsync(reason, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(reason);
    }

    private async Task<Result> ApplyAsync(ReasonCode reason, SaveReasonCodeRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.UpperCode(request.Code, "reason_code.code");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "reason_code.name");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var applies = Validation.OneOf(request.AppliesTo, "reason_code.applies_to", AppliesTo);
        if (applies.IsFailure)
        {
            return applies.Error!;
        }

        var role = string.IsNullOrWhiteSpace(request.AccountRoleOverride) ? null : request.AccountRoleOverride.Trim();
        if (role is not null && !OverridableRoles.Contains(role, StringComparer.Ordinal))
        {
            return Error.Validation("reason_code.account_role_invalid", "A reason code may only redirect the movement to an inventory expense account.").WithWhy(("accountRoleOverride", role), ("allowed", OverridableRoles));
        }

        if (await db.ReasonCodes.AnyAsync(r => r.Code == code.Value && r.Id != reason.Id, cancellationToken))
        {
            return Error.Conflict("reason_code.code_taken", "A reason code with this code exists.").WithWhy(("code", code.Value));
        }

        reason.Code = code.Value;
        reason.Name = name.Value;
        reason.AppliesTo = applies.Value;
        reason.AccountRoleOverride = role;
        reason.RequiresNote = request.RequiresNote;
        reason.IsActive = request.IsActive;
        reason.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private static ReasonCodeSummary Map(ReasonCode r) => new(r.Id, r.Code, r.Name.Values, r.AppliesTo, r.AccountRoleOverride, r.RequiresNote, r.IsActive, r.UpdatedAt);
}

// ------------------------------------------------------------------ adjustments

public sealed record SaveAdjustmentRequest(
    Guid CompanyId,
    Guid WarehouseId,
    string Kind,
    IReadOnlyList<SaveAdjustmentLineRequest> Lines,
    DateOnly? PostingDate = null,
    string? Reference = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record SaveAdjustmentLineRequest(
    Guid? ItemId = null,
    string? ItemCode = null,
    decimal Quantity = 0m,
    string? Uom = null,
    Guid? UomId = null,
    Guid? VariantId = null,
    Guid? BinId = null,
    decimal? UnitCost = null,
    Guid? ReasonCodeId = null,
    string? ReasonCode = null,
    string? Note = null,
    string? LotNumber = null,
    DateOnly? ExpiresOn = null,
    IReadOnlyList<string>? SerialNumbers = null);

public sealed record RejectRequest(string Reason);

public sealed record AdjustmentSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Kind,
    string Status,
    Guid WarehouseId,
    string WarehouseCode,
    DateOnly PostingDate,
    string? Reference,
    string? Notes,
    JsonElement CustomFields,
    Guid? StockPostingId,
    Guid? JournalEntryId,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? RejectionReason,
    Guid? PostedBy,
    DateTimeOffset? PostedAt,
    IReadOnlyList<AdjustmentLineSummary> Lines,
    DateTimeOffset UpdatedAt);

public sealed record AdjustmentLineSummary(Guid Id, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? VariantId, Guid? BinId, decimal Quantity, Guid UomId, string UomCode, decimal? UnitCost, Guid ReasonCodeId, string ReasonCode, string? Note, decimal? CostAmount, string? LotNumber, DateOnly? ExpiresOn, IReadOnlyList<string> SerialNumbers);

/// <summary>
/// Adjustment documents (positive, negative, scrap, opening): drafted with reason codes, optionally approved (company
/// setting <c>inventory.adjustments.approval</c>), posted through the stock and costing engines with the reason's
/// account override honoured. Negative lines are valued at the applied cost; positive lines at the entered cost or,
/// when none is given, at the item's current cost.
/// </summary>
public sealed class AdjustmentService(
    InventoryDbContext db,
    IInventoryPosting posting,
    ICompanyDirectory companies,
    ICompanySettings settings,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IWorkflowEngine workflow,
    IInventoryCosting costing,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "stock_adjustment";

    public const string ApprovalSetting = "inventory.adjustments.approval";

    public static readonly IReadOnlyList<string> Kinds = ["positive", "negative", "scrap", "opening"];

    public async Task<IReadOnlyList<AdjustmentSummary>> ListAsync(Guid? companyId, string? status, string? kind, Guid? warehouseId, CancellationToken cancellationToken)
    {
        var query = db.Adjustments.Include(static a => a.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(a => a.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(a => a.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim().ToLowerInvariant();
            query = query.Where(a => a.Kind == k);
        }

        if (warehouseId is { } w)
        {
            query = query.Where(a => a.WarehouseId == w);
        }

        var result = new List<AdjustmentSummary>();
        foreach (var adjustment in await query.OrderByDescending(static a => a.Id).Take(200).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(adjustment, cancellationToken));
        }

        return result;
    }

    public async Task<AdjustmentSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return adjustment is null ? null : await MapAsync(adjustment, cancellationToken);
    }

    public async Task<Result<AdjustmentSummary>> CreateAsync(SaveAdjustmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Validation.Scope(principal, InventoryPermissions.AdjustmentManage, request.CompanyId, request.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var adjustment = new Adjustment { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(adjustment, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Adjustments.Add(adjustment);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    public async Task<Result<AdjustmentSummary>> UpdateAsync(Guid id, SaveAdjustmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", id);
        }

        if (adjustment.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("adjustment.not_draft", "Only a draft (or rejected) adjustment can be edited.").WithWhy(("status", adjustment.Status));
        }

        if (adjustment.CompanyId != request.CompanyId)
        {
            return Error.Conflict("adjustment.company_locked", "An adjustment cannot move to another company.");
        }

        var scope = Validation.Scope(principal, InventoryPermissions.AdjustmentManage, request.CompanyId, request.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var applied = await ApplyAsync(adjustment, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        adjustment.Status = "draft";
        adjustment.RejectionReason = null;
        adjustment.ApprovalRequestId = null;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    /// <summary>
    /// Submits: an active workflow definition for stock adjustments decides (auto-approved posts at once, a matching rule
    /// opens a request); without one, the company setting requires a manual approval or the adjustment posts straight away.
    /// </summary>
    public async Task<Result<AdjustmentSummary>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", id);
        }

        if (adjustment.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("adjustment.not_draft", "Only a draft adjustment is submitted.").WithWhy(("status", adjustment.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.AdjustmentManage, adjustment.CompanyId, adjustment.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        adjustment.SubmittedBy = principal.Principal?.UserId.Value;
        adjustment.SubmittedAt = clock.UtcNow;
        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(await SubjectAsync(adjustment, cancellationToken), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.AutoApproved)
            {
                return await PostAsync(adjustment, cancellationToken);
            }

            adjustment.Status = "pending_approval";
            adjustment.ApprovalRequestId = outcome.Value.RequestId;
            adjustment.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.StateChanged, After: new { status = adjustment.Status, approvalRequestId = adjustment.ApprovalRequestId, rule = outcome.Value.RuleName?.Values }, CompanyId: adjustment.CompanyId), cancellationToken);
            return await MapAsync(adjustment, cancellationToken);
        }

        if (!await ApprovalRequiredAsync(adjustment.CompanyId, cancellationToken))
        {
            return await PostAsync(adjustment, cancellationToken);
        }

        adjustment.Status = "pending_approval";
        adjustment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.StateChanged, After: new { status = adjustment.Status }, CompanyId: adjustment.CompanyId), cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    /// <summary>The workflow's decision on a pending adjustment (ADR-0020): an approval posts it under the approver's name, a rejection sends it back with the reason, a cancelled request returns it to draft.</summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == decision.EntityId, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", decision.EntityId);
        }

        if (adjustment.Status != "pending_approval" || adjustment.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("adjustment.not_pending", "The adjustment is no longer awaiting this approval request.").WithWhy(("status", adjustment.Status), ("approvalRequestId", adjustment.ApprovalRequestId));
        }

        if (decision.Status == WorkflowDecisions.Approved)
        {
            adjustment.ApprovedBy = principal.Principal?.UserId.Value;
            adjustment.ApprovedAt = clock.UtcNow;
            await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.Approved, After: new { approvalRequestId = decision.RequestId }, Reason: decision.Comment, CompanyId: adjustment.CompanyId), cancellationToken);
            var posted = await PostCoreAsync(adjustment, cancellationToken);
            return posted.IsFailure ? posted.Error! : Result.Success();
        }

        var rejected = decision.Status == WorkflowDecisions.Rejected;
        adjustment.Status = rejected ? "rejected" : "draft";
        adjustment.RejectionReason = rejected ? decision.Comment ?? "Not approved." : null;
        adjustment.ApprovalRequestId = null;
        adjustment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), rejected ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status = adjustment.Status, reason = adjustment.RejectionReason, decision = decision.Action }, CompanyId: adjustment.CompanyId), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<AdjustmentSummary>> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", id);
        }

        if (adjustment.Status != "pending_approval")
        {
            return Error.Conflict("adjustment.not_pending", "Only an adjustment awaiting approval is approved.").WithWhy(("status", adjustment.Status));
        }

        if (adjustment.ApprovalRequestId is { } approvalRequestId)
        {
            return Error.Conflict("adjustment.decided_by_workflow", "This adjustment is decided through its approval request.").WithWhy(("approvalRequestId", approvalRequestId));
        }

        var actor = principal.Principal?.UserId.Value;
        if (actor is not null && adjustment.SubmittedBy == actor)
        {
            return Error.Forbidden("adjustment.self_approval", "The person who submitted an adjustment cannot approve it.");
        }

        adjustment.ApprovedBy = actor;
        adjustment.ApprovedAt = clock.UtcNow;
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.Approved, CompanyId: adjustment.CompanyId), cancellationToken);
        return await PostAsync(adjustment, cancellationToken);
    }

    public async Task<Result<AdjustmentSummary>> RejectAsync(Guid id, RejectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", id);
        }

        if (adjustment.Status != "pending_approval")
        {
            return Error.Conflict("adjustment.not_pending", "Only an adjustment awaiting approval is rejected.").WithWhy(("status", adjustment.Status));
        }

        if (adjustment.ApprovalRequestId is { } approvalRequestId)
        {
            return Error.Conflict("adjustment.decided_by_workflow", "This adjustment is decided through its approval request.").WithWhy(("approvalRequestId", approvalRequestId));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("adjustment.rejection_reason_required", "A rejection gives its reason.");
        }

        adjustment.Status = "rejected";
        adjustment.RejectionReason = request.Reason.Trim();
        adjustment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.Rejected, After: new { reason = adjustment.RejectionReason }, CompanyId: adjustment.CompanyId), cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    public async Task<Result<AdjustmentSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var adjustment = await db.Adjustments.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (adjustment is null)
        {
            return Error.NotFound("adjustment", id);
        }

        if (adjustment.Status is "posted" or "cancelled")
        {
            return Error.Conflict("adjustment.not_cancellable", "A posted adjustment is corrected by another adjustment, not cancelled.").WithWhy(("status", adjustment.Status));
        }

        if (adjustment.ApprovalRequestId is not null)
        {
            var withdrawn = await workflow.CancelAsync(DocumentType, adjustment.Id, "The adjustment was cancelled.", cancellationToken);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }

            adjustment.ApprovalRequestId = null;
        }

        adjustment.Status = "cancelled";
        adjustment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, Number(adjustment), AuditActions.StateChanged, After: new { status = adjustment.Status }, CompanyId: adjustment.CompanyId), cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    private async Task<Result<AdjustmentSummary>> PostAsync(Adjustment adjustment, CancellationToken cancellationToken)
    {
        var scope = Validation.Scope(principal, InventoryPermissions.AdjustmentPost, adjustment.CompanyId, adjustment.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        return await PostCoreAsync(adjustment, cancellationToken);
    }

    /// <summary>Posts without the caller's post permission: the workflow's approval is the authority (A-106).</summary>
    private async Task<Result<AdjustmentSummary>> PostCoreAsync(Adjustment adjustment, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(adjustment.CompanyId), cancellationToken))!;
        var reasons = await db.ReasonCodes.Where(r => adjustment.Lines.Select(static l => l.ReasonCodeId).Contains(r.Id)).ToDictionaryAsync(static r => r.Id, cancellationToken);
        var entryType = adjustment.Kind switch
        {
            "positive" => StockEntryTypes.PositiveAdjustment,
            "negative" => StockEntryTypes.NegativeAdjustment,
            "scrap" => StockEntryTypes.Scrap,
            _ => StockEntryTypes.Opening,
        };
        var lines = adjustment.Lines.OrderBy(static l => l.LineNo).Select(l => new StockLine(l.ItemId, entryType, l.Quantity, adjustment.WarehouseId, l.UomId, l.VariantId, l.BinId, l.LotId, l.SerialId, SourceLineId: l.Id,
            UnitCost: l.UnitCost, OffsetRoleOverride: reasons.GetValueOrDefault(l.ReasonCodeId)?.AccountRoleOverride, LotNumber: l.LotNumber, ExpiresOn: l.ExpiresOn, SerialNumbers: l.SerialNumbers.Count == 0 ? null : l.SerialNumbers)).ToList();
        var posted = await posting.PostAsync(new StockPostingRequest(adjustment.CompanyId, adjustment.PostingDate, DocumentType, adjustment.Id, lines, $"{DocumentType}:{adjustment.Id:N}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (adjustment.Number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "ADJ-" + company.Code, "ADJ-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, adjustment.PostingDate, adjustment.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            adjustment.Number = allocated.Value.Text;
        }

        adjustment.StockPostingId = posted.Value.PostingId;
        adjustment.JournalEntryId = posted.Value.JournalEntryId;
        adjustment.Status = "posted";
        adjustment.PostedBy = principal.Principal?.UserId.Value;
        adjustment.PostedAt = clock.UtcNow;
        adjustment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, adjustment.Id, adjustment.Number, AuditActions.Posted, After: new { kind = adjustment.Kind, postingDate = adjustment.PostingDate, stockPostingId = adjustment.StockPostingId, journalEntryId = adjustment.JournalEntryId, lines = posted.Value.Entries.Select(static e => new { e.ItemId, e.Quantity, e.CostAmount }) }, CompanyId: adjustment.CompanyId), cancellationToken);
        return await MapAsync(adjustment, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Adjustment adjustment, SaveAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var kind = Validation.OneOf(request.Kind, "adjustment.kind", Kinds);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value || !warehouse.IsActive)
        {
            return Error.Validation("adjustment.warehouse_invalid", "The warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("adjustment.lines_required", "An adjustment has at least one line.");
        }

        var validated = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var applies = kind.Value switch { "scrap" => "scrap", "opening" => "adjustment", _ => "adjustment" };
        var lines = new List<AdjustmentLine>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            var resolved = await ResolveLineAsync(line, kind.Value, applies, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            var entity = resolved.Value;
            entity.AdjustmentId = adjustment.Id;
            entity.LineNo = ++lineNo;
            lines.Add(entity);
        }

        adjustment.WarehouseId = request.WarehouseId;
        adjustment.Kind = kind.Value;
        adjustment.PostingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        adjustment.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        adjustment.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        adjustment.CustomFields = validated.Value;
        adjustment.Lines.Clear();
        adjustment.Lines.AddRange(lines);
        adjustment.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<Result<AdjustmentLine>> ResolveLineAsync(SaveAdjustmentLineRequest line, string kind, string applies, CancellationToken cancellationToken)
    {
        var resolved = await Documents.ResolveItemLineAsync(items, "adjustment", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, line.VariantId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var (item, unit) = resolved.Value;
        if (line.UnitCost is { } cost && cost < 0m)
        {
            return Error.Validation("adjustment.unit_cost_invalid", "A unit cost is zero or more.").WithWhy(("item", item.Code));
        }

        if (kind is "negative" or "scrap" && line.UnitCost is not null)
        {
            return Error.Validation("adjustment.unit_cost_not_allowed", "Stock taken out is valued at its applied cost; a negative or scrap line carries no cost.").WithWhy(("item", item.Code), ("kind", kind));
        }

        var reasonCode = line.ReasonCode?.Trim().ToUpperInvariant();
        var reason = line.ReasonCodeId is { } rid ? await db.ReasonCodes.SingleOrDefaultAsync(r => r.Id == rid, cancellationToken) : reasonCode is null ? null : await db.ReasonCodes.SingleOrDefaultAsync(r => r.Code == reasonCode, cancellationToken);
        if (reason is null)
        {
            return Error.Validation("adjustment.reason_required", "Every adjustment line names an active reason code.").WithWhy(("item", item.Code), ("reasonCode", line.ReasonCode ?? line.ReasonCodeId?.ToString()));
        }

        if (!reason.IsActive || reason.AppliesTo != applies)
        {
            return Error.Validation("adjustment.reason_not_applicable", $"The reason code applies to {reason.AppliesTo}, not to {applies}.").WithWhy(("item", item.Code), ("reasonCode", reason.Code), ("appliesTo", reason.AppliesTo), ("kind", kind));
        }

        if (reason.RequiresNote && string.IsNullOrWhiteSpace(line.Note))
        {
            return Error.Validation("adjustment.note_required", "This reason code requires a note on the line.").WithWhy(("item", item.Code), ("reasonCode", reason.Code));
        }

        return new AdjustmentLine
        {
            Id = Guid.CreateVersion7(),
            ItemId = item.Id,
            VariantId = line.VariantId,
            BinId = line.BinId,
            Quantity = line.Quantity,
            UomId = unit.UomId,
            UnitCost = line.UnitCost,
            ReasonCodeId = reason.Id,
            Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim(),
            LotNumber = string.IsNullOrWhiteSpace(line.LotNumber) ? null : line.LotNumber.Trim(),
            ExpiresOn = line.ExpiresOn,
            SerialNumbers = line.SerialNumbers?.Select(static n => n.Trim()).Where(static n => n.Length > 0).ToList() ?? [],
        };
    }

    /// <summary>
    /// The adjustment as a workflow rule sees it: kind, warehouse, line count, total quantity in base units, the amount at
    /// stake in the functional currency (entered cost, else the current cost of the item in the warehouse) and the reference.
    /// </summary>
    internal async Task<WorkflowSubject> SubjectAsync(Adjustment adjustment, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(adjustment.CompanyId), cancellationToken))!;
        var warehouse = await warehouses.FindAsync(adjustment.WarehouseId, cancellationToken);
        var amount = 0m;
        var quantity = 0m;
        foreach (var line in adjustment.Lines)
        {
            var unit = (await items.UomsAsync(line.ItemId, cancellationToken)).First(u => u.UomId == line.UomId);
            var baseQuantity = unit.Denominator == 0m ? line.Quantity : line.Quantity * unit.Numerator / unit.Denominator;
            quantity += Math.Abs(baseQuantity);
            if (line.UnitCost is { } enteredCost)
            {
                amount += Math.Abs(line.Quantity * enteredCost);
            }
            else
            {
                var cost = await costing.CostAsync(adjustment.CompanyId, line.ItemId, adjustment.WarehouseId, adjustment.PostingDate, cancellationToken);
                amount += Math.Abs(baseQuantity * (cost?.AverageUnitCost ?? 0m));
            }
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = adjustment.Kind,
            ["warehouseCode"] = warehouse?.Code,
            ["lineCount"] = adjustment.Lines.Count,
            ["quantity"] = quantity,
            ["amount"] = RoundingPolicy.Default.Round(amount, company.FunctionalCurrency.MinorUnits),
            ["currency"] = company.FunctionalCurrency.Code,
            ["reference"] = adjustment.Reference,
            ["postingDate"] = adjustment.PostingDate,
        };
        return new WorkflowSubject(DocumentType, adjustment.Id, adjustment.CompanyId, $"{Number(adjustment)} · {adjustment.Kind} · {warehouse?.Code}", values);
    }

    private async Task<bool> ApprovalRequiredAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(companyId, ApprovalSetting, cancellationToken);
        return value is { ValueKind: JsonValueKind.String } v && string.Equals(v.GetString(), "required", StringComparison.OrdinalIgnoreCase);
    }

    private static string Number(Adjustment a) => a.Number ?? DraftIdentifiers.For(a.Id);

    private async Task<AdjustmentSummary> MapAsync(Adjustment a, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.FindAsync(a.WarehouseId, cancellationToken);
        var reasons = await db.ReasonCodes.Where(r => a.Lines.Select(static l => l.ReasonCodeId).Contains(r.Id)).ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        var costs = a.StockPostingId is { } postingId
            ? await db.Entries.Where(e => e.PostingId == postingId).Join(db.ValueEntries.Where(static v => v.AccountRole == "Inventory" || v.AccountRole == "InventoryInTransit"), static e => e.Id, static v => v.SleId, static (e, v) => new { e.SourceLineId, v.CostAmountActual, v.CostAmountExpected })
                .GroupBy(static x => x.SourceLineId).Select(static g => new { LineId = g.Key, Amount = g.Sum(static x => x.CostAmountActual + x.CostAmountExpected) }).ToDictionaryAsync(static x => x.LineId ?? Guid.Empty, static x => x.Amount, cancellationToken)
            : new Dictionary<Guid, decimal>();
        var lines = new List<AdjustmentLineSummary>(a.Lines.Count);
        foreach (var l in a.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ItemId, cancellationToken))!;
            var unit = (await items.UomsAsync(l.ItemId, cancellationToken)).First(u => u.UomId == l.UomId);
            lines.Add(new AdjustmentLineSummary(l.Id, l.LineNo, item.Id, item.Code, item.Name.Values, l.VariantId, l.BinId, ItemUomMath.Normalize(l.Quantity), l.UomId, unit.UomCode, l.UnitCost, l.ReasonCodeId, reasons.GetValueOrDefault(l.ReasonCodeId) ?? string.Empty, l.Note,
                costs.TryGetValue(l.Id, out var amount) ? amount : null, l.LotNumber, l.ExpiresOn, l.SerialNumbers));
        }

        return new AdjustmentSummary(a.Id, a.CompanyId, Number(a), a.Kind, a.Status, a.WarehouseId, warehouse?.Code ?? string.Empty, a.PostingDate, a.Reference, a.Notes, JsonDocument.Parse(a.CustomFields).RootElement.Clone(),
            a.StockPostingId, a.JournalEntryId, a.SubmittedBy, a.SubmittedAt, a.ApprovedBy, a.ApprovedAt, a.RejectionReason, a.PostedBy, a.PostedAt, lines, a.UpdatedAt);
    }
}

// ------------------------------------------------------------------ revaluations

public sealed record SaveRevaluationRequest(Guid CompanyId, string Kind, IReadOnlyList<SaveRevaluationLineRequest> Lines, DateOnly? PostingDate = null, string? Reference = null, string? Notes = null);

public sealed record SaveRevaluationLineRequest(Guid? ItemId = null, string? ItemCode = null, Guid? WarehouseId = null, decimal NewUnitCost = 0m, string? Note = null);

public sealed record RevaluationSummary(Guid Id, Guid CompanyId, string Number, string Kind, string Status, DateOnly PostingDate, string? Reference, string? Notes, IReadOnlyList<Guid> RunIds, Guid? PostedBy, DateTimeOffset? PostedAt, IReadOnlyList<RevaluationLineSummary> Lines, decimal TotalAmount, DateTimeOffset UpdatedAt);

public sealed record RevaluationLineSummary(Guid Id, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? WarehouseId, decimal Quantity, decimal CurrentUnitCost, decimal NewUnitCost, decimal Amount, string? Note);

/// <summary>NRV write-downs (IAS 2: to net realisable value, reversals capped at cost) and manual revaluations of the stock on hand at a date, posted through the costing engine.</summary>
public sealed class RevaluationService(
    InventoryDbContext db,
    CostingService costing,
    ICompanyDirectory companies,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "stock_revaluation";

    public static readonly IReadOnlyList<string> Kinds = ["nrv_writedown", "manual"];

    public async Task<IReadOnlyList<RevaluationSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Revaluations.Include(static r => r.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(r => r.Status == s);
        }

        var result = new List<RevaluationSummary>();
        foreach (var revaluation in await query.OrderByDescending(static r => r.Id).Take(200).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(revaluation, cancellationToken));
        }

        return result;
    }

    public async Task<RevaluationSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var revaluation = await db.Revaluations.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return revaluation is null ? null : await MapAsync(revaluation, cancellationToken);
    }

    public async Task<Result<RevaluationSummary>> CreateAsync(SaveRevaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Validation.Scope(principal, InventoryPermissions.CostingManage, request.CompanyId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var revaluation = new Revaluation { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(revaluation, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Revaluations.Add(revaluation);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(revaluation, cancellationToken);
    }

    public async Task<Result<RevaluationSummary>> UpdateAsync(Guid id, SaveRevaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var revaluation = await db.Revaluations.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (revaluation is null)
        {
            return Error.NotFound("revaluation", id);
        }

        if (revaluation.Status != "draft")
        {
            return Error.Conflict("revaluation.not_draft", "Only a draft revaluation can be edited.").WithWhy(("status", revaluation.Status));
        }

        if (revaluation.CompanyId != request.CompanyId)
        {
            return Error.Conflict("revaluation.company_locked", "A revaluation cannot move to another company.");
        }

        var applied = await ApplyAsync(revaluation, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(revaluation, cancellationToken);
    }

    public async Task<Result<RevaluationSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var revaluation = await db.Revaluations.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (revaluation is null)
        {
            return Error.NotFound("revaluation", id);
        }

        if (revaluation.Status != "draft")
        {
            return Error.Conflict("revaluation.not_draft", "Only a draft revaluation is posted.").WithWhy(("status", revaluation.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.CostingManage, revaluation.CompanyId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(revaluation.CompanyId), cancellationToken))!;
        var runs = new List<Guid>();
        foreach (var line in revaluation.Lines.OrderBy(static l => l.LineNo))
        {
            var revalued = await costing.RevalueAsync(revaluation.CompanyId, line.ItemId, line.WarehouseId, revaluation.PostingDate, line.NewUnitCost, DocumentType, revaluation.Id, line.Note ?? revaluation.Notes, revaluation.Kind == "nrv_writedown", cancellationToken);
            if (revalued.IsFailure)
            {
                return revalued.Error!.WithWhy(("lineNo", line.LineNo));
            }

            line.Quantity = revalued.Value.Quantity;
            line.CurrentUnitCost = revalued.Value.CurrentUnitCost;
            line.Amount = revalued.Value.Amount;
            if (revalued.Value.RunId is { } run)
            {
                runs.Add(run);
            }
        }

        if (revaluation.Number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "RVL-" + company.Code, "RVL-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, revaluation.PostingDate, revaluation.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            revaluation.Number = allocated.Value.Text;
        }

        revaluation.RunIds = runs;
        revaluation.Status = "posted";
        revaluation.PostedBy = principal.Principal?.UserId.Value;
        revaluation.PostedAt = clock.UtcNow;
        revaluation.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, revaluation.Id, revaluation.Number, AuditActions.Posted, After: new { kind = revaluation.Kind, postingDate = revaluation.PostingDate, lines = revaluation.Lines.Select(static l => new { l.ItemId, l.Quantity, l.CurrentUnitCost, l.NewUnitCost, l.Amount }), runs }, CompanyId: revaluation.CompanyId), cancellationToken);
        return await MapAsync(revaluation, cancellationToken);
    }

    public async Task<Result<RevaluationSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var revaluation = await db.Revaluations.Include(static r => r.Lines).SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (revaluation is null)
        {
            return Error.NotFound("revaluation", id);
        }

        if (revaluation.Status != "draft")
        {
            return Error.Conflict("revaluation.not_draft", "Only a draft revaluation is cancelled.").WithWhy(("status", revaluation.Status));
        }

        revaluation.Status = "cancelled";
        revaluation.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(revaluation, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Revaluation revaluation, SaveRevaluationRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var kind = Validation.OneOf(request.Kind, "revaluation.kind", Kinds);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("revaluation.lines_required", "A revaluation has at least one line.");
        }

        var lines = new List<RevaluationLine>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            var code = line.ItemCode?.Trim();
            var item = line.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : code is null ? null : await items.FindByCodeAsync(code, cancellationToken);
            if (item is null)
            {
                return Error.Validation("revaluation.item_unknown", "The item does not exist.").WithWhy(("item", line.ItemCode ?? line.ItemId?.ToString()));
            }

            if (line.NewUnitCost < 0m)
            {
                return Error.Validation("revaluation.unit_cost_invalid", "The new unit cost is zero or more.").WithWhy(("item", item.Code));
            }

            if (line.WarehouseId is { } wid)
            {
                var warehouse = await warehouses.FindAsync(wid, cancellationToken);
                if (warehouse is null || warehouse.CompanyId != company.Id.Value)
                {
                    return Error.Validation("revaluation.warehouse_invalid", "The warehouse must belong to the company.").WithWhy(("warehouseId", wid));
                }
            }
            else if (company.CostingScope == "warehouse")
            {
                return Error.Validation("revaluation.warehouse_required", "The company costs per warehouse; every line names its warehouse.").WithWhy(("item", item.Code));
            }

            lines.Add(new RevaluationLine { Id = Guid.CreateVersion7(), RevaluationId = revaluation.Id, LineNo = ++lineNo, ItemId = item.Id, WarehouseId = line.WarehouseId, NewUnitCost = line.NewUnitCost, Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim() });
        }

        revaluation.Kind = kind.Value;
        revaluation.PostingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        revaluation.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        revaluation.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        revaluation.Lines.Clear();
        revaluation.Lines.AddRange(lines);
        revaluation.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<RevaluationSummary> MapAsync(Revaluation r, CancellationToken cancellationToken)
    {
        var lines = new List<RevaluationLineSummary>(r.Lines.Count);
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ItemId, cancellationToken))!;
            lines.Add(new RevaluationLineSummary(l.Id, l.LineNo, item.Id, item.Code, item.Name.Values, l.WarehouseId, ItemUomMath.Normalize(l.Quantity), l.CurrentUnitCost, l.NewUnitCost, l.Amount, l.Note));
        }

        return new RevaluationSummary(r.Id, r.CompanyId, r.Number ?? DraftIdentifiers.For(r.Id), r.Kind, r.Status, r.PostingDate, r.Reference, r.Notes, r.RunIds, r.PostedBy, r.PostedAt, lines, lines.Sum(static l => l.Amount), r.UpdatedAt);
    }
}

// ------------------------------------------------------------------ assemblies

public sealed record SaveAssemblyRequest(
    Guid CompanyId,
    Guid WarehouseId,
    decimal OutputQuantity,
    Guid? OutputItemId = null,
    string? OutputItemCode = null,
    string? OutputUom = null,
    Guid? OutputUomId = null,
    Guid? OutputVariantId = null,
    Guid? OutputBinId = null,
    string? OutputLotNumber = null,
    DateOnly? OutputExpiresOn = null,
    IReadOnlyList<string>? OutputSerialNumbers = null,
    IReadOnlyList<SaveAssemblyLineRequest>? Lines = null,
    DateOnly? PostingDate = null,
    string? Reference = null,
    string? Notes = null);

public sealed record SaveAssemblyLineRequest(Guid? ItemId = null, string? ItemCode = null, decimal Quantity = 0m, string? Uom = null, Guid? UomId = null, Guid? VariantId = null, Guid? BinId = null, string? LotNumber = null, IReadOnlyList<string>? SerialNumbers = null);

public sealed record AssemblySummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid? BomId,
    Guid OutputItemId,
    string OutputItemCode,
    IReadOnlyDictionary<string, string> OutputItemName,
    Guid? OutputVariantId,
    decimal OutputQuantity,
    Guid OutputUomId,
    string OutputUomCode,
    Guid? OutputBinId,
    Guid WarehouseId,
    string WarehouseCode,
    DateOnly PostingDate,
    string? Reference,
    string? Notes,
    Guid? StockPostingId,
    Guid? JournalEntryId,
    decimal? OutputCost,
    Guid? PostedBy,
    DateTimeOffset? PostedAt,
    IReadOnlyList<AssemblyLineSummary> Lines,
    DateTimeOffset UpdatedAt);

public sealed record AssemblyLineSummary(Guid Id, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? VariantId, decimal Quantity, Guid UomId, string UomCode, Guid? BinId, decimal? CostAmount, string? LotNumber, IReadOnlyList<string> SerialNumbers);

/// <summary>
/// Assembly builds: the components of the item's active bill (or the lines given) are consumed and the assembly item
/// produced in one stock posting; the costing engine values the output at what the components cost (POSTING_RULES §4).
/// </summary>
public sealed class AssemblyService(
    InventoryDbContext db,
    IInventoryPosting posting,
    ICompanyDirectory companies,
    IItemDirectory items,
    IBomDirectory boms,
    IWarehouseDirectory warehouses,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "stock_assembly";

    public async Task<IReadOnlyList<AssemblySummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Assemblies.Include(static a => a.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(a => a.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(a => a.Status == s);
        }

        var result = new List<AssemblySummary>();
        foreach (var assembly in await query.OrderByDescending(static a => a.Id).Take(200).ToListAsync(cancellationToken))
        {
            result.Add(await MapAsync(assembly, cancellationToken));
        }

        return result;
    }

    public async Task<AssemblySummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var assembly = await db.Assemblies.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return assembly is null ? null : await MapAsync(assembly, cancellationToken);
    }

    public async Task<Result<AssemblySummary>> CreateAsync(SaveAssemblyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Validation.Scope(principal, InventoryPermissions.AssemblyManage, request.CompanyId, request.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var assembly = new Assembly { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(assembly, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Assemblies.Add(assembly);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(assembly, cancellationToken);
    }

    public async Task<Result<AssemblySummary>> UpdateAsync(Guid id, SaveAssemblyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var assembly = await db.Assemblies.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assembly is null)
        {
            return Error.NotFound("assembly", id);
        }

        if (assembly.Status != "draft")
        {
            return Error.Conflict("assembly.not_draft", "Only a draft assembly can be edited.").WithWhy(("status", assembly.Status));
        }

        if (assembly.CompanyId != request.CompanyId)
        {
            return Error.Conflict("assembly.company_locked", "An assembly cannot move to another company.");
        }

        var scope = Validation.Scope(principal, InventoryPermissions.AssemblyManage, request.CompanyId, request.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var applied = await ApplyAsync(assembly, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(assembly, cancellationToken);
    }

    public async Task<Result<AssemblySummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var assembly = await db.Assemblies.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assembly is null)
        {
            return Error.NotFound("assembly", id);
        }

        if (assembly.Status != "draft")
        {
            return Error.Conflict("assembly.not_draft", "Only a draft assembly is posted.").WithWhy(("status", assembly.Status));
        }

        var scope = Validation.Scope(principal, InventoryPermissions.AssemblyPost, assembly.CompanyId, assembly.WarehouseId);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(assembly.CompanyId), cancellationToken))!;
        var lines = assembly.Lines.OrderBy(static l => l.LineNo)
            .Select(l => new StockLine(l.ComponentItemId, StockEntryTypes.AssemblyConsumption, l.Quantity, assembly.WarehouseId, l.UomId, l.ComponentVariantId, l.BinId, l.LotId, l.SerialId, SourceLineId: l.Id, LotNumber: l.LotNumber, SerialNumbers: l.SerialNumbers.Count == 0 ? null : l.SerialNumbers))
            .Append(new StockLine(assembly.OutputItemId, StockEntryTypes.AssemblyOutput, assembly.OutputQty, assembly.WarehouseId, assembly.OutputUomId, assembly.OutputVariantId, assembly.OutputBinId, LotNumber: assembly.OutputLotNumber, ExpiresOn: assembly.OutputExpiresOn, SerialNumbers: assembly.OutputSerialNumbers.Count == 0 ? null : assembly.OutputSerialNumbers))
            .ToList();
        var posted = await posting.PostAsync(new StockPostingRequest(assembly.CompanyId, assembly.PostingDate, DocumentType, assembly.Id, lines, $"{DocumentType}:{assembly.Id:N}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (assembly.Number is null)
        {
            var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "ASM-" + company.Code, "ASM-{yyyy}-{seq:5}", "yearly", cancellationToken);
            if (series.IsFailure)
            {
                return series.Error!;
            }

            var allocated = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, assembly.PostingDate, assembly.Id), cancellationToken);
            if (allocated.IsFailure)
            {
                return allocated.Error!;
            }

            assembly.Number = allocated.Value.Text;
        }

        assembly.StockPostingId = posted.Value.PostingId;
        assembly.JournalEntryId = posted.Value.JournalEntryId;
        assembly.Status = "posted";
        assembly.PostedBy = principal.Principal?.UserId.Value;
        assembly.PostedAt = clock.UtcNow;
        assembly.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, assembly.Id, assembly.Number, AuditActions.Posted, After: new { postingDate = assembly.PostingDate, output = new { assembly.OutputItemId, assembly.OutputQty }, stockPostingId = assembly.StockPostingId, journalEntryId = assembly.JournalEntryId, entries = posted.Value.Entries.Select(static e => new { e.ItemId, e.EntryType, e.Quantity, e.CostAmount }) }, CompanyId: assembly.CompanyId), cancellationToken);
        return await MapAsync(assembly, cancellationToken);
    }

    public async Task<Result<AssemblySummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var assembly = await db.Assemblies.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (assembly is null)
        {
            return Error.NotFound("assembly", id);
        }

        if (assembly.Status != "draft")
        {
            return Error.Conflict("assembly.not_draft", "Only a draft assembly is cancelled.").WithWhy(("status", assembly.Status));
        }

        assembly.Status = "cancelled";
        assembly.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(assembly, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Assembly assembly, SaveAssemblyRequest request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var warehouse = await warehouses.FindAsync(request.WarehouseId, cancellationToken);
        if (warehouse is null || warehouse.CompanyId != company.Id.Value || !warehouse.IsActive || warehouse.Kind == "in_transit")
        {
            return Error.Validation("assembly.warehouse_invalid", "The warehouse must be an active stock warehouse of the company.").WithWhy(("warehouseId", request.WarehouseId));
        }

        var output = await Documents.ResolveItemLineAsync(items, "assembly", request.OutputItemId, request.OutputItemCode, request.OutputQuantity, request.OutputUom, request.OutputUomId, request.OutputVariantId, cancellationToken);
        if (output.IsFailure)
        {
            return output.Error!;
        }

        var (outputItem, outputUnit) = output.Value;
        if (outputItem.Type != "assembly")
        {
            return Error.Validation("assembly.item_not_assembly", "Only an assembly item is built.").WithWhy(("item", outputItem.Code), ("type", outputItem.Type));
        }

        var lines = new List<AssemblyLine>();
        var lineNo = 0;
        Guid? bomId = null;
        if (request.Lines is { Count: > 0 })
        {
            foreach (var line in request.Lines)
            {
                var resolved = await Documents.ResolveItemLineAsync(items, "assembly", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, line.VariantId, cancellationToken);
                if (resolved.IsFailure)
                {
                    return resolved.Error!;
                }

                var (item, unit) = resolved.Value;
                if (item.Id == outputItem.Id)
                {
                    return Error.Validation("assembly.component_is_output", "An assembly does not consume itself.").WithWhy(("item", item.Code));
                }

                lines.Add(new AssemblyLine { Id = Guid.CreateVersion7(), AssemblyId = assembly.Id, LineNo = ++lineNo, ComponentItemId = item.Id, ComponentVariantId = line.VariantId, Quantity = line.Quantity, UomId = unit.UomId, BinId = line.BinId, LotNumber = string.IsNullOrWhiteSpace(line.LotNumber) ? null : line.LotNumber.Trim(), SerialNumbers = line.SerialNumbers?.Select(static n => n.Trim()).Where(static n => n.Length > 0).ToList() ?? [] });
            }
        }
        else
        {
            // No lines given: the active bill of material decides, in base units with the scrap allowance.
            var baseOutput = await items.ToBaseAsync(outputItem.Id, outputUnit.UomId, request.OutputQuantity, cancellationToken);
            if (baseOutput.IsFailure)
            {
                return baseOutput.Error!;
            }

            var build = await boms.BuildAsync(outputItem.Id, baseOutput.Value.Quantity, cancellationToken);
            if (build.IsFailure)
            {
                return build.Error!;
            }

            if (build.Value is null)
            {
                return Error.Validation("assembly.bom_required", "The item has no active assembly bill of material; give the component lines.").WithWhy(("item", outputItem.Code));
            }

            bomId = build.Value.BomId;
            foreach (var component in build.Value.Components)
            {
                lines.Add(new AssemblyLine { Id = Guid.CreateVersion7(), AssemblyId = assembly.Id, LineNo = ++lineNo, ComponentItemId = component.ComponentItemId, ComponentVariantId = component.ComponentVariantId, Quantity = component.QuantityWithScrap, UomId = component.BaseUomId });
            }
        }

        if (lines.Count == 0)
        {
            return Error.Validation("assembly.lines_required", "An assembly consumes at least one component.");
        }

        assembly.BomId = bomId;
        assembly.OutputItemId = outputItem.Id;
        assembly.OutputVariantId = request.OutputVariantId;
        assembly.OutputQty = request.OutputQuantity;
        assembly.OutputUomId = outputUnit.UomId;
        assembly.OutputBinId = request.OutputBinId;
        assembly.OutputLotNumber = string.IsNullOrWhiteSpace(request.OutputLotNumber) ? null : request.OutputLotNumber.Trim();
        assembly.OutputExpiresOn = request.OutputExpiresOn;
        assembly.OutputSerialNumbers = request.OutputSerialNumbers?.Select(static n => n.Trim()).Where(static n => n.Length > 0).ToList() ?? [];
        assembly.WarehouseId = request.WarehouseId;
        assembly.PostingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        assembly.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        assembly.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        assembly.Lines.Clear();
        assembly.Lines.AddRange(lines);
        assembly.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<AssemblySummary> MapAsync(Assembly a, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.FindAsync(a.WarehouseId, cancellationToken);
        var outputItem = (await items.FindAsync(a.OutputItemId, cancellationToken))!;
        var outputUnit = (await items.UomsAsync(a.OutputItemId, cancellationToken)).First(u => u.UomId == a.OutputUomId);
        Dictionary<Guid, decimal> costs = new();
        decimal? outputCost = null;
        if (a.StockPostingId is { } postingId)
        {
            var valued = await db.Entries.Where(e => e.PostingId == postingId).Join(db.ValueEntries.Where(static v => v.AccountRole == "Inventory" || v.AccountRole == "InventoryInTransit"), static e => e.Id, static v => v.SleId, static (e, v) => new { e.SourceLineId, e.EntryType, v.CostAmountActual, v.CostAmountExpected }).ToListAsync(cancellationToken);
            costs = valued.Where(static v => v.SourceLineId is not null).GroupBy(static v => v.SourceLineId!.Value).ToDictionary(static g => g.Key, static g => g.Sum(static v => v.CostAmountActual + v.CostAmountExpected));
            outputCost = valued.Where(static v => v.EntryType == StockEntryTypes.AssemblyOutput).Sum(static v => v.CostAmountActual + v.CostAmountExpected);
        }

        var lines = new List<AssemblyLineSummary>(a.Lines.Count);
        foreach (var l in a.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ComponentItemId, cancellationToken))!;
            var unit = (await items.UomsAsync(l.ComponentItemId, cancellationToken)).First(u => u.UomId == l.UomId);
            lines.Add(new AssemblyLineSummary(l.Id, l.LineNo, item.Id, item.Code, item.Name.Values, l.ComponentVariantId, ItemUomMath.Normalize(l.Quantity), l.UomId, unit.UomCode, l.BinId, costs.TryGetValue(l.Id, out var amount) ? amount : null, l.LotNumber, l.SerialNumbers));
        }

        return new AssemblySummary(a.Id, a.CompanyId, a.Number ?? DraftIdentifiers.For(a.Id), a.Status, a.BomId, outputItem.Id, outputItem.Code, outputItem.Name.Values, a.OutputVariantId, ItemUomMath.Normalize(a.OutputQty), a.OutputUomId, outputUnit.UomCode, a.OutputBinId,
            a.WarehouseId, warehouse?.Code ?? string.Empty, a.PostingDate, a.Reference, a.Notes, a.StockPostingId, a.JournalEntryId, outputCost, a.PostedBy, a.PostedAt, lines, a.UpdatedAt);
    }
}

/// <summary>Line resolution shared by the stock documents: the item by id or code, its unit, the exact base quantity, the variant.</summary>
internal static class Documents
{
    public static async Task<Result<(ItemInfo Item, ItemUomInfo Unit)>> ResolveItemLineAsync(IItemDirectory items, string prefix, Guid? itemId, string? itemCode, decimal quantity, string? uom, Guid? uomId, Guid? variantId, CancellationToken cancellationToken)
    {
        var code = itemCode?.Trim();
        var item = itemId is { } id ? await items.FindAsync(id, cancellationToken) : code is null ? null : await items.FindByCodeAsync(code, cancellationToken);
        if (item is null)
        {
            return Error.Validation($"{prefix}.item_unknown", "The item does not exist.").WithWhy(("item", itemCode ?? itemId?.ToString()));
        }

        if (item.Type is not ("stock" or "assembly"))
        {
            return Error.Validation($"{prefix}.item_not_stocked", "Only stock and assembly items move.").WithWhy(("item", item.Code), ("type", item.Type));
        }

        if (quantity <= 0m)
        {
            return Error.Validation($"{prefix}.quantity_invalid", "Quantities are positive.").WithWhy(("item", item.Code));
        }

        var units = await items.UomsAsync(item.Id, cancellationToken);
        ItemUomInfo? unit;
        if (uomId is null && string.IsNullOrWhiteSpace(uom))
        {
            unit = units.First(static u => u.IsBase);
        }
        else
        {
            var wanted = uom?.Trim().ToUpperInvariant();
            unit = units.FirstOrDefault(u => uomId is { } uid ? u.UomId == uid : u.UomCode == wanted);
            if (unit is null)
            {
                return Error.Validation($"{prefix}.uom_not_item_uom", "The unit must be one of the item's units.").WithWhy(("item", item.Code), ("uom", uom ?? uomId?.ToString()), ("itemUoms", units.Select(static u => u.UomCode)));
            }
        }

        var exact = await items.ToBaseAsync(item.Id, unit.UomId, quantity, cancellationToken);
        if (exact.IsFailure)
        {
            return exact.Error!;
        }

        if (variantId is { } vid)
        {
            var variant = await items.FindVariantAsync(vid, cancellationToken);
            if (variant is null || variant.ItemId != item.Id)
            {
                return Error.Validation($"{prefix}.variant_invalid", "The variant must belong to the item.").WithWhy(("item", item.Code), ("variantId", vid));
            }
        }
        else if (item.HasVariants)
        {
            return Error.Validation($"{prefix}.variant_required", "The item has variants; name the one that moves.").WithWhy(("item", item.Code));
        }

        return (item, unit);
    }
}

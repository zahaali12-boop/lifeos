using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;
using Quicker.Workflow.Contracts;
using Scriban;
using Scriban.Runtime;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Purchase orders: drafted with priced lines in the supplier's currency, submitted through the workflow engine
/// (a matching rule routes it, none approves it), approved with budget commitments and blanket releases recorded,
/// sent to the supplier by email, changed through revisions that keep the previous version and go through approval
/// again, cancelled or closed with the commitments released.
/// </summary>
public sealed class PurchaseOrderService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IWarehouseDirectory warehouses,
    IPartnerDirectory partners,
    IDimensionSets dimensionSets,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IWorkflowEngine workflow,
    IEmailSender email,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    BlanketAgreementService agreements,
    IServiceProvider services)
{
    public const string DocumentType = PurchaseDocumentTypes.Order;

    /// <summary>The budget line a non-stock purchase commits against; resolved to the item's expense account when budgets land (M6).</summary>
    public const string ExpenseRole = "Expense";

    private static readonly Lazy<string> EmailTemplate = new(static () =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("templates.purchase_order_email.html") ?? throw new InvalidOperationException("The purchase order email template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public async Task<IReadOnlyList<PurchaseOrderSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Orders.Include(static o => o.Lines).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(o => o.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = s == "open" ? query.Where(static o => o.Status == "approved" || o.Status == "sent" || o.Status == "partially_received") : query.Where(o => o.Status == s);
        }

        if (partnerId is { } p)
        {
            query = query.Where(o => o.PartnerId == p);
        }

        var rows = await query.OrderByDescending(static o => o.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<PurchaseOrderSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, false, cancellationToken));
        }

        return result;
    }

    public async Task<PurchaseOrderSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? null : await MapAsync(order, true, cancellationToken);
    }

    public async Task<Result<PurchaseOrderSummary>> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken, Guid? requisitionId = null, Guid? rfqId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var order = new PurchaseOrder { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, RequisitionId = requisitionId, RfqId = rfqId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(order, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "PO-" + company.Code, "PO-{yyyy}-{seq:5}", "yearly", cancellationToken);
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
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(order, true, cancellationToken);
    }

    public async Task<Result<PurchaseOrderSummary>> UpdateAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("order.not_draft", "An approved order is changed through a change order.").WithWhy(("status", order.Status));
        }

        if (order.CompanyId != request.CompanyId)
        {
            return Error.Conflict("order.company_locked", "An order cannot move to another company.");
        }

        var company = (await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken))!;
        var applied = await ApplyAsync(order, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        order.Status = "draft";
        order.RejectionReason = null;
        order.ApprovalRequestId = null;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(order, true, cancellationToken);
    }

    private async Task<Result> ApplyAsync(PurchaseOrder order, SavePurchaseOrderRequest request, CompanyInfo company, CancellationToken cancellationToken)
    {
        var supplier = await partners.FindSupplierAsync(company.Id.Value, request.PartnerId, cancellationToken);
        if (supplier is null)
        {
            return Error.Validation("supplier.not_registered", "The partner is not a supplier of this company.").WithWhy(("partnerId", request.PartnerId));
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, supplier.Currency, "order", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var orderDate = request.OrderDate ?? clock.TodayIn(company.TimeZone);
        var rate = 1m;
        if (currency.Value.Code != company.FunctionalCurrency.Code)
        {
            var resolved = await rates.ResolveAsync(company.Id, currency.Value.Code, company.FunctionalCurrency.Code, orderDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            rate = resolved.Value.Rate.Rate;
        }

        if (request.WarehouseId is { } w)
        {
            var warehouse = await warehouses.FindAsync(w, cancellationToken);
            if (warehouse is null || warehouse.CompanyId != company.Id.Value || !warehouse.IsActive)
            {
                return Error.Validation("order.warehouse_invalid", "The warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", w));
            }
        }

        if (request.AgreementId is { } agreementId)
        {
            var agreement = await db.Agreements.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agreementId, cancellationToken);
            if (agreement is null || agreement.PartnerId != request.PartnerId || agreement.CompanyId != company.Id.Value)
            {
                return Error.Validation("order.agreement_invalid", "The agreement must belong to the supplier and the company.").WithWhy(("agreementId", agreementId));
            }
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("order.lines_required", "An order has at least one line.");
        }

        var validated = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var lines = new List<PurchaseOrderLine>();
        var lineNo = 0;
        var totalNet = 0m;
        foreach (var line in request.Lines)
        {
            lineNo++;
            var resolved = await Shared.ResolveLineAsync(items, "order", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!.WithWhy(("lineNo", lineNo));
            }

            var (item, unit, quantityBase) = resolved.Value;
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

            if (line.WarehouseId is { } lw)
            {
                var warehouse = await warehouses.FindAsync(lw, cancellationToken);
                if (warehouse is null || warehouse.CompanyId != company.Id.Value)
                {
                    return Error.Validation("order.warehouse_invalid", "The line's warehouse must belong to the company.").WithWhy(("lineNo", lineNo));
                }
            }

            if (line.DimensionSetId is { } ds && await dimensionSets.GetAsync(ds, cancellationToken) is null)
            {
                return Error.Validation("order.dimension_set_unknown", "The dimension set does not exist.").WithWhy(("lineNo", lineNo));
            }

            var unitPrice = line.UnitPrice;
            if (line.BlanketLineId is { } blanketLineId)
            {
                var blanket = await agreements.FindLineAsync(blanketLineId, cancellationToken);
                if (blanket is null || blanket.ItemId != item.Id)
                {
                    return Error.Validation("order.blanket_line_invalid", "The agreement line must be for the same item.").WithWhy(("lineNo", lineNo));
                }

                unitPrice ??= blanket.AgreedPrice;
            }

            if (unitPrice is null || unitPrice < 0m)
            {
                return Error.Validation("order.price_required", "Every line carries a unit price of zero or more.").WithWhy(("lineNo", lineNo), ("item", item.Code));
            }

            var net = Shared.Round(line.Quantity * unitPrice.Value * (1m - line.DiscountPct / 100m), currency.Value);
            totalNet += net;
            lines.Add(new PurchaseOrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = order.Id,
                LineNo = lineNo,
                ItemId = item.Id,
                VariantId = line.VariantId,
                Description = Shared.Trim(line.Description),
                Quantity = line.Quantity,
                UomId = unit.UomId,
                QuantityBase = quantityBase,
                UnitPrice = unitPrice.Value,
                DiscountPct = line.DiscountPct,
                NetAmount = net,
                TaxAmount = 0m,
                ExpectedDate = line.ExpectedDate ?? request.ExpectedDate,
                WarehouseId = line.WarehouseId ?? request.WarehouseId,
                DimensionSetId = line.DimensionSetId,
                RequisitionLineId = line.RequisitionLineId,
                BlanketLineId = line.BlanketLineId,
            });
        }

        var expectedDate = request.ExpectedDate ?? (supplier.LeadTimeDays > 0 ? orderDate.AddDays(supplier.LeadTimeDays) : null);
        order.PartnerId = request.PartnerId;
        order.BranchId = request.BranchId;
        order.Currency = currency.Value.Code;
        order.ExchangeRate = rate;
        order.OrderDate = orderDate;
        order.ExpectedDate = expectedDate;
        order.PaymentTermsId = request.PaymentTermsId ?? supplier.PaymentTermsId;
        order.DeliveryTermsId = request.DeliveryTermsId ?? supplier.DeliveryTermsId;
        order.WarehouseId = request.WarehouseId;
        order.AgreementId = request.AgreementId;
        order.Notes = Shared.Trim(request.Notes);
        order.CustomFields = validated.Value;
        order.TotalNet = totalNet;
        order.TotalTax = 0m;
        order.TotalGross = totalNet;
        order.SupplierSnapshot = JsonSerializer.Serialize(new { supplier.PartnerCode, name = supplier.PartnerName.Values, supplier.Currency, supplier.PaymentTermsCode, supplier.DeliveryTermsCode, supplier.LeadTimeDays, supplier.PriceTolerancePct, supplier.QtyTolerancePct, supplier.RequiresPo }, Shared.Json);
        order.Lines.Clear();
        order.Lines.AddRange(lines);
        order.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Submits: the supplier must be open for purchasing; the active definition decides, none approves at once (ASSUMPTIONS A-108).</summary>
    public async Task<Result<PurchaseOrderSummary>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Status is not ("draft" or "rejected"))
        {
            return Error.Conflict("order.not_draft", "Only a draft order is submitted.").WithWhy(("status", order.Status));
        }

        var supplier = await partners.EnsureSupplierAsync(order.CompanyId, order.PartnerId, SupplierPurposes.Purchase, cancellationToken);
        if (supplier.IsFailure)
        {
            return supplier.Error!;
        }

        var releases = await CheckReleasesAsync(order, cancellationToken);
        if (releases.IsFailure)
        {
            return releases.Error!;
        }

        order.SubmittedBy = principal.Principal?.UserId.Value;
        order.SubmittedAt = clock.UtcNow;
        order.UpdatedAt = clock.UtcNow;
        if (await workflow.HasActiveDefinitionAsync(DocumentType, WorkflowTriggers.OnSubmit, null, cancellationToken))
        {
            var outcome = await workflow.SubmitAsync(await SubjectAsync(order, cancellationToken), WorkflowTriggers.OnSubmit, cancellationToken);
            if (outcome.IsFailure)
            {
                return outcome.Error!;
            }

            if (outcome.Value.Status == WorkflowOutcomes.Pending)
            {
                order.Status = "pending_approval";
                order.ApprovalRequestId = outcome.Value.RequestId;
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { status = order.Status, approvalRequestId = order.ApprovalRequestId, rule = outcome.Value.RuleName?.Values }, CompanyId: order.CompanyId), cancellationToken);
                return await MapAsync(order, true, cancellationToken);
            }
        }

        var approved = await ApproveCoreAsync(order, null, cancellationToken);
        if (approved.IsFailure)
        {
            return approved.Error!;
        }

        return await MapAsync(order, true, cancellationToken);
    }

    /// <summary>Before submitting: every blanket release must still fit (the release itself is recorded at approval).</summary>
    private async Task<Result> CheckReleasesAsync(PurchaseOrder order, CancellationToken cancellationToken)
    {
        foreach (var line in order.Lines.Where(static l => l.BlanketLineId is not null))
        {
            var blanket = await agreements.FindLineAsync(line.BlanketLineId!.Value, cancellationToken);
            if (blanket is null)
            {
                return Error.Validation("agreement.line_unknown", "The agreement line does not exist.").WithWhy(("lineNo", line.LineNo));
            }

            if (blanket.ReleasedQty + line.Quantity > blanket.AgreedQty)
            {
                return Error.Conflict("agreement.over_release", "The release exceeds what the agreement still allows.").WithWhy(("lineNo", line.LineNo), ("agreedQty", blanket.AgreedQty), ("releasedQty", blanket.ReleasedQty), ("requested", line.Quantity));
            }
        }

        return Result.Success();
    }

    private async Task<Result> ApproveCoreAsync(PurchaseOrder order, string? comment, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken))!;
        foreach (var line in order.Lines)
        {
            if (line.BlanketLineId is { } blanketLineId)
            {
                var released = await agreements.ReleaseAsync(blanketLineId, order.PartnerId, line.UomId, line.Quantity, line.NetAmount, order.OrderDate, cancellationToken);
                if (released.IsFailure)
                {
                    return released;
                }
            }

            var item = await items.FindAsync(line.ItemId, cancellationToken);
            var amountRc = Shared.Round(line.NetAmount * order.ExchangeRate, company.FunctionalCurrency);
            db.Commitments.Add(new Commitment
            {
                Id = Guid.CreateVersion7(),
                CompanyId = order.CompanyId,
                OrderId = order.Id,
                OrderLineId = line.Id,
                AccountRole = item?.IsStockItem == true ? AccountRoles.Inventory : ExpenseRole,
                DimensionSetId = line.DimensionSetId,
                PeriodKey = Shared.PeriodKey(line.ExpectedDate ?? order.ExpectedDate ?? order.OrderDate),
                AmountFc = line.NetAmount,
                Currency = order.Currency,
                AmountRc = amountRc,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
            });
        }

        order.Status = "approved";
        order.ApprovedAt = clock.UtcNow;
        order.ApprovalRequestId = null;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.Approved, After: new { status = order.Status, order.Revision, totalGross = order.TotalGross, order.Currency }, Reason: comment, CompanyId: order.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>The workflow's decision (ADR-0020): approval records commitments and releases; rejection keeps the order for editing.</summary>
    public async Task<Result> DecideAsync(WorkflowDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == decision.EntityId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", decision.EntityId);
        }

        if (order.Status != "pending_approval" || order.ApprovalRequestId != decision.RequestId)
        {
            return Error.Conflict("order.not_pending", "The order is no longer awaiting this approval request.").WithWhy(("status", order.Status));
        }

        if (decision.Status == WorkflowDecisions.Approved)
        {
            return await ApproveCoreAsync(order, decision.Comment, cancellationToken);
        }

        var rejected = decision.Status == WorkflowDecisions.Rejected;
        order.Status = rejected ? "rejected" : "draft";
        order.RejectionReason = rejected ? decision.Comment ?? "Not approved." : null;
        order.ApprovalRequestId = null;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, rejected ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status = order.Status, reason = order.RejectionReason }, CompanyId: order.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>A change order: the current version is kept as a revision, the new content applied, commitments and releases of the old version given back, and the order goes through approval again.</summary>
    public async Task<Result<PurchaseOrderSummary>> ChangeAsync(Guid id, ChangeOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Lines.Any(static l => l.QtyReceived > 0m || l.QtyInvoiced > 0m))
        {
            return Error.Conflict("order.change_after_receipt", "An order with receipts or invoices is closed short and reordered rather than changed.").WithWhy(("status", order.Status));
        }

        if (order.Status is not ("approved" or "sent"))
        {
            return Error.Conflict("order.not_changeable", "Only an approved or sent order takes a change order; drafts are edited, received orders are closed.").WithWhy(("status", order.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("order.change_reason_required", "A change order says why.");
        }

        if (order.CompanyId != request.Order.CompanyId)
        {
            return Error.Conflict("order.company_locked", "An order cannot move to another company.");
        }

        var snapshot = await MapAsync(order, false, cancellationToken);
        db.Revisions.Add(new PurchaseOrderRevision { Id = Guid.CreateVersion7(), OrderId = order.Id, Revision = order.Revision, Snapshot = JsonSerializer.Serialize(snapshot, Shared.Json), Reason = request.Reason.Trim(), ChangedBy = principal.Principal?.UserId.Value, ChangedAt = clock.UtcNow });
        var undone = await UndoApprovalAsync(order, cancellationToken);
        if (undone.IsFailure)
        {
            return undone.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken))!;
        var previousStatus = order.Status;
        var applied = await ApplyAsync(order, request.Order, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        order.Revision += 1;
        order.Status = "draft";
        order.SentAt = null;
        order.SentTo = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.Updated, Before: new { status = previousStatus, revision = order.Revision - 1 }, After: new { revision = order.Revision, totalGross = order.TotalGross }, Reason: request.Reason, CompanyId: order.CompanyId), cancellationToken);
        return await SubmitAsync(order.Id, cancellationToken);
    }

    private async Task<Result> UndoApprovalAsync(PurchaseOrder order, CancellationToken cancellationToken)
    {
        foreach (var line in order.Lines.Where(static l => l.BlanketLineId is not null))
        {
            var released = await agreements.ReleaseAsync(line.BlanketLineId!.Value, order.PartnerId, line.UomId, -line.Quantity, -line.NetAmount, order.OrderDate, cancellationToken);
            if (released.IsFailure)
            {
                return released;
            }
        }

        foreach (var commitment in await db.Commitments.Where(c => c.OrderId == order.Id && c.Status == "open").ToListAsync(cancellationToken))
        {
            commitment.Status = "released";
            commitment.UpdatedAt = clock.UtcNow;
        }

        return Result.Success();
    }

    /// <summary>Emails the order to the supplier: a bilingual HTML rendering from the shipped template and a plain-text body; the PDF attachment arrives with the print pipeline (5.9).</summary>
    public async Task<Result<PurchaseOrderSummary>> SendAsync(Guid id, SendOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Status is not ("approved" or "sent" or "partially_received"))
        {
            return Error.Conflict("order.not_approved", "Only an approved order is sent.").WithWhy(("status", order.Status));
        }

        var partner = await partners.FindAsync(order.PartnerId, cancellationToken);
        var to = Shared.Trim(request.To) ?? partner?.Email;
        if (string.IsNullOrWhiteSpace(to))
        {
            return Error.Validation("order.no_email", "The supplier has no email address; give one to send to.").WithWhy(("partner", partner?.Code));
        }

        var company = (await companies.FindAsync(new CompanyId(order.CompanyId), cancellationToken))!;
        var summary = await MapAsync(order, false, cancellationToken);
        var html = RenderEmail(summary, company, partner, Shared.Trim(request.Message));
        var text = string.Join('\n', new[] { $"Purchase order {order.Number} (revision {order.Revision}) from {company.Code}", $"Supplier: {partner?.Code}", $"Order date: {order.OrderDate:yyyy-MM-dd}", string.Empty }
            .Concat(summary.Lines.Select(l => $"{l.LineNo}. {l.ItemCode} {l.Description ?? string.Empty} — {l.Quantity:0.###} {l.UomCode} × {l.UnitPrice:0.####} = {l.NetAmount:0.##} {order.Currency}"))
            .Append(string.Empty).Append($"Total: {order.TotalGross:0.##} {order.Currency}"));
        await email.SendAsync(new EmailMessage(to, $"Purchase order {order.Number}" + (order.Revision > 1 ? $" (revision {order.Revision})" : string.Empty), text, html, partner?.LegalName.Resolve("en")), cancellationToken);

        order.Status = order.Status == "approved" ? "sent" : order.Status;
        order.SentAt = clock.UtcNow;
        order.SentTo = to;
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { status = order.Status, sentTo = to, order.Revision }, CompanyId: order.CompanyId), cancellationToken);
        return await MapAsync(order, true, cancellationToken);
    }

#pragma warning disable MA0002 // Scriban script objects are case-sensitive ordinal maps by design.
    private static string RenderEmail(PurchaseOrderSummary o, CompanyInfo company, PartnerInfo? partner, string? message)
    {
        var globals = new ScriptObject
        {
            ["number"] = o.Number,
            ["revision"] = o.Revision,
            ["order_date"] = o.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["expected_date"] = o.ExpectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["company_code"] = company.Code,
            ["company_name_en"] = company.LegalName.Resolve("en"),
            ["company_name_ar"] = company.LegalName.Resolve("ar"),
            ["supplier_code"] = partner?.Code ?? o.PartnerCode,
            ["supplier_name_en"] = partner?.LegalName.Resolve("en") ?? o.PartnerCode,
            ["supplier_name_ar"] = partner?.LegalName.Resolve("ar") ?? o.PartnerCode,
            ["currency"] = o.Currency,
            ["payment_terms"] = o.PaymentTermsCode,
            ["delivery_terms"] = o.DeliveryTermsCode,
            ["message"] = message,
            ["notes"] = o.Notes,
            ["total_net"] = o.TotalNet.ToString("N2", CultureInfo.InvariantCulture),
            ["total_gross"] = o.TotalGross.ToString("N2", CultureInfo.InvariantCulture),
            ["lines"] = new ScriptArray(o.Lines.Select(l => new ScriptObject
            {
                ["line_no"] = l.LineNo,
                ["item_code"] = l.ItemCode,
                ["description_en"] = l.Description ?? (l.ItemName.TryGetValue("en", out var en) ? en : string.Empty),
                ["description_ar"] = l.Description ?? (l.ItemName.TryGetValue("ar", out var ar) ? ar : string.Empty),
                ["quantity"] = l.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                ["uom"] = l.UomCode,
                ["unit_price"] = l.UnitPrice.ToString("N4", CultureInfo.InvariantCulture),
                ["net"] = l.NetAmount.ToString("N2", CultureInfo.InvariantCulture),
            })),
        };
        var context = new TemplateContext();
        context.PushGlobal(globals);
        return Template.Parse(EmailTemplate.Value).Render(context);
    }
#pragma warning restore MA0002

    public async Task<Result<PurchaseOrderSummary>> CancelAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Status is "cancelled" or "closed" or "received" or "partially_received")
        {
            return Error.Conflict("order.not_cancellable", "An order with receipts is closed, not cancelled.").WithWhy(("status", order.Status));
        }

        if (order.ApprovalRequestId is not null)
        {
            var withdrawn = await workflow.CancelAsync(DocumentType, order.Id, reason ?? "The order was cancelled.", cancellationToken);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }
        }

        if (order.Status is "approved" or "sent")
        {
            var undone = await UndoApprovalAsync(order, cancellationToken);
            if (undone.IsFailure)
            {
                return undone.Error!;
            }
        }

        if (order.RequisitionId is { } requisitionId)
        {
            await services.GetRequiredService<RequisitionService>().ReleaseOrderedAsync(requisitionId, order.Lines.Where(static l => l.RequisitionLineId is not null).Select(static l => (l.RequisitionLineId!.Value, l.Quantity)), cancellationToken);
        }

        order.Status = "cancelled";
        order.ApprovalRequestId = null;
        order.UpdatedAt = clock.UtcNow;
        foreach (var line in order.Lines)
        {
            line.QtyCancelled = line.Quantity - line.QtyReceived;
            line.Status = "cancelled";
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { status = order.Status }, Reason: reason, CompanyId: order.CompanyId), cancellationToken);
        return await MapAsync(order, true, cancellationToken);
    }

    /// <summary>Closes an open order short: what was not received is cancelled on the lines and the open commitments released.</summary>
    public async Task<Result<PurchaseOrderSummary>> CloseAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("purchase_order", id);
        }

        if (order.Status is not ("approved" or "sent" or "partially_received" or "received"))
        {
            return Error.Conflict("order.not_closable", "Only an approved, sent or received order is closed.").WithWhy(("status", order.Status));
        }

        foreach (var line in order.Lines.Where(static l => l.Status is "open" or "partially_received"))
        {
            line.QtyCancelled = line.Quantity - line.QtyReceived;
            line.Status = "closed";
        }

        foreach (var commitment in await db.Commitments.Where(c => c.OrderId == order.Id && c.Status == "open").ToListAsync(cancellationToken))
        {
            commitment.Status = commitment.ConsumedRc > 0m ? "consumed" : "released";
            commitment.UpdatedAt = clock.UtcNow;
        }

        order.Status = "closed";
        order.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, order.Id, order.Number, AuditActions.StateChanged, After: new { status = order.Status }, CompanyId: order.CompanyId), cancellationToken);
        return await MapAsync(order, true, cancellationToken);
    }

    internal async Task<WorkflowSubject> SubjectAsync(PurchaseOrder o, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(o.PartnerId, cancellationToken);
        var warehouse = o.WarehouseId is { } w ? await warehouses.FindAsync(w, cancellationToken) : null;
        var company = (await companies.FindAsync(new CompanyId(o.CompanyId), cancellationToken))!;
        return new WorkflowSubject(DocumentType, o.Id, o.CompanyId, $"{o.Number} · {partner?.Code} · {o.TotalGross:0.##} {o.Currency}", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = o.TotalGross,
            ["currency"] = o.Currency,
            ["amountRc"] = Shared.Round(o.TotalGross * o.ExchangeRate, company.FunctionalCurrency),
            ["supplierCode"] = partner?.Code,
            ["lineCount"] = o.Lines.Count,
            ["warehouseCode"] = warehouse?.Code,
            ["orderDate"] = o.OrderDate,
            ["revision"] = o.Revision,
            ["hasAgreement"] = o.AgreementId is not null || o.Lines.Any(static l => l.BlanketLineId is not null),
            ["fromRequisition"] = o.RequisitionId is not null,
        });
    }

    internal async Task<PurchaseOrder?> LoadAsync(Guid id, CancellationToken cancellationToken) => await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    internal async Task<PurchaseOrderSummary> MapAsync(PurchaseOrder o, bool withHistory, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(o.PartnerId, cancellationToken);
        var terms = await partners.FindSupplierAsync(o.CompanyId, o.PartnerId, cancellationToken);
        var warehouse = o.WarehouseId is { } w ? await warehouses.FindAsync(w, cancellationToken) : null;
        var company = (await companies.FindAsync(new CompanyId(o.CompanyId), cancellationToken))!;
        var lines = new List<PurchaseOrderLineSummary>(o.Lines.Count);
        foreach (var l in o.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var unit = item is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            lines.Add(new PurchaseOrderLineSummary(l.Id, l.LineNo, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.VariantId, l.Description, l.Quantity, l.UomId, unit?.UomCode ?? string.Empty, l.QuantityBase, l.UnitPrice, l.DiscountPct, l.NetAmount, l.TaxAmount, l.ExpectedDate, l.WarehouseId, l.DimensionSetId, l.QtyReceived, l.QtyInvoiced, l.QtyCancelled, l.RequisitionLineId, l.BlanketLineId, l.Status));
        }

        var revisions = withHistory
            ? (await db.Revisions.AsNoTracking().Where(r => r.OrderId == o.Id).OrderBy(static r => r.Revision).ToListAsync(cancellationToken)).Select(static r => new PurchaseOrderRevisionSummary(r.Revision, r.Reason, r.ChangedBy, r.ChangedAt, Shared.Parse(r.Snapshot))).ToList()
            : [];
        var commitments = withHistory
            ? (await db.Commitments.AsNoTracking().Where(c => c.OrderId == o.Id).OrderBy(static c => c.CreatedAt).ToListAsync(cancellationToken)).Select(static c => new CommitmentSummary(c.Id, c.OrderLineId, c.AccountRole, c.DimensionSetId, c.PeriodKey, c.AmountFc, c.Currency, c.AmountRc, c.ConsumedRc, c.Status)).ToList()
            : [];
        var paymentTermsCode = o.PaymentTermsId is { } pt ? (pt == terms?.PaymentTermsId ? terms.PaymentTermsCode : await TermsCodeAsync("payment", pt, cancellationToken)) : null;
        var deliveryTermsCode = o.DeliveryTermsId is { } dt ? (dt == terms?.DeliveryTermsId ? terms.DeliveryTermsCode : await TermsCodeAsync("delivery", dt, cancellationToken)) : null;
        return new PurchaseOrderSummary(o.Id, o.CompanyId, o.Number, o.Revision, o.Status, o.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            o.Currency, o.ExchangeRate, o.OrderDate, o.ExpectedDate, o.PaymentTermsId, paymentTermsCode, o.DeliveryTermsId, deliveryTermsCode, o.WarehouseId, warehouse?.Code,
            o.TotalNet, o.TotalTax, o.TotalGross, Shared.Round(o.TotalGross * o.ExchangeRate, company.FunctionalCurrency), o.ApprovalRequestId, o.RejectionReason, o.RequisitionId, o.RfqId, o.AgreementId, o.Notes, Shared.Parse(o.CustomFields),
            o.SubmittedAt, o.ApprovedAt, o.SentAt, o.SentTo, lines, revisions, commitments, o.UpdatedAt);
    }

    private async Task<string?> TermsCodeAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        // Terms other than the supplier's defaults are read through the partner module's tables by id; a lookup contract is not worth its weight yet.
        var code = kind == "payment"
            ? await db.Database.SqlQuery<string>($"SELECT code AS \"Value\" FROM app.ptr_payment_terms WHERE id = {id}").FirstOrDefaultAsync(cancellationToken)
            : await db.Database.SqlQuery<string>($"SELECT code AS \"Value\" FROM app.ptr_delivery_terms WHERE id = {id}").FirstOrDefaultAsync(cancellationToken);
        return code;
    }
}

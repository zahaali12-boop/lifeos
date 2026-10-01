using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>Purchase requisitions as the workflow engine sees them (ADR-0020).</summary>
public sealed class RequisitionWorkflowSubject(RequisitionService requisitions) : IWorkflowSubjectProvider
{
    public string EntityType => PurchaseDocumentTypes.Requisition;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Purchase requisition", "طلب شراء");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Estimated total", "الإجمالي التقديري")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
        new("neededBy", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Needed by", "مطلوب بحلول")),
        new("department", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Department (dimension value id)", "القسم (معرّف قيمة البعد)")),
        new("hasWarehouse", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Names a warehouse", "يحدد مستودعًا")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = [];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var requisition = await requisitions.LoadAsync(entityId, cancellationToken);
        return requisition is null ? null : RequisitionService.Subject(requisition);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => requisitions.DecideAsync(decision, cancellationToken);
}

/// <summary>Purchase orders as the workflow engine sees them: the acceptance rule of 4.2, "a PO above threshold routes to approval", is one condition on <c>amount_in</c>.</summary>
public sealed class PurchaseOrderWorkflowSubject(PurchaseOrderService orders) : IWorkflowSubjectProvider
{
    public string EntityType => PurchaseDocumentTypes.Order;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Purchase order", "أمر شراء");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Order total", "إجمالي الأمر")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("amountRc", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Order total in the company's currency", "إجمالي الأمر بعملة الشركة")),
        new("supplierCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Supplier code", "رمز المورد")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
        new("warehouseCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Warehouse code", "رمز المستودع")),
        new("orderDate", WorkflowFieldTypes.Date, LocalizedText.Bilingual("Order date", "تاريخ الأمر")),
        new("revision", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Revision", "المراجعة")),
        new("hasAgreement", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Releases a blanket agreement", "يصرف من اتفاقية إطارية")),
        new("fromRequisition", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Raised from a requisition", "ناشئ عن طلب شراء")),
    ];

    public IReadOnlyList<string> BlockKinds { get; } = ["budget_exceeded"];

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var order = await orders.LoadAsync(entityId, cancellationToken);
        return order is null ? null : await orders.SubjectAsync(order, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => orders.DecideAsync(decision, cancellationToken);
}

/// <summary>Supplier invoices as the workflow engine sees them: approval by amount, and the match blocks (price, quantity, duplicate) an override clears (ADR-0020).</summary>
public sealed class InvoiceWorkflowSubject(InvoiceService invoices) : IWorkflowSubjectProvider
{
    public string EntityType => PurchaseDocumentTypes.Invoice;

    public LocalizedText Label { get; } = LocalizedText.Bilingual("Supplier invoice", "فاتورة مورد");

    public IReadOnlyList<WorkflowField> Fields { get; } =
    [
        new("amount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Invoice total", "إجمالي الفاتورة")),
        new("currency", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Currency", "العملة")),
        new("amountRc", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Invoice total in the company's currency", "إجمالي الفاتورة بعملة الشركة")),
        new("supplierCode", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Supplier code", "رمز المورد")),
        new("kind", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Kind (invoice, expense)", "النوع (فاتورة، مصروف)")),
        new("matchStatus", WorkflowFieldTypes.Text, LocalizedText.Bilingual("Match status", "حالة المطابقة")),
        new("hasVariance", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Has a variance beyond tolerance", "فيها فرق يتجاوز التسامح")),
        new("hasOverride", WorkflowFieldTypes.Boolean, LocalizedText.Bilingual("Cleared by an override", "أُجيزت بتجاوز")),
        new("lineCount", WorkflowFieldTypes.Number, LocalizedText.Bilingual("Number of lines", "عدد البنود")),
    ];

    public IReadOnlyList<string> BlockKinds => InvoiceService.BlockKinds;

    public async Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var invoice = await invoices.LoadAsync(entityId, cancellationToken);
        return invoice is null ? null : await invoices.SubjectAsync(invoice, null, cancellationToken);
    }

    public Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default) => invoices.DecideAsync(decision, cancellationToken);
}

/// <summary>Open purchase order lines as incoming supply for the replenishment planner (roadmap 3.7 contract).</summary>
public sealed class PurchasingSupply(PurchasingDbContext db) : IIncomingSupply, IPurchaseOrderDirectory
{
    private static readonly string[] OpenOrderStatuses = ["approved", "sent", "partially_received"];

    private static readonly string[] OpenLineStatuses = ["open", "partially_received"];

    public async Task<IReadOnlyList<IncomingSupplyInfo>> IncomingAsync(Guid companyId, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        var rows = await (from l in db.OrderLines.AsNoTracking()
                          join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                          where o.CompanyId == companyId && OpenOrderStatuses.Contains(o.Status) && OpenLineStatuses.Contains(l.Status) && (l.WarehouseId ?? o.WarehouseId) == warehouseId
                          select new { l.ItemId, l.Quantity, l.QuantityBase, l.QtyReceived, l.QtyCancelled, ExpectedOn = l.ExpectedDate ?? o.ExpectedDate, o.Id })
            .ToListAsync(cancellationToken);
        return rows
            .Select(r => new IncomingSupplyInfo(r.ItemId, warehouseId, Remaining(r.Quantity, r.QuantityBase, r.QtyReceived, r.QtyCancelled), r.ExpectedOn, PurchaseDocumentTypes.Order, r.Id))
            .Where(static s => s.Quantity > 0m)
            .ToList();
    }

    public async Task<IReadOnlyList<PurchaseOrderLineInfo>> OpenLinesAsync(Guid companyId, Guid? partnerId = null, Guid? orderId = null, CancellationToken cancellationToken = default)
    {
        var query = from l in db.OrderLines.AsNoTracking()
                    join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                    where o.CompanyId == companyId && OpenOrderStatuses.Contains(o.Status) && OpenLineStatuses.Contains(l.Status)
                    select new { Line = l, Order = o };
        if (partnerId is { } p)
        {
            query = query.Where(x => x.Order.PartnerId == p);
        }

        if (orderId is { } id)
        {
            query = query.Where(x => x.Order.Id == id);
        }

        var rows = await query.OrderBy(static x => x.Order.Number).ThenBy(static x => x.Line.LineNo).ToListAsync(cancellationToken);
        return rows.Select(static x => Map(x.Line, x.Order)).ToList();
    }

    public async Task<Result<PurchaseOrderLineInfo>> FindLineAsync(Guid orderLineId, CancellationToken cancellationToken = default)
    {
        var line = await db.OrderLines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == orderLineId, cancellationToken);
        var order = line is null ? null : await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == line.OrderId, cancellationToken);
        return line is null || order is null ? Error.NotFound("purchase_order_line", orderLineId) : Map(line, order);
    }

    private static decimal Remaining(decimal quantity, decimal quantityBase, decimal received, decimal cancelled)
    {
        var remaining = quantity - received - cancelled;
        return remaining <= 0m || quantity == 0m ? 0m : remaining * quantityBase / quantity;
    }

    private static PurchaseOrderLineInfo Map(Domain.PurchaseOrderLine l, Domain.PurchaseOrder o) => new(o.Id, o.Number, l.Id, l.LineNo, o.CompanyId, o.PartnerId, l.ItemId, l.VariantId, l.UomId, l.Quantity, l.QuantityBase, l.QtyReceived, l.QtyInvoiced, l.UnitPrice, l.DiscountPct, o.Currency, o.ExchangeRate, l.WarehouseId ?? o.WarehouseId, l.ExpectedDate ?? o.ExpectedDate, l.Status);
}

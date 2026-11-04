using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>One item bought: the quantity in the item's unit (or the one named) at the supplier's unit price in the purchase currency.</summary>
public sealed record QuickPurchaseLineRequest(decimal Quantity, decimal UnitPrice, Guid? ItemId = null, string? ItemCode = null, string? Uom = null);

/// <summary>A cost of bringing the goods in (freight, duty, port, transport) in its currency, owed to its agent when one is named.</summary>
public sealed record QuickPurchaseCostRequest(Guid ChargeTypeId, decimal Amount, string? Currency = null, Guid? PartnerId = null, string? Description = null);

/// <summary>
/// Goods bought and already in hand, recorded in one step (trader edition, ADR-0035 §22): the supplier, the store, the
/// day, the items at their prices in the purchase currency, the journey's costs, the supplier's invoice number.
/// </summary>
public sealed record QuickPurchaseRequest(Guid CompanyId, Guid PartnerId, Guid WarehouseId, IReadOnlyList<QuickPurchaseLineRequest> Lines, string? Currency = null, DateOnly? Date = null, string? SupplierInvoiceNumber = null, string? Reference = null, IReadOnlyList<QuickPurchaseCostRequest>? Costs = null, string? Notes = null);

public sealed record QuickPurchaseLineResult(Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal Quantity, decimal UnitPrice, decimal LandedUnitCostRc);

public sealed record QuickPurchaseResult(Guid OrderId, string OrderNumber, Guid ShipmentId, Guid ReceiptId, string ReceiptNumber, Guid InvoiceId, string InvoiceNumber, string InvoiceStatus, string Currency, decimal TotalGross, decimal CostsTotalRc, decimal LandedTotalRc, Guid? OpenItemId, decimal OutstandingTc, IReadOnlyList<QuickPurchaseLineResult> Lines);

/// <summary>
/// A purchase in one step for goods that are already here (trader edition, ADR-0035 §22): the order is made and
/// submitted, tracked as a shipment that arrives at once (the goods receipt and the landed costs posted through their
/// own services, the agents' invoices made for the costs), and the supplier's invoice is recorded and posted into the
/// payable at the ordered prices. Nothing is kept when any step refuses (one unit of work). The shipment stays the
/// record of the purchase, so the pricing board, Shipments and Money see it as they see any arrival; paying for it is
/// the shipment's own step, from the screen or from Money.
/// </summary>
public sealed class QuickPurchaseService(PurchaseOrderService orders, ShipmentService shipments, ICompanyDirectory companies, IClock clock)
{
    public async Task<Result<QuickPurchaseResult>> ReceiveAsync(QuickPurchaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
        {
            return Error.Validation("quick_purchase.lines_required", "A purchase has at least one item.");
        }

        if (request.Lines.Any(static l => l.Quantity <= 0m))
        {
            return Error.Validation("quick_purchase.quantity_invalid", "Quantities are positive.");
        }

        if (request.Lines.Any(static l => l.UnitPrice < 0m))
        {
            return Error.Validation("quick_purchase.price_invalid", "A unit price is zero or more.");
        }

        if (request.Costs is not null && request.Costs.Any(static c => c.Amount <= 0m))
        {
            return Error.Validation("quick_purchase.cost_invalid", "A cost is a positive amount.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("quick_purchase.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? "Direct purchase" : request.Notes.Trim();
        var order = await orders.CreateAsync(new SavePurchaseOrderRequest(request.CompanyId, request.PartnerId,
            request.Lines.Select(l => new SavePurchaseOrderLineRequest(l.ItemId, l.ItemCode, Quantity: l.Quantity, Uom: l.Uom, UnitPrice: l.UnitPrice, ExpectedDate: date, WarehouseId: request.WarehouseId)).ToList(),
            request.Currency, date, date, WarehouseId: request.WarehouseId, Notes: notes), cancellationToken);
        if (order.IsFailure)
        {
            return order.Error!;
        }

        var shipment = await shipments.CreateAsync(new CreatePurchaseShipmentRequest(order.Value.Id, date, request.Reference, notes), cancellationToken);
        if (shipment.IsFailure)
        {
            return shipment.Error!;
        }

        foreach (var cost in request.Costs ?? [])
        {
            var added = await shipments.AddCostAsync(shipment.Value.Id, new SavePurchaseShipmentCostRequest(cost.ChargeTypeId, cost.Amount, cost.Currency, null, cost.Description, date, cost.PartnerId), cancellationToken);
            if (added.IsFailure)
            {
                return added.Error!;
            }
        }

        var received = await shipments.ReceiveAsync(shipment.Value.Id, new ReceivePurchaseShipmentRequest(request.WarehouseId, date), cancellationToken);
        if (received.IsFailure)
        {
            return received.Error!;
        }

        var invoiced = await shipments.RecordInvoiceAsync(shipment.Value.Id, new RecordPurchaseShipmentInvoiceRequest(request.SupplierInvoiceNumber, date, date), cancellationToken);
        if (invoiced.IsFailure)
        {
            return invoiced.Error!;
        }

        var s = invoiced.Value;
        var openItem = await shipments.OpenInvoiceItemAsync(s.InvoiceId, cancellationToken);
        return new QuickPurchaseResult(s.OrderId, s.OrderNumber, s.Id, s.ReceiptId!.Value, s.ReceiptNumber ?? string.Empty, s.InvoiceId!.Value, s.InvoiceNumber ?? string.Empty, s.InvoiceStatus ?? string.Empty,
            s.Currency, s.GoodsTotal, s.CostsTotalRc, s.LandedTotalRc, openItem?.Id, s.GoodsOutstandingTc,
            s.Lines.Select(static l => new QuickPurchaseLineResult(l.ItemId, l.ItemCode, l.ItemName, l.Quantity, l.UnitPrice, l.LandedUnitCostRc)).ToList());
    }
}

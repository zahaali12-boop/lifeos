using System.Globalization;
using Quicker.Kernel.Results;

namespace Quicker.Sales.Application;

public sealed record QuickSaleLineRequest(decimal Quantity, Guid? ItemId = null, string? ItemCode = null, decimal? UnitPrice = null, string? Uom = null);

/// <summary>A sale done in one step (trader edition, ADR-0035 §6): the goods leave the warehouse and the invoice is posted at once.</summary>
public sealed record QuickSaleRequest(Guid CompanyId, Guid PartnerId, Guid WarehouseId, IReadOnlyList<QuickSaleLineRequest> Lines, DateOnly? Date = null, string? Currency = null, Guid? PriceListId = null, string? CustomerReference = null, string? Notes = null);

public sealed record QuickSaleResult(Guid OrderId, string OrderNumber, Guid ShipmentId, string ShipmentNumber, Guid InvoiceId, string InvoiceNumber, string InvoiceStatus, string Currency, decimal TotalGross, Guid? OpenItemId, DateOnly? DueDate);

/// <summary>
/// One step from "sold" to the books: the order is created and confirmed (credit control included), shipped in full
/// from the warehouse (stock and cost of sales move), and invoiced from the shipment and posted (revenue, tax and the
/// receivable). Every document is the ordinary one, made by its own service with its own rules; a refusal anywhere
/// (a credit hold, not enough stock, a closed period) stops the whole sale and nothing is kept.
/// </summary>
public sealed class QuickSaleService(OrderService orders, ShipmentService shipments, InvoiceService invoices)
{
    public async Task<Result<QuickSaleResult>> SellAsync(QuickSaleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
        {
            return Error.Validation("quick_sale.lines_required", "A sale has at least one item.");
        }

        if (request.Lines.Any(static l => l.Quantity <= 0m))
        {
            return Error.Validation("quick_sale.quantity_invalid", "Quantities are positive.");
        }

        var order = await orders.CreateAsync(new SaveOrderRequest(request.CompanyId, request.PartnerId, request.WarehouseId,
            request.Lines.Select(static l => new SaveOrderLineRequest(l.ItemId, l.ItemCode, Quantity: l.Quantity, Uom: l.Uom, UnitPrice: l.UnitPrice)).ToList(),
            request.Currency, request.Date, request.Date, request.PriceListId, Notes: request.Notes), cancellationToken);
        if (order.IsFailure)
        {
            return order.Error!;
        }

        var confirmed = await orders.ConfirmAsync(order.Value.Id, cancellationToken);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        if (confirmed.Value.Status != "confirmed")
        {
            return Error.Conflict("quick_sale.order_held", "The sale is held by credit control; release the order or settle what the customer owes first.")
                .WithWhy(("order", confirmed.Value.Number), ("status", confirmed.Value.Status), ("reason", confirmed.Value.BlockReason));
        }

        // A sale in one step leaves the store now: a line the store cannot cover in full is refused in plain words, before
        // the shipment would refuse it as exceeding the reservation. Nothing is kept (one unit of work).
        var shortLine = confirmed.Value.Lines.FirstOrDefault(static l => l.Status == "backordered" || l.QtyReserved < l.QuantityBase);
        if (shortLine is not null)
        {
            return Error.Conflict("quick_sale.not_enough_stock", $"Not enough stock of {shortLine.ItemCode}: {shortLine.QtyReserved.ToString(CultureInfo.InvariantCulture)} on hand, {shortLine.QuantityBase.ToString(CultureInfo.InvariantCulture)} wanted. Receive stock first, or sell what there is.")
                .WithWhy(("item", shortLine.ItemCode), ("available", shortLine.QtyReserved), ("wanted", shortLine.QuantityBase), ("unit", shortLine.UomCode));
        }

        var shipment = await shipments.CreateAsync(new SaveShipmentRequest(order.Value.Id, confirmed.Value.Lines.Select(static l => new SaveShipmentLineRequest(l.Id, l.Quantity)).ToList(), request.Date), cancellationToken);
        if (shipment.IsFailure)
        {
            return shipment.Error!;
        }

        var shipped = await shipments.PostAsync(shipment.Value.Id, cancellationToken);
        if (shipped.IsFailure)
        {
            return shipped.Error!;
        }

        var invoice = await invoices.CreateAsync(new SaveSalesInvoiceRequest(request.CompanyId, request.PartnerId,
            shipped.Value.Lines.Select(static l => new SaveSalesInvoiceLineRequest("shipment", l.Quantity, l.Id)).ToList(),
            "invoice", request.CustomerReference, DocumentDate: request.Date, PostingDate: request.Date, Currency: request.Currency, PriceListId: request.PriceListId, Notes: request.Notes), cancellationToken);
        if (invoice.IsFailure)
        {
            return invoice.Error!;
        }

        var posted = await invoices.PostAsync(invoice.Value.Id, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var item = posted.Value.OpenItems.Count > 0 ? posted.Value.OpenItems[0] : null;
        return new QuickSaleResult(order.Value.Id, order.Value.Number, shipped.Value.Id, shipped.Value.Number, posted.Value.Id, posted.Value.Number, posted.Value.Status, posted.Value.Currency, posted.Value.TotalGross, item?.Id, item?.DueDate);
    }
}

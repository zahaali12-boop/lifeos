using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Sales.Application;
using Quicker.Web;

namespace Quicker.Sales.Api;

/// <summary>Sales under /api/v1/sales: quotations (roadmap 5.4a) and orders converted from them or created directly,
/// reserved against stock and credit-checked on confirmation (roadmap 5.4b) -- both priced by the pricing engine and
/// taxed by the tax engine.</summary>
public static class SalesEndpoints
{
    public static RouteGroupBuilder MapSalesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var sales = api.MapGroup("/sales").WithTags("Sales").RequireAuthorization();

        var quotations = sales.MapGroup("/quotations");
        quotations.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, QuotationService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(SalesPermissions.QuoteRead)
            .WithSummary("Quotations, newest first, filtered by company, status or customer");
        quotations.MapPost("/", async (SaveQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static q => $"/api/v1/sales/quotations/{q.Id}"))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("A draft quotation with lines priced by the pricing engine (customer agreements, price lists, promotions) and taxed for the customer on the pricing date");
        quotations.MapGet("/{quotationId:guid}", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(quotationId, ct), "quotation", quotationId))
            .RequirePermission(SalesPermissions.QuoteRead)
            .WithSummary("The quotation with its priced, taxed lines");
        quotations.MapPut("/{quotationId:guid}", async (Guid quotationId, SaveQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(quotationId, request, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Re-prices and re-taxes a draft quotation's lines; only a draft is edited");
        quotations.MapPost("/{quotationId:guid}/send", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.SendAsync(quotationId, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Marks a draft quotation sent to the customer");
        quotations.MapPost("/{quotationId:guid}/accept", async (Guid quotationId, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.AcceptAsync(quotationId, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Accepts a sent quotation; refused once its validity has passed (quotation.expired)");
        quotations.MapPost("/{quotationId:guid}/reject", async (Guid quotationId, RejectQuotationRequest request, QuotationService service, CancellationToken ct) => ApiProblems.Ok(await service.RejectAsync(quotationId, request, ct)))
            .RequirePermission(SalesPermissions.QuoteManage)
            .WithSummary("Rejects a sent quotation with a reason");
        quotations.MapPost("/{quotationId:guid}/convert", async (Guid quotationId, ConvertQuotationRequest request, OrderService service, CancellationToken ct) => ApiProblems.Created(await service.ConvertAsync(quotationId, request, ct), static o => $"/api/v1/sales/orders/{o.Id}"))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("A draft sales order with the accepted quotation's frozen lines carried over as they were quoted");

        var orders = sales.MapGroup("/orders");
        orders.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, OrderService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(SalesPermissions.OrderRead)
            .WithSummary("Sales orders, newest first, filtered by company, status or customer");
        orders.MapPost("/", async (SaveOrderRequest request, OrderService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static o => $"/api/v1/sales/orders/{o.Id}"))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("A draft sales order with lines priced by the pricing engine and taxed for the customer on the pricing date");
        orders.MapGet("/{orderId:guid}", async (Guid orderId, OrderService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(orderId, ct), "order", orderId))
            .RequirePermission(SalesPermissions.OrderRead)
            .WithSummary("The order with its priced, taxed lines and their reservation and cancellation state");
        orders.MapPost("/{orderId:guid}/confirm", async (Guid orderId, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.ConfirmAsync(orderId, ct)))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("Reserves what stock is available per line (a short line is backordered) and checks the customer's credit exposure; an excess holds the order for an authorized approver's override (hard scenario 6)");
        orders.MapPost("/{orderId:guid}/retry-backorders", async (Guid orderId, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.RetryBackordersAsync(orderId, ct)))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("Re-attempts reservation for every backordered line of a confirmed order");
        orders.MapPost("/{orderId:guid}/cancel", async (Guid orderId, CancelOrderRequest request, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(orderId, request, ct)))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("Cancels the whole order with a reason, releasing every reservation");
        orders.MapPost("/{orderId:guid}/lines/{lineId:guid}/cancel", async (Guid orderId, Guid lineId, CancelOrderLineRequest request, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelLineAsync(orderId, lineId, request, ct)))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("Cancels part of a line's remaining, unshipped quantity");
        orders.MapPost("/{orderId:guid}/lines/{lineId:guid}/purchase-order", async (Guid orderId, Guid lineId, LinkPurchaseOrderLineRequest request, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.LinkPurchaseOrderLineAsync(orderId, lineId, request, ct)))
            .RequirePermission(SalesPermissions.OrderManage)
            .WithSummary("Records the purchase order line raised for a drop-ship line, once");

        var shipments = sales.MapGroup("/shipments");
        shipments.MapGet("/", async (Guid? companyId, string? status, Guid? orderId, ShipmentService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, orderId, ct)))
            .RequirePermission(SalesPermissions.ShipmentRead)
            .WithSummary("Shipments, newest first, filtered by company, status or order");
        shipments.MapPost("/", async (SaveShipmentRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static s => $"/api/v1/sales/shipments/{s.Id}"))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("A draft shipment of a confirmed order's reserved lines, in full or in part");
        shipments.MapGet("/{shipmentId:guid}", async (Guid shipmentId, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(shipmentId, ct)))
            .RequirePermission(SalesPermissions.ShipmentRead)
            .WithSummary("The shipment with its lines and, once posted, their booked cost of goods sold");
        shipments.MapPut("/{shipmentId:guid}", async (Guid shipmentId, SaveShipmentRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(shipmentId, request, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Re-validates a draft shipment's lines against the order's reservations; only a draft is edited");
        shipments.MapDelete("/{shipmentId:guid}", async (Guid shipmentId, ShipmentService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(shipmentId, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Deletes a draft shipment; a posted one is reversed instead");
        shipments.MapPost("/{shipmentId:guid}/post", async (Guid shipmentId, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(shipmentId, ct)))
            .RequirePermission(SalesPermissions.ShipmentPost)
            .WithSummary("Posts the shipment: consumes the order lines' reservations, moves the stock out and books cost of goods sold");
        shipments.MapPost("/{shipmentId:guid}/reverse", async (Guid shipmentId, ReverseShipmentRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(shipmentId, request, ct)))
            .RequirePermission(SalesPermissions.ShipmentPost)
            .WithSummary("Reverses a posted shipment with a reason: the stock returns and re-reserves for the order");

        return api;
    }
}

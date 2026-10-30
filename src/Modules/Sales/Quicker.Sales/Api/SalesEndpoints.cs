using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Kernel.Time;
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
        shipments.MapPost("/{shipmentId:guid}/release", async (Guid shipmentId, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.ReleaseAsync(shipmentId, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Releases a draft for picking: the warehouse gets a pick list in walking order (lots first expiry first out, bins in pick sequence) and the shipment waits until every line is picked");
        shipments.MapPost("/{shipmentId:guid}/cancel-picking", async (Guid shipmentId, CancelPickingRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelPickingAsync(shipmentId, request, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Takes a shipment back from picking: its pick list is cancelled and the shipment is a draft again");
        shipments.MapPut("/{shipmentId:guid}/packages", async (Guid shipmentId, SavePackagesRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.SavePackagesAsync(shipmentId, request, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Replaces the shipment's packages: type, weight, dimensions, tracking number and which order lines each holds how much of; allowed until the shipment is reversed");
        shipments.MapPut("/{shipmentId:guid}/carrier", async (Guid shipmentId, SaveCarrierRequest request, ShipmentService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveCarrierAsync(shipmentId, request, ct)))
            .RequirePermission(SalesPermissions.ShipmentManage)
            .WithSummary("Sets the carrier and its tracking reference, also after posting");

        var invoices = sales.MapGroup("/invoices");
        invoices.MapGet("/", async (Guid? companyId, string? status, string? kind, Guid? partnerId, InvoiceService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, kind, partnerId, ct)))
            .RequirePermission(SalesPermissions.InvoiceRead)
            .WithSummary("Sales invoices and credit notes, newest first, filtered by company, status, kind or customer");
        invoices.MapGet("/invoicable", async (Guid companyId, Guid partnerId, InvoiceService service, CancellationToken ct) => TypedResults.Ok(await service.InvoicableAsync(companyId, partnerId, ct)))
            .RequirePermission(SalesPermissions.InvoiceRead)
            .WithSummary("What a customer can still be invoiced: posted shipment lines with an uninvoiced quantity and open service lines of its orders");
        invoices.MapPost("/", async (SaveSalesInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static i => $"/api/v1/sales/invoices/{i.Id}"))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("A draft invoice or credit note: lines from shipments and orders at the order's price, items priced by the pricing engine, service lines, or credited invoice lines; taxed on the document date");
        invoices.MapGet("/{invoiceId:guid}", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(invoiceId, ct)))
            .RequirePermission(SalesPermissions.InvoiceRead)
            .WithSummary("The invoice with its lines and, once posted, its receivable open items");
        invoices.MapPut("/{invoiceId:guid}", async (Guid invoiceId, SaveSalesInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(invoiceId, request, ct)))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("Replaces a draft's lines and re-taxes them; only a draft is edited");
        invoices.MapPost("/{invoiceId:guid}/cancel", async (Guid invoiceId, CancelSalesInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(invoiceId, request, ct)))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("Cancels a draft with a reason, keeping its number");
        invoices.MapPost("/{invoiceId:guid}/post", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(invoiceId, ct)))
            .RequirePermission(SalesPermissions.InvoicePost)
            .WithSummary("Posts the invoice: Dr receivables / Cr revenue per line / Cr output tax per code, the tax ledger, one receivable open item per instalment; a credit note naming its invoice is applied to it at once");
        invoices.MapPost("/{invoiceId:guid}/reverse", async (Guid invoiceId, ReverseSalesInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(invoiceId, request, ct)))
            .RequirePermission(SalesPermissions.InvoicePost)
            .WithSummary("Reverses a posted invoice with a reason: the journal and tax ledger mirrored, the open items closed, the billed quantities given back; refused while an open item is settled or deferred revenue was recognised");
        invoices.MapPost("/{invoiceId:guid}/einvoice/submit", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitEinvoiceAsync(invoiceId, ct)))
            .RequirePermission(SalesPermissions.InvoicePost)
            .WithSummary("Submits a posted invoice to its regime's e-invoicing scheme and records the scheme's answer (status, submission id, QR); refused when the regime names no scheme or no adapter is registered for it yet");

        var recurring = sales.MapGroup("/recurring-invoices");
        recurring.MapGet("/", async (Guid? companyId, Guid? partnerId, bool? active, RecurringInvoiceService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, partnerId, active, ct)))
            .RequirePermission(SalesPermissions.InvoiceRead)
            .WithSummary("Recurring invoices by company, customer or active state, with their next run");
        recurring.MapPost("/", async (SaveRecurringInvoiceRequest request, RecurringInvoiceService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/sales/recurring-invoices/{r.Id}"))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("A recurring invoice: a customer's item and service lines issued on a cron schedule in the company's time zone, posted at once or left as a draft to review");
        recurring.MapGet("/{recurringId:guid}", async (Guid recurringId, RecurringInvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(recurringId, ct)))
            .RequirePermission(SalesPermissions.InvoiceRead);
        recurring.MapPut("/{recurringId:guid}", async (Guid recurringId, SaveRecurringInvoiceRequest request, RecurringInvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(recurringId, request, ct)))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("Changes the schedule, lines or settings; a new start date re-plans the next run");
        recurring.MapPost("/{recurringId:guid}/generate", async (Guid recurringId, GenerateRecurringInvoiceRequest request, RecurringInvoiceService service, CancellationToken ct) => ApiProblems.Created(await service.GenerateAsync(recurringId, request, ct), static i => $"/api/v1/sales/invoices/{i.Id}"))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("Issues the invoice for the next due run (or a given date) now and advances the schedule");
        recurring.MapPost("/run", async (Guid? companyId, DateOnly? asOf, RecurringInvoiceService service, IClock clock, CancellationToken ct) => TypedResults.Ok(await service.GenerateDueAsync(companyId, asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct)))
            .RequirePermission(SalesPermissions.InvoiceManage)
            .WithSummary("Generates every recurring invoice due on or before the date, as the daily job does at 00:40 UTC");

        return api;
    }
}

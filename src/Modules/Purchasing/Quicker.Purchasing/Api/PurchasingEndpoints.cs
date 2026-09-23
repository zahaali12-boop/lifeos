using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Purchasing.Application;
using Quicker.Web;

namespace Quicker.Purchasing.Api;

/// <summary>Procure to pay under /api/v1/purchasing: requisitions, requests for quotation, blanket agreements and purchase orders.</summary>
public static class PurchasingEndpoints
{
    public static RouteGroupBuilder MapPurchasingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var purchasing = api.MapGroup("/purchasing").WithTags("Purchasing").RequireAuthorization();

        var requisitions = purchasing.MapGroup("/requisitions");
        requisitions.MapGet("/", async (Guid? companyId, string? status, RequisitionService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(PurchasingPermissions.RequisitionRead);
        requisitions.MapPost("/", async (SaveRequisitionRequest request, RequisitionService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/purchasing/requisitions/{r.Id}"))
            .RequirePermission(PurchasingPermissions.RequisitionManage)
            .WithSummary("A draft requisition: items (or free text with a unit), quantities, estimated prices, the warehouse and the suggested supplier per line");
        requisitions.MapGet("/{requisitionId:guid}", async (Guid requisitionId, RequisitionService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(requisitionId, ct), "purchase_requisition", requisitionId))
            .RequirePermission(PurchasingPermissions.RequisitionRead);
        requisitions.MapPut("/{requisitionId:guid}", async (Guid requisitionId, SaveRequisitionRequest request, RequisitionService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(requisitionId, request, ct)))
            .RequirePermission(PurchasingPermissions.RequisitionManage);
        requisitions.MapPost("/{requisitionId:guid}/submit", async (Guid requisitionId, RequisitionService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitAsync(requisitionId, ct)))
            .RequirePermission(PurchasingPermissions.RequisitionManage)
            .WithSummary("Runs the active approval definition for purchase_requisition; without one the requisition is approved at once");
        requisitions.MapPost("/{requisitionId:guid}/cancel", async (Guid requisitionId, RequisitionService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(requisitionId, ct)))
            .RequirePermission(PurchasingPermissions.RequisitionManage);
        requisitions.MapPost("/{requisitionId:guid}/orders", async (Guid requisitionId, CreateOrdersFromRequisitionRequest? request, RequisitionService service, CancellationToken ct) => ApiProblems.Ok(await service.CreateOrdersAsync(requisitionId, request ?? new CreateOrdersFromRequisitionRequest(), ct)))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Purchase order drafts from the approved lines, one per supplier (the suggested one, or partnerId for all)");

        var rfqs = purchasing.MapGroup("/rfqs");
        rfqs.MapGet("/", async (Guid? companyId, string? status, RfqService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(PurchasingPermissions.RfqRead);
        rfqs.MapPost("/", async (SaveRfqRequest request, RfqService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/purchasing/rfqs/{r.Id}"))
            .RequirePermission(PurchasingPermissions.RfqManage)
            .WithSummary("Lines to price (from requisition lines or ad hoc) and the suppliers to invite");
        rfqs.MapGet("/{rfqId:guid}", async (Guid rfqId, RfqService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(rfqId, ct), "purchase_rfq", rfqId))
            .RequirePermission(PurchasingPermissions.RfqRead);
        rfqs.MapPut("/{rfqId:guid}", async (Guid rfqId, SaveRfqRequest request, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(rfqId, request, ct)))
            .RequirePermission(PurchasingPermissions.RfqManage);
        rfqs.MapPost("/{rfqId:guid}/invite", async (Guid rfqId, InviteSuppliersRequest request, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.InviteAsync(rfqId, request, ct)))
            .RequirePermission(PurchasingPermissions.RfqManage);
        rfqs.MapPost("/{rfqId:guid}/send", async (Guid rfqId, SendRfqRequest? request, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.SendAsync(rfqId, request ?? new SendRfqRequest(), ct)))
            .RequirePermission(PurchasingPermissions.RfqManage)
            .WithSummary("Emails the request to every invited supplier not yet sent to (emails: {partnerId: address} overrides the partner's)");
        rfqs.MapPost("/{rfqId:guid}/quotes", async (Guid rfqId, SaveQuoteRequest request, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.RecordQuoteAsync(rfqId, request, ct)))
            .RequirePermission(PurchasingPermissions.RfqManage)
            .WithSummary("Records or replaces a supplier's quote: currency, validity, lead time, freight and charges, a price per request line");
        rfqs.MapPost("/{rfqId:guid}/suppliers/{partnerId:guid}/decline", async (Guid rfqId, Guid partnerId, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.DeclineAsync(rfqId, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.RfqManage);
        rfqs.MapPost("/{rfqId:guid}/compare", async (Guid rfqId, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.CompareAsync(rfqId, ct)))
            .RequirePermission(PurchasingPermissions.RfqRead)
            .WithSummary("Ranks the quotes: complete ones first, then landed total in the company's currency (goods + freight + charges at today's spot rate), then lead time");
        rfqs.MapPost("/{rfqId:guid}/award", async (Guid rfqId, AwardRequest request, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.AwardAsync(rfqId, request, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Awards a quote and returns the purchase order draft created from it");
        rfqs.MapPost("/{rfqId:guid}/close", async (Guid rfqId, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.SetStatusAsync(rfqId, "close", ct)))
            .RequirePermission(PurchasingPermissions.RfqManage);
        rfqs.MapPost("/{rfqId:guid}/cancel", async (Guid rfqId, RfqService service, CancellationToken ct) => ApiProblems.Ok(await service.SetStatusAsync(rfqId, "cancel", ct)))
            .RequirePermission(PurchasingPermissions.RfqManage);

        var agreements = purchasing.MapGroup("/agreements");
        agreements.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, BlanketAgreementService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.AgreementRead);
        agreements.MapPost("/", async (SaveBlanketAgreementRequest request, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static a => $"/api/v1/purchasing/agreements/{a.Id}"))
            .RequirePermission(PurchasingPermissions.AgreementManage)
            .WithSummary("A blanket agreement: agreed quantities and prices per item with a supplier for a period, optionally a committed amount");
        agreements.MapGet("/{agreementId:guid}", async (Guid agreementId, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(agreementId, ct), "purchase_agreement", agreementId))
            .RequirePermission(PurchasingPermissions.AgreementRead);
        agreements.MapPut("/{agreementId:guid}", async (Guid agreementId, SaveBlanketAgreementRequest request, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(agreementId, request, ct)))
            .RequirePermission(PurchasingPermissions.AgreementManage);
        agreements.MapPost("/{agreementId:guid}/activate", async (Guid agreementId, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Ok(await service.SetStatusAsync(agreementId, "activate", ct)))
            .RequirePermission(PurchasingPermissions.AgreementManage);
        agreements.MapPost("/{agreementId:guid}/close", async (Guid agreementId, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Ok(await service.SetStatusAsync(agreementId, "close", ct)))
            .RequirePermission(PurchasingPermissions.AgreementManage);
        agreements.MapPost("/{agreementId:guid}/cancel", async (Guid agreementId, BlanketAgreementService service, CancellationToken ct) => ApiProblems.Ok(await service.SetStatusAsync(agreementId, "cancel", ct)))
            .RequirePermission(PurchasingPermissions.AgreementManage);

        var orders = purchasing.MapGroup("/orders");
        orders.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, PurchaseOrderService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.OrderRead)
            .WithSummary("status=open lists approved, sent and partially received orders");
        orders.MapPost("/", async (SavePurchaseOrderRequest request, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static o => $"/api/v1/purchasing/orders/{o.Id}"))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("A draft order in the supplier's currency (rate resolved at the order date), lines priced or released from an agreement line");
        orders.MapGet("/{orderId:guid}", async (Guid orderId, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(orderId, ct), "purchase_order", orderId))
            .RequirePermission(PurchasingPermissions.OrderRead)
            .WithSummary("The order with its lines, revisions (change orders) and budget commitments");
        orders.MapPut("/{orderId:guid}", async (Guid orderId, SavePurchaseOrderRequest request, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(orderId, request, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage);
        orders.MapPost("/{orderId:guid}/submit", async (Guid orderId, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitAsync(orderId, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Runs the active approval definition for purchase_order (a rule over the amount routes it); approval records commitments and agreement releases");
        orders.MapPost("/{orderId:guid}/change", async (Guid orderId, ChangeOrderRequest request, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.ChangeAsync(orderId, request, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("A change order on an approved or sent order: keeps the previous revision, re-applies the content and goes through approval again");
        orders.MapPost("/{orderId:guid}/send", async (Guid orderId, SendOrderRequest? request, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.SendAsync(orderId, request ?? new SendOrderRequest(), ct)))
            .RequirePermission(PurchasingPermissions.OrderSend)
            .WithSummary("Emails the order (bilingual HTML) to the supplier's address or the one given");
        orders.MapPost("/{orderId:guid}/cancel", async (Guid orderId, SendOrderRequest? request, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(orderId, request?.Message, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage);
        orders.MapPost("/{orderId:guid}/close", async (Guid orderId, PurchaseOrderService service, CancellationToken ct) => ApiProblems.Ok(await service.CloseAsync(orderId, ct)))
            .RequirePermission(PurchasingPermissions.OrderManage)
            .WithSummary("Closes short: what was not received is cancelled and the open commitments released");

        var receipts = purchasing.MapGroup("/receipts");
        receipts.MapGet("/", async (Guid? companyId, string? status, Guid? orderId, ReceiptService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, orderId, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptRead)
            .WithSummary("Goods receipts of a company, optionally by status or order.");
        receipts.MapGet("/receivable", async (Guid companyId, Guid? orderId, Guid? partnerId, ReceiptService service, CancellationToken ct) => TypedResults.Ok(await service.ReceivableAsync(companyId, orderId, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptRead)
            .WithSummary("Open order lines that can be received, with what the supplier's tolerance still allows.");
        receipts.MapPost("/", async (SaveReceiptRequest request, ReceiptService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/purchasing/receipts/{r.Id}"))
            .RequirePermission(PurchasingPermissions.ReceiptManage)
            .WithSummary("Drafts a goods receipt against one purchase order's open lines.");
        receipts.MapGet("/{receiptId:guid}", async (Guid receiptId, ReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(receiptId, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptRead);
        receipts.MapPut("/{receiptId:guid}", async (Guid receiptId, SaveReceiptRequest request, ReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(receiptId, request, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptManage);
        receipts.MapDelete("/{receiptId:guid}", async (Guid receiptId, ReceiptService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(receiptId, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptManage);
        receipts.MapPost("/{receiptId:guid}/post", async (Guid receiptId, ReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(receiptId, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptPost)
            .WithSummary("Posts the receipt: stock in at the expected cost against GRNI, the order's received quantities updated.");
        receipts.MapPost("/{receiptId:guid}/reverse", async (Guid receiptId, ReverseReceiptRequest request, ReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(receiptId, request, ct)))
            .RequirePermission(PurchasingPermissions.ReceiptPost)
            .WithSummary("Reverses a posted receipt as a whole at its exact cost.");

        return api;
    }
}

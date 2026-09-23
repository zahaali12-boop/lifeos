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

        var invoices = purchasing.MapGroup("/invoices");
        invoices.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, InvoiceService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceRead)
            .WithSummary("Supplier invoices of a company, optionally by status or supplier.");
        invoices.MapGet("/invoicable", async (Guid companyId, Guid partnerId, InvoiceService service, CancellationToken ct) => TypedResults.Ok(await service.InvoicableAsync(companyId, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceRead)
            .WithSummary("What the supplier can still invoice: uninvoiced receipt lines and open service lines of its orders.");
        invoices.MapPost("/", async (SaveInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static i => $"/api/v1/purchasing/invoices/{i.Id}"))
            .RequirePermission(PurchasingPermissions.InvoiceManage)
            .WithSummary("Drafts a supplier invoice: lines against receipt lines, service order lines or expense accounts.");
        invoices.MapGet("/{invoiceId:guid}", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(invoiceId, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceRead);
        invoices.MapPut("/{invoiceId:guid}", async (Guid invoiceId, SaveInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(invoiceId, request, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceManage);
        invoices.MapDelete("/{invoiceId:guid}", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(invoiceId, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceManage);
        invoices.MapPost("/{invoiceId:guid}/submit", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitAsync(invoiceId, ct)))
            .RequirePermission(PurchasingPermissions.InvoiceManage)
            .WithSummary("Matches the invoice; a breach beyond tolerance blocks it until an override, otherwise the workflow decides or it is approved at once.");
        invoices.MapPost("/{invoiceId:guid}/post", async (Guid invoiceId, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(invoiceId, ct)))
            .RequirePermission(PurchasingPermissions.InvoicePost)
            .WithSummary("Posts an approved invoice: receipts re-priced, GRNI settled, AP opened, commitments consumed.");
        invoices.MapPost("/{invoiceId:guid}/reverse", async (Guid invoiceId, ReverseInvoiceRequest request, InvoiceService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(invoiceId, request, ct)))
            .RequirePermission(PurchasingPermissions.InvoicePost)
            .WithSummary("Reverses a posted invoice as a whole.");

        var returns = purchasing.MapGroup("/returns");
        returns.MapGet("/", async (Guid? companyId, string? status, Guid? receiptId, ReturnService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, receiptId, ct)))
            .RequirePermission(PurchasingPermissions.ReturnRead)
            .WithSummary("Supplier returns of a company, optionally by status or receipt.");
        returns.MapGet("/returnable", async (Guid companyId, Guid? receiptId, Guid? partnerId, ReturnService service, CancellationToken ct) => TypedResults.Ok(await service.ReturnableAsync(companyId, receiptId, partnerId, ct)))
            .RequirePermission(PurchasingPermissions.ReturnRead)
            .WithSummary("Posted receipt lines with a quantity still on hand that can go back to the supplier.");
        returns.MapPost("/", async (SaveReturnRequest request, ReturnService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/purchasing/returns/{r.Id}"))
            .RequirePermission(PurchasingPermissions.ReturnManage)
            .WithSummary("Drafts a return to the supplier against one posted receipt.");
        returns.MapGet("/{returnId:guid}", async (Guid returnId, ReturnService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(returnId, ct)))
            .RequirePermission(PurchasingPermissions.ReturnRead);
        returns.MapPut("/{returnId:guid}", async (Guid returnId, SaveReturnRequest request, ReturnService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(returnId, request, ct)))
            .RequirePermission(PurchasingPermissions.ReturnManage);
        returns.MapDelete("/{returnId:guid}", async (Guid returnId, ReturnService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(returnId, ct)))
            .RequirePermission(PurchasingPermissions.ReturnManage);
        returns.MapPost("/{returnId:guid}/post", async (Guid returnId, ReturnService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(returnId, ct)))
            .RequirePermission(PurchasingPermissions.ReturnPost)
            .WithSummary("Posts the return: stock out at the receipt's exact cost, GRNI relieved with the return as reference.");
        returns.MapPost("/{returnId:guid}/reverse", async (Guid returnId, ReverseReturnRequest request, ReturnService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(returnId, request, ct)))
            .RequirePermission(PurchasingPermissions.ReturnPost)
            .WithSummary("Reverses a posted return that has not been credited: the goods come back at the same cost.");

        var intelligence = purchasing.MapGroup("/intelligence");
        intelligence.MapGet("/price-history", async (Guid companyId, Guid? itemId, Guid? partnerId, DateOnly? from, DateOnly? to, SupplierIntelligenceService service, CancellationToken ct) => TypedResults.Ok(await service.PriceHistoryAsync(companyId, itemId, partnerId, from, to, ct)))
            .RequirePermission(PurchasingPermissions.IntelligenceRead)
            .WithSummary("Prices paid and quoted per item and supplier: order, invoice and quote points with their value in the company's currency, and a summary per item and supplier.");
        intelligence.MapGet("/lead-times", async (Guid companyId, Guid? partnerId, DateOnly? from, DateOnly? to, SupplierIntelligenceService service, CancellationToken ct) => TypedResults.Ok(await service.LeadTimesAsync(companyId, partnerId, from, to, ct)))
            .RequirePermission(PurchasingPermissions.IntelligenceRead)
            .WithSummary("Days from order to receipt per supplier (average, median, minimum, maximum) against the supplier's stated lead time, and the share received by the expected date.");
        intelligence.MapGet("/scorecard", async (Guid companyId, DateOnly? asOf, SupplierIntelligenceService service, CancellationToken ct) => ApiProblems.Ok(await service.ScorecardAsync(companyId, asOf, ct)))
            .RequirePermission(PurchasingPermissions.IntelligenceRead)
            .WithSummary("Supplier scorecard over the look-back window: on time, quantity kept (not returned), price within tolerance and invoices matched first time, weighted into a score and a grade.");
        intelligence.MapGet("/scoring-settings", async (Guid companyId, SupplierIntelligenceService service, CancellationToken ct) => ApiProblems.Ok(await service.SettingsAsync(companyId, ct)))
            .RequirePermission(PurchasingPermissions.IntelligenceRead);
        intelligence.MapPut("/scoring-settings", async (SaveScoringSettingsRequest request, SupplierIntelligenceService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveSettingsAsync(request, ct)))
            .RequirePermission(PurchasingPermissions.IntelligenceManage);

        var chargeTypes = purchasing.MapGroup("/charge-types");
        chargeTypes.MapGet("/", async (LandedCostService service, CancellationToken ct) => TypedResults.Ok(await service.ChargeTypesAsync(ct)))
            .RequirePermission(PurchasingPermissions.LandedCostRead);
        chargeTypes.MapPost("/", async (SaveChargeTypeRequest request, LandedCostService service, CancellationToken ct) => ApiProblems.Created(await service.SaveChargeTypeAsync(null, request, ct), static c => $"/api/v1/purchasing/charge-types/{c.Id}"))
            .RequirePermission(PurchasingPermissions.LandedCostManage);
        chargeTypes.MapPut("/{chargeTypeId:guid}", async (Guid chargeTypeId, SaveChargeTypeRequest request, LandedCostService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveChargeTypeAsync(chargeTypeId, request, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostManage);

        var landedCosts = purchasing.MapGroup("/landed-costs");
        landedCosts.MapGet("/", async (Guid? companyId, string? status, LandedCostService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostRead)
            .WithSummary("Landed-cost documents of a company with their charges and allocations (the on-hand versus sold split per receipt line).");
        landedCosts.MapGet("/allocatable", async (Guid companyId, Guid? partnerId, DateOnly? from, LandedCostService service, CancellationToken ct) => TypedResults.Ok(await service.AllocatableAsync(companyId, partnerId, from, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostRead)
            .WithSummary("Posted receipt lines a landed cost can be allocated to, with their value, quantity, weight and volume.");
        landedCosts.MapPost("/", async (SaveLandedCostRequest request, LandedCostService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static d => $"/api/v1/purchasing/landed-costs/{d.Id}"))
            .RequirePermission(PurchasingPermissions.LandedCostManage)
            .WithSummary("Drafts a landed-cost document: charges allocated to receipt lines by value, weight, volume or quantity.");
        landedCosts.MapGet("/{landedCostId:guid}", async (Guid landedCostId, LandedCostService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(landedCostId, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostRead);
        landedCosts.MapPut("/{landedCostId:guid}", async (Guid landedCostId, SaveLandedCostRequest request, LandedCostService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(landedCostId, request, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostManage);
        landedCosts.MapDelete("/{landedCostId:guid}", async (Guid landedCostId, LandedCostService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(landedCostId, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostManage);
        landedCosts.MapPost("/{landedCostId:guid}/post", async (Guid landedCostId, LandedCostService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(landedCostId, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostPost)
            .WithSummary("Posts the document: stock on hand takes its share, what was already sold goes to cost of sales, the clearing account is credited.");
        landedCosts.MapPost("/{landedCostId:guid}/reverse", async (Guid landedCostId, ReverseLandedCostRequest request, LandedCostService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(landedCostId, request, ct)))
            .RequirePermission(PurchasingPermissions.LandedCostPost)
            .WithSummary("Reverses a posted document whose charges are still estimates.");

        return api;
    }
}

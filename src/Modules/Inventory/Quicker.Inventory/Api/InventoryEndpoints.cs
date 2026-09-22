using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Time;
using Quicker.Web;

namespace Quicker.Inventory.Api;

/// <summary>The inventory surface of /api/v1: warehouses and bins, stock balances, search, availability and ledger, reservations, transfers.</summary>
public static class InventoryEndpoints
{
    public static RouteGroupBuilder MapInventoryEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var inventory = api.MapGroup("/inventory").WithTags("Inventory").RequireAuthorization();

        // ------------------------------------------------------------------ warehouses and bins
        var warehouses = inventory.MapGroup("/warehouses");
        warehouses.MapGet("/", async (Guid? companyId, WarehouseService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(InventoryPermissions.WarehouseRead);
        warehouses.MapPost("/", async (SaveWarehouseRequest request, WarehouseService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveAsync(null, request, ct), static w => $"/api/v1/inventory/warehouses/{w.Id}"))
            .RequirePermission(InventoryPermissions.WarehouseManage)
            .WithSummary("A warehouse of a company: kind standard, in_transit (one per company for two-step transfers), consignment, quarantine or virtual; bins optional; negative-stock override");
        warehouses.MapGet("/{warehouseId:guid}", async (Guid warehouseId, WarehouseService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(warehouseId, ct), "warehouse", warehouseId))
            .RequirePermission(InventoryPermissions.WarehouseRead);
        warehouses.MapPut("/{warehouseId:guid}", async (Guid warehouseId, SaveWarehouseRequest request, WarehouseService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(warehouseId, request, ct)))
            .RequirePermission(InventoryPermissions.WarehouseManage);
        warehouses.MapGet("/{warehouseId:guid}/bins", async (Guid warehouseId, WarehouseService service, CancellationToken ct) => ApiProblems.Ok(await service.ListBinsAsync(warehouseId, ct)))
            .RequirePermission(InventoryPermissions.WarehouseRead);
        warehouses.MapPost("/{warehouseId:guid}/bins", async (Guid warehouseId, SaveBinRequest request, WarehouseService service, CancellationToken ct) =>
            ApiProblems.Created(await service.SaveBinAsync(warehouseId, null, request, ct), b => $"/api/v1/inventory/warehouses/{warehouseId}/bins/{b.Id}"))
            .RequirePermission(InventoryPermissions.WarehouseManage);
        warehouses.MapPut("/{warehouseId:guid}/bins/{binId:guid}", async (Guid warehouseId, Guid binId, SaveBinRequest request, WarehouseService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveBinAsync(warehouseId, binId, request, ct)))
            .RequirePermission(InventoryPermissions.WarehouseManage);
        warehouses.MapDelete("/{warehouseId:guid}/bins/{binId:guid}", async (Guid warehouseId, Guid binId, WarehouseService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteBinAsync(warehouseId, binId, ct)))
            .RequirePermission(InventoryPermissions.WarehouseManage);

        // ------------------------------------------------------------------ stock
        var stock = inventory.MapGroup("/stock");
        stock.MapGet("/", async (Guid? companyId, string? q, Guid? warehouseId, bool? onlyAvailable, int? limit, string? cursor, StockInquiryService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SearchAsync(companyId, q, warehouseId, onlyAvailable ?? false, new PageRequest(limit, cursor), ct)))
            .RequirePermission(InventoryPermissions.StockRead)
            .WithSummary("Global stock search: one row per item and warehouse with on hand, reserved and available; q matches the item code or name in any language");
        stock.MapGet("/balances", async (Guid companyId, Guid? itemId, Guid? warehouseId, bool? includeZero, StockInquiryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BalancesAsync(companyId, itemId, warehouseId, includeZero ?? false, ct)))
            .RequirePermission(InventoryPermissions.StockRead)
            .WithSummary("Balances per item, variant, warehouse, bin, lot and serial");
        stock.MapGet("/availability", async (Guid companyId, Guid itemId, Guid warehouseId, Guid? variantId, StockInquiryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AvailabilityAsync(companyId, itemId, warehouseId, variantId, ct)))
            .RequirePermission(InventoryPermissions.StockRead)
            .WithSummary("Available to promise: on hand minus reservations and quality holds, with the quantity in transit to the warehouse");
        stock.MapGet("/ledger", async (Guid companyId, Guid? itemId, Guid? warehouseId, DateOnly? from, DateOnly? to, string? sourceDocumentType, Guid? sourceDocumentId, int? limit, string? cursor, StockInquiryService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.LedgerAsync(companyId, itemId, warehouseId, from, to, sourceDocumentType, sourceDocumentId, new PageRequest(limit, cursor), ct)))
            .RequirePermission(InventoryPermissions.StockRead)
            .WithSummary("The stock ledger newest first, paged; every entry names its posting and source document");

        // ------------------------------------------------------------------ reservations
        var reservations = inventory.MapGroup("/reservations");
        reservations.MapGet("/", async (string sourceDocumentType, Guid sourceDocumentId, IStockReservations service, CancellationToken ct) => TypedResults.Ok(await service.ForDocumentAsync(sourceDocumentType, sourceDocumentId, ct)))
            .RequirePermission(InventoryPermissions.StockRead);
        reservations.MapPost("/", async (ReserveStockRequest request, ReservationService service, CancellationToken ct) =>
            ApiProblems.Created(await service.ReserveAsync(new ReservationRequest(request.CompanyId, request.ItemId, request.Quantity, request.WarehouseId, request.SourceDocumentType, request.SourceDocumentId, request.SourceLineId,
                request.UomId, request.VariantId, request.BinId, request.LotId, request.SerialId, request.ExpiresOn, request.Reason), ct), static r => $"/api/v1/inventory/reservations/{r.Id}"))
            .RequirePermission(InventoryPermissions.ReservationManage)
            .WithSummary("Holds available quantity for a document; refused when less is available than asked");
        reservations.MapPost("/{reservationId:guid}/release", async (Guid reservationId, ReleaseReservationRequest? request, ReservationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ReleaseAsync(reservationId, request?.Reason, ct)))
            .RequirePermission(InventoryPermissions.ReservationManage);

        // ------------------------------------------------------------------ transfers
        var transfers = inventory.MapGroup("/transfers");
        transfers.MapGet("/", async (Guid? companyId, string? status, Guid? warehouseId, TransferService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, warehouseId, ct)))
            .RequirePermission(InventoryPermissions.TransferRead);
        transfers.MapPost("/", async (SaveTransferRequest request, TransferService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static t => $"/api/v1/inventory/transfers/{t.Id}"))
            .RequirePermission(InventoryPermissions.TransferManage)
            .WithSummary("A draft transfer between two warehouses of a company; shipping moves the stock to the in-transit warehouse, receiving moves it in");
        transfers.MapGet("/{transferId:guid}", async (Guid transferId, TransferService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(transferId, ct), "transfer", transferId))
            .RequirePermission(InventoryPermissions.TransferRead);
        transfers.MapPut("/{transferId:guid}", async (Guid transferId, SaveTransferRequest request, TransferService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(transferId, request, ct)))
            .RequirePermission(InventoryPermissions.TransferManage);
        transfers.MapPost("/{transferId:guid}/ship", async (Guid transferId, ShipTransferRequest? request, TransferService service, CancellationToken ct) => ApiProblems.Ok(await service.ShipAsync(transferId, request ?? new ShipTransferRequest(), ct)))
            .RequirePermission(InventoryPermissions.TransferShip)
            .WithSummary("Ships the requested (or given) quantities out of the source into transit and numbers the transfer; exactly one of two concurrent ships of the last unit succeeds");
        transfers.MapPost("/{transferId:guid}/receive", async (Guid transferId, ReceiveTransferRequest? request, TransferService service, CancellationToken ct) => ApiProblems.Ok(await service.ReceiveAsync(transferId, request ?? new ReceiveTransferRequest(), ct)))
            .RequirePermission(InventoryPermissions.TransferReceive)
            .WithSummary("Receives what is in transit (or the given quantities) into the destination; partial receipts leave the transfer partially received");
        transfers.MapPost("/{transferId:guid}/cancel", async (Guid transferId, TransferService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(transferId, ct)))
            .RequirePermission(InventoryPermissions.TransferManage);

        // ------------------------------------------------------------------ costing (ADR-0008)
        var costing = inventory.MapGroup("/costing");
        costing.MapGet("/item-cost", async (Guid companyId, Guid itemId, Guid? warehouseId, DateOnly? asOf, CostingService service, CancellationToken ct) =>
            await service.CostAsync(companyId, itemId, warehouseId, asOf, ct) is { } cost ? Results.Ok(cost) : ApiProblems.From(Kernel.Results.Error.NotFound("item", itemId)))
            .RequirePermission(InventoryPermissions.CostingRead)
            .WithSummary("The item's cost in its cost scope at a date: running quantity, value and average, the last and the standard cost, and whether a re-application is pending");
        costing.MapGet("/valuation", async (Guid companyId, DateOnly? asOf, Guid? warehouseId, Guid? itemId, bool? includeZero, CostInquiryService service, IClock clock, CancellationToken ct) =>
            TypedResults.Ok(await service.ValuationAsync(companyId, asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), warehouseId, itemId, includeZero ?? false, ct)))
            .RequirePermission(InventoryPermissions.CostingRead)
            .WithSummary("Inventory valuation at a date per item and warehouse: quantity, actual and expected value; equals the inventory accounts at that date");
        costing.MapGet("/entries/{sleId:guid}", async (Guid sleId, CostInquiryService service, CancellationToken ct) => ApiProblems.Ok(await service.ExplainAsync(sleId, ct)))
            .RequirePermission(InventoryPermissions.CostingRead)
            .WithSummary("Why did this cost change: the entry's value entries in order with their reasons, the layers it consumed or the entries that consumed it, and the adjustment runs involved");
        costing.MapGet("/runs", async (Guid? companyId, Guid? itemId, string? status, int? limit, string? cursor, CostInquiryService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.RunsAsync(companyId, itemId, status, new PageRequest(limit, cursor), ct)))
            .RequirePermission(InventoryPermissions.CostingRead)
            .WithSummary("Cost adjustment runs newest first: the trigger document, what was walked and re-applied, what was posted");
        costing.MapGet("/runs/{runId:guid}", async (Guid runId, CostInquiryService service, CancellationToken ct) => ApiProblems.Ok(await service.RunAsync(runId, ct)))
            .RequirePermission(InventoryPermissions.CostingRead);
        costing.MapPost("/runs/{runId:guid}/run", async (Guid runId, CostingService service, CancellationToken ct) => ApiProblems.Ok(await service.RunAsync(runId, ct)))
            .RequirePermission(InventoryPermissions.CostingManage)
            .WithSummary("Runs a queued re-application now instead of waiting for the worker");
        costing.MapGet("/standard-costs", async (Guid companyId, Guid itemId, CostInquiryService service, CancellationToken ct) => TypedResults.Ok(await service.StandardCostsAsync(companyId, itemId, ct)))
            .RequirePermission(InventoryPermissions.CostingRead);
        costing.MapPost("/standard-costs", async (SetStandardCostRequest request, CostingService service, CancellationToken ct) =>
            ApiProblems.Created((await service.SetStandardCostAsync(request.CompanyId, request.ItemId, request.StandardCost, request.EffectiveFrom, request.Reason, ct)).Map(CostInquiryService.Map), static v => $"/api/v1/inventory/costing/standard-costs?companyId={v.CompanyId}&itemId={v.ItemId}"))
            .RequirePermission(InventoryPermissions.CostingManage)
            .WithSummary("A new standard cost version from a date; under standard costing the stock on hand is revalued and later movements re-applied");
        costing.MapPost("/inbound-adjustments", async (InboundCostAdjustmentRequest request, CostingService service, CancellationToken ct) => ApiProblems.Ok(await service.AdjustInboundCostAsync(request, ct)))
            .RequirePermission(InventoryPermissions.CostingManage)
            .WithSummary("Settles an inbound entry's expected cost with the invoiced one, or adds a landed cost to it, and re-applies everything it fed (hard scenarios 1 and 2)");

        // ------------------------------------------------------------------ reason codes, adjustments, revaluations, assemblies (roadmap 3.4)
        var reasons = inventory.MapGroup("/reason-codes");
        reasons.MapGet("/", async (string? appliesTo, bool? includeInactive, ReasonCodeService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(appliesTo, includeInactive ?? false, ct)))
            .RequirePermission(InventoryPermissions.StockRead);
        reasons.MapPost("/", async (SaveReasonCodeRequest request, ReasonCodeService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/inventory/reason-codes/{r.Id}"))
            .RequirePermission(InventoryPermissions.ReasonCodeManage)
            .WithSummary("A reason code for adjustments, scrap, counts, returns or transfer shortages; it may redirect the movement to another expense account and require a note");
        reasons.MapPut("/{reasonId:guid}", async (Guid reasonId, SaveReasonCodeRequest request, ReasonCodeService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(reasonId, request, ct)))
            .RequirePermission(InventoryPermissions.ReasonCodeManage);

        var adjustments = inventory.MapGroup("/adjustments");
        adjustments.MapGet("/", async (Guid? companyId, string? status, string? kind, Guid? warehouseId, AdjustmentService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, kind, warehouseId, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentRead);
        adjustments.MapPost("/", async (SaveAdjustmentRequest request, AdjustmentService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static a => $"/api/v1/inventory/adjustments/{a.Id}"))
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("A draft adjustment (positive, negative, scrap or opening) with a reason code per line; submit posts it, or sends it for approval when the company requires one");
        adjustments.MapGet("/{adjustmentId:guid}", async (Guid adjustmentId, AdjustmentService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(adjustmentId, ct), "adjustment", adjustmentId))
            .RequirePermission(InventoryPermissions.AdjustmentRead);
        adjustments.MapPut("/{adjustmentId:guid}", async (Guid adjustmentId, SaveAdjustmentRequest request, AdjustmentService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(adjustmentId, request, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentManage);
        adjustments.MapPost("/{adjustmentId:guid}/submit", async (Guid adjustmentId, AdjustmentService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitAsync(adjustmentId, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentManage)
            .WithSummary("Posts the adjustment, or leaves it pending approval when the company setting inventory.adjustments.approval is 'required'");
        adjustments.MapPost("/{adjustmentId:guid}/approve", async (Guid adjustmentId, AdjustmentService service, CancellationToken ct) => ApiProblems.Ok(await service.ApproveAsync(adjustmentId, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentApprove)
            .WithSummary("Approves and posts; the submitter cannot approve their own adjustment");
        adjustments.MapPost("/{adjustmentId:guid}/reject", async (Guid adjustmentId, RejectRequest request, AdjustmentService service, CancellationToken ct) => ApiProblems.Ok(await service.RejectAsync(adjustmentId, request, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentApprove);
        adjustments.MapPost("/{adjustmentId:guid}/cancel", async (Guid adjustmentId, AdjustmentService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(adjustmentId, ct)))
            .RequirePermission(InventoryPermissions.AdjustmentManage);

        var revaluations = inventory.MapGroup("/revaluations");
        revaluations.MapGet("/", async (Guid? companyId, string? status, RevaluationService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(InventoryPermissions.CostingRead);
        revaluations.MapPost("/", async (SaveRevaluationRequest request, RevaluationService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static r => $"/api/v1/inventory/revaluations/{r.Id}"))
            .RequirePermission(InventoryPermissions.CostingManage)
            .WithSummary("A draft NRV write-down (IAS 2; a reversal never lifts the value above cost) or manual revaluation of the stock on hand per item at a date");
        revaluations.MapGet("/{revaluationId:guid}", async (Guid revaluationId, RevaluationService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(revaluationId, ct), "revaluation", revaluationId))
            .RequirePermission(InventoryPermissions.CostingRead);
        revaluations.MapPut("/{revaluationId:guid}", async (Guid revaluationId, SaveRevaluationRequest request, RevaluationService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(revaluationId, request, ct)))
            .RequirePermission(InventoryPermissions.CostingManage);
        revaluations.MapPost("/{revaluationId:guid}/post", async (Guid revaluationId, RevaluationService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(revaluationId, ct)))
            .RequirePermission(InventoryPermissions.CostingManage)
            .WithSummary("Posts the revaluation through the costing engine (Inventory against InventoryWriteDown) and re-applies later movements");
        revaluations.MapPost("/{revaluationId:guid}/cancel", async (Guid revaluationId, RevaluationService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(revaluationId, ct)))
            .RequirePermission(InventoryPermissions.CostingManage);

        var assemblies = inventory.MapGroup("/assemblies");
        assemblies.MapGet("/", async (Guid? companyId, string? status, AssemblyService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(InventoryPermissions.AssemblyRead);
        assemblies.MapPost("/", async (SaveAssemblyRequest request, AssemblyService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static a => $"/api/v1/inventory/assemblies/{a.Id}"))
            .RequirePermission(InventoryPermissions.AssemblyManage)
            .WithSummary("A draft assembly build; without lines, the item's active bill of material decides what is consumed");
        assemblies.MapGet("/{assemblyId:guid}", async (Guid assemblyId, AssemblyService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(assemblyId, ct), "assembly", assemblyId))
            .RequirePermission(InventoryPermissions.AssemblyRead);
        assemblies.MapPut("/{assemblyId:guid}", async (Guid assemblyId, SaveAssemblyRequest request, AssemblyService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(assemblyId, request, ct)))
            .RequirePermission(InventoryPermissions.AssemblyManage);
        assemblies.MapPost("/{assemblyId:guid}/post", async (Guid assemblyId, AssemblyService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(assemblyId, ct)))
            .RequirePermission(InventoryPermissions.AssemblyPost)
            .WithSummary("Consumes the components and produces the assembly in one posting; the output is valued at what the components cost");
        assemblies.MapPost("/{assemblyId:guid}/cancel", async (Guid assemblyId, AssemblyService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(assemblyId, ct)))
            .RequirePermission(InventoryPermissions.AssemblyManage);

        return inventory;
    }
}

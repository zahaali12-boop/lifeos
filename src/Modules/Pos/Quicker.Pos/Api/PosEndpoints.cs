using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Pos.Application;
using Quicker.Web;

namespace Quicker.Pos.Api;

/// <summary>Point of sale under /api/v1/pos: registers and the catalogue a till caches, sessions, and the orders a till syncs.</summary>
public static class PosEndpoints
{
    public static RouteGroupBuilder MapPosEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var pos = api.MapGroup("/pos").WithTags("Point of sale").RequireAuthorization();

        var registers = pos.MapGroup("/registers");
        registers.MapGet("/", async (Guid? companyId, RegisterService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(PosPermissions.RegisterRead)
            .WithSummary("The registers of a company with their warehouse, cash box, card account, rules and the open session.");
        registers.MapPost("/", async (SaveRegisterRequest request, RegisterService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static r => $"/api/v1/pos/registers/{r.Id}"))
            .RequirePermission(PosPermissions.RegisterManage)
            .WithSummary("Creates a register: the till's warehouse (and bin), cash box, card account, price list, walk-in customer, cash rounding and what its cashier may do.");
        registers.MapGet("/{registerId:guid}", async (Guid registerId, RegisterService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(registerId, ct)))
            .RequirePermission(PosPermissions.RegisterRead);
        registers.MapPut("/{registerId:guid}", async (Guid registerId, SaveRegisterRequest request, RegisterService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(registerId, request, ct)))
            .RequirePermission(PosPermissions.RegisterManage);
        registers.MapGet("/{registerId:guid}/catalogue", async (Guid registerId, RegisterService service, CancellationToken ct) => ApiProblems.Ok(await service.CatalogueAsync(registerId, ct)))
            .RequirePermission(PosPermissions.RegisterRead)
            .WithSummary("What the till caches to sell offline: the register's rules and every sellable item priced on the register's price list and taxed as of today.");
        registers.MapPost("/{registerId:guid}/sessions", async (Guid registerId, OpenSessionRequest request, SessionService service, CancellationToken ct) => ApiProblems.Created(await service.OpenAsync(registerId, request, ct), static s => $"/api/v1/pos/sessions/{s.Id}"))
            .RequirePermission(PosPermissions.SessionOperate)
            .WithSummary("Opens a session on the register with the cashier's float; one session is open per register at a time.");

        var sessions = pos.MapGroup("/sessions");
        sessions.MapGet("/", async (Guid? companyId, Guid? registerId, string? status, SessionService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, registerId, status, ct)))
            .RequirePermission(PosPermissions.SessionRead)
            .WithSummary("Sessions, newest first, with the float, the takings by tender, the expected and counted cash and the difference.");
        sessions.MapGet("/{sessionId:guid}", async (Guid sessionId, SessionService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(sessionId, ct)))
            .RequirePermission(PosPermissions.SessionRead);
        sessions.MapPost("/{sessionId:guid}/close", async (Guid sessionId, CloseSessionRequest request, SessionService service, CancellationToken ct) => ApiProblems.Ok(await service.CloseAsync(sessionId, request, ct)))
            .RequirePermission(PosPermissions.SessionOperate)
            .WithSummary("Closes the session with the cash counted; a difference against the expected cash is booked on the cash box against suspense.");

        var orders = pos.MapGroup("/orders");
        orders.MapGet("/", async (Guid? companyId, Guid? registerId, Guid? sessionId, Guid? partnerId, DateOnly? from, DateOnly? to, OrderService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, registerId, sessionId, partnerId, from, to, ct)))
            .RequirePermission(PosPermissions.OrderRead)
            .WithSummary("Orders synced from the tills, newest first, with their lines, tenders and the journal, stock and e-invoice they produced.");
        orders.MapPost("/", async (SubmitOrderRequest request, OrderService service, CancellationToken ct) =>
            {
                var result = await service.SubmitAsync(request, ct);
                if (result.IsFailure)
                {
                    return ApiProblems.From(result.Error!);
                }

                return result.Value.Replayed ? Results.Ok(result.Value) : Results.Created($"/api/v1/pos/orders/{result.Value.Id}", result.Value);
            })
            .Produces<PosOrderSummary>(StatusCodes.Status201Created)
            .Produces<PosOrderSummary>(StatusCodes.Status200OK)
            .RequirePermission(PosPermissions.OrderSell)
            .WithSummary("Syncs an order from a till: validated, numbered and posted (journal, stock, cash, receivable, tax, e-invoice) in one go; sent again under the same device id it answers 200 with the order already posted.");
        orders.MapGet("/{orderId:guid}", async (Guid orderId, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(orderId, ct)))
            .RequirePermission(PosPermissions.OrderRead);
        orders.MapPost("/{orderId:guid}/einvoice", async (Guid orderId, OrderService service, CancellationToken ct) => ApiProblems.Ok(await service.SubmitEinvoiceAsync(orderId, false, ct)))
            .RequirePermission(PosPermissions.OrderRead)
            .WithSummary("Submits the order to its e-invoicing scheme by hand (a device set to submit by hand, or a retry after the scheme refused).");

        return api;
    }
}

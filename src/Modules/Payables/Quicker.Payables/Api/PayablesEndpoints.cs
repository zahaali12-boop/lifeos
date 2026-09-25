using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Payables.Application;
using Quicker.Web;

namespace Quicker.Payables.Api;

/// <summary>The payables subledger under /api/v1/payables: open items and aging, holds, settlements and payment proposals.</summary>
public static class PayablesEndpoints
{
    public static RouteGroupBuilder MapPayablesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var payables = api.MapGroup("/payables").WithTags("Payables").RequireAuthorization();

        var items = payables.MapGroup("/open-items");
        items.MapGet("/", async (Guid companyId, Guid? partnerId, string? status, string? kind, PayablesService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, partnerId, status, kind, ct)))
            .RequirePermission(PayablesPermissions.OpenItemRead)
            .WithSummary("Payable open items of a company: by supplier, status (open, partially_settled, settled, reversed, or live for the first two) and kind.");
        items.MapGet("/aging", async (Guid companyId, DateOnly? asOf, Guid? partnerId, PayablesService service, CancellationToken ct) => ApiProblems.Ok(await service.AgingAsync(companyId, asOf, partnerId, ct)))
            .RequirePermission(PayablesPermissions.OpenItemRead)
            .WithSummary("Aging at any date, per supplier, in the company's currency: computed from the items and the settlements dated up to it.");
        payables.MapGet("/statement", async (Guid companyId, Guid partnerId, DateOnly? from, DateOnly? to, PayablesService service, CancellationToken ct) => ApiProblems.Ok(await service.StatementAsync(companyId, partnerId, from, to, ct)))
            .RequirePermission(PayablesPermissions.OpenItemRead)
            .WithSummary("A supplier's statement over a period, per document currency: opening balance, documents and reversals with a running balance, closing balance.");
        items.MapGet("/{itemId:guid}", async (Guid itemId, PayablesService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(itemId, ct)))
            .RequirePermission(PayablesPermissions.OpenItemRead);
        items.MapPost("/{itemId:guid}/hold", async (Guid itemId, HoldOpenItemRequest request, PayablesService service, CancellationToken ct) => ApiProblems.Ok(await service.HoldAsync(itemId, request, ct)))
            .RequirePermission(PayablesPermissions.OpenItemManage)
            .WithSummary("Holds an item: proposals skip it and nothing is applied to it until released.");
        items.MapPost("/{itemId:guid}/release", async (Guid itemId, PayablesService service, CancellationToken ct) => ApiProblems.Ok(await service.ReleaseAsync(itemId, ct)))
            .RequirePermission(PayablesPermissions.OpenItemManage);

        var settlements = payables.MapGroup("/settlements");
        settlements.MapGet("/", async (Guid companyId, Guid? openItemId, Guid? partnerId, SettlementService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, openItemId, partnerId, ct)))
            .RequirePermission(PayablesPermissions.OpenItemRead)
            .WithSummary("Settlements of a company, optionally those touching one item or one supplier; reversals are mirror rows naming what they reverse.");
        settlements.MapPost("/apply", async (ApplyRequest request, SettlementService service, CancellationToken ct) => ApiProblems.Ok(await service.ApplyAsync(request, ct)))
            .RequirePermission(PayablesPermissions.SettlementPost)
            .WithSummary("Applies a debit note, a payment on account or an advance to an invoice open item; a rate difference is realised FX.");
        settlements.MapPost("/{settlementId:guid}/reverse", async (Guid settlementId, ReverseSettlementRequest request, SettlementService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(settlementId, request, ct)))
            .RequirePermission(PayablesPermissions.SettlementPost)
            .WithSummary("Reverses an application: both items reopen at their booked values, the FX journal is mirrored.");

        var proposals = payables.MapGroup("/proposals");
        proposals.MapGet("/", async (Guid? companyId, string? status, ProposalService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, ct)))
            .RequirePermission(PayablesPermissions.ProposalRead);
        proposals.MapPost("/", async (SaveProposalRequest request, ProposalService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static p => $"/api/v1/payables/proposals/{p.Id}"))
            .RequirePermission(PayablesPermissions.ProposalManage)
            .WithSummary("Proposes what to pay by a date in one currency: unheld invoice items due by then and those whose early-payment discount is still open.");
        proposals.MapGet("/{proposalId:guid}", async (Guid proposalId, ProposalService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(proposalId, ct)))
            .RequirePermission(PayablesPermissions.ProposalRead);
        proposals.MapPut("/{proposalId:guid}/lines", async (Guid proposalId, UpdateProposalLinesRequest request, ProposalService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateLinesAsync(proposalId, request, ct)))
            .RequirePermission(PayablesPermissions.ProposalManage)
            .WithSummary("Selects, deselects or reduces lines; a partial amount forfeits the discount.");
        proposals.MapPost("/{proposalId:guid}/approve", async (Guid proposalId, ProposalService service, CancellationToken ct) => ApiProblems.Ok(await service.ApproveAsync(proposalId, ct)))
            .RequirePermission(PayablesPermissions.ProposalApprove);
        proposals.MapPost("/{proposalId:guid}/cancel", async (Guid proposalId, ProposalService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(proposalId, ct)))
            .RequirePermission(PayablesPermissions.ProposalManage);
        proposals.MapDelete("/{proposalId:guid}", async (Guid proposalId, ProposalService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(proposalId, ct)))
            .RequirePermission(PayablesPermissions.ProposalManage);

        return api;
    }
}

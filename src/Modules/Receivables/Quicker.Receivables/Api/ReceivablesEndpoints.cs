using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Receivables.Application;
using Quicker.Receivables.Contracts;
using Quicker.Web;

namespace Quicker.Receivables.Api;

/// <summary>The receivables subledger under /api/v1/receivables: open items and aging, customer statements, holds and applications.</summary>
public static class ReceivablesEndpoints
{
    public static RouteGroupBuilder MapReceivablesEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var receivables = api.MapGroup("/receivables").WithTags("Receivables").RequireAuthorization();

        var items = receivables.MapGroup("/open-items");
        items.MapGet("/", async (Guid companyId, Guid? partnerId, string? status, string? kind, ReceivablesService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, partnerId, status, kind, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead)
            .WithSummary("Receivable open items of a company: by customer, status (open, partially_settled, settled, reversed, or live for the first two) and kind.");
        items.MapGet("/aging", async (Guid companyId, DateOnly? asOf, Guid? partnerId, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.AgingAsync(companyId, asOf, partnerId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead)
            .WithSummary("Aging at any date, per customer, in the company's currency: computed from the items and the settlements dated up to it.");
        receivables.MapGet("/statement", async (Guid companyId, Guid partnerId, DateOnly? from, DateOnly? to, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.StatementAsync(companyId, partnerId, from, to, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead)
            .WithSummary("A customer's statement over a period, per document currency: opening balance, documents and reversals with a running balance, closing balance.");
        items.MapGet("/{itemId:guid}", async (Guid itemId, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(itemId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead);
        items.MapPost("/{itemId:guid}/hold", async (Guid itemId, HoldReceivableRequest request, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.HoldAsync(itemId, request, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemManage)
            .WithSummary("Holds an item: nothing is applied to it until released.");
        items.MapPost("/{itemId:guid}/release", async (Guid itemId, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.ReleaseAsync(itemId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemManage);
        items.MapPost("/{itemId:guid}/promise", async (Guid itemId, PromiseToPayRequest request, ReceivablesService service, CancellationToken ct) => ApiProblems.Ok(await service.PromiseAsync(itemId, request, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemManage)
            .WithSummary("Records the customer's promise to pay an item by a date (a null date clears it); dunning leaves the item alone until then.");
        items.MapPost("/{itemId:guid}/write-off", async (Guid itemId, WriteOffRequest request, WriteOffService service, CancellationToken ct) => ApiProblems.Created(await service.WriteOffAsync(itemId, request, ct), static w => $"/api/v1/receivables/write-offs/{w.Id}"))
            .RequirePermission(ReceivablesPermissions.WriteOffPost)
            .WithSummary("Writes off what is open on the item, or part of it: Dr bad debt (or the small-balance account) / Cr receivables, the item settled by the write-off.");

        var writeOffs = receivables.MapGroup("/write-offs");
        writeOffs.MapGet("/", async (Guid companyId, Guid? partnerId, string? status, WriteOffService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, partnerId, status, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead);
        writeOffs.MapGet("/{writeOffId:guid}", async (Guid writeOffId, WriteOffService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(writeOffId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead);
        writeOffs.MapPost("/{writeOffId:guid}/reverse", async (Guid writeOffId, ReverseWriteOffRequest request, WriteOffService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(writeOffId, request, ct)))
            .RequirePermission(ReceivablesPermissions.WriteOffPost)
            .WithSummary("Reverses a write-off: the journal is mirrored and the item reopens for the amount written off.");

        var settlements = receivables.MapGroup("/settlements");
        settlements.MapGet("/", async (Guid companyId, Guid? openItemId, Guid? partnerId, SettlementService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, openItemId, partnerId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead)
            .WithSummary("Settlements of a company, optionally those touching one item or one customer; reversals are mirror rows naming what they reverse.");
        settlements.MapPost("/apply", async (ApplyReceivableRequest request, SettlementService service, CancellationToken ct) => ApiProblems.Ok(await service.ApplyAsync(request, ct)))
            .RequirePermission(ReceivablesPermissions.SettlementPost)
            .WithSummary("Applies a credit note, a receipt on account or a deposit to an invoice open item; a rate difference is realised FX.");
        settlements.MapPost("/{settlementId:guid}/reverse", async (Guid settlementId, ReverseReceivableSettlementRequest request, SettlementService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(settlementId, request, ct)))
            .RequirePermission(ReceivablesPermissions.SettlementPost)
            .WithSummary("Reverses an application: both items reopen at their booked values, the FX journal is mirrored.");

        var dunning = receivables.MapGroup("/dunning");
        dunning.MapGet("/levels", async (Guid companyId, DunningService service, CancellationToken ct) => TypedResults.Ok(await service.ListLevelsAsync(companyId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningRead)
            .WithSummary("The company's reminder ladder: each level's days past due, wording, fee and whether it holds the customer's credit.");
        dunning.MapPost("/levels", async (SaveDunningLevelRequest request, DunningService service, CancellationToken ct) => ApiProblems.Created(await service.SaveLevelAsync(null, request, ct), static l => $"/api/v1/receivables/dunning/levels/{l.Id}"))
            .RequirePermission(ReceivablesPermissions.DunningManage);
        dunning.MapPut("/levels/{levelId:guid}", async (Guid levelId, SaveDunningLevelRequest request, DunningService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveLevelAsync(levelId, request, ct)))
            .RequirePermission(ReceivablesPermissions.DunningManage);
        dunning.MapGet("/preview", async (Guid companyId, DateOnly? runDate, Guid? partnerId, DunningService service, CancellationToken ct) => ApiProblems.Ok(await service.PreviewAsync(companyId, runDate, partnerId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningRead)
            .WithSummary("What a run on the date would issue: one letter per customer whose overdue items climb a level, nothing written.");
        dunning.MapGet("/runs", async (Guid companyId, DunningService service, CancellationToken ct) => TypedResults.Ok(await service.ListRunsAsync(companyId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningRead);
        dunning.MapPost("/runs", async (DunningRunRequest request, DunningService service, CancellationToken ct) => ApiProblems.Created(await service.RunAsync(request, ct), static r => $"/api/v1/receivables/dunning/runs/{r.Id}"))
            .RequirePermission(ReceivablesPermissions.DunningManage)
            .WithSummary("Issues the letters due on the date: items climb to their level, fees are charged to the customers, customers are held where the level says so.");
        dunning.MapGet("/runs/{runId:guid}", async (Guid runId, DunningService service, CancellationToken ct) => ApiProblems.Ok(await service.GetRunAsync(runId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningRead);
        dunning.MapGet("/letters", async (Guid companyId, Guid? partnerId, DunningService service, CancellationToken ct) => TypedResults.Ok(await service.LettersAsync(companyId, partnerId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningRead);
        dunning.MapPost("/letters/{letterId:guid}/sent", async (Guid letterId, DunningService service, CancellationToken ct) => ApiProblems.Ok(await service.MarkSentAsync(letterId, ct)))
            .RequirePermission(ReceivablesPermissions.DunningManage)
            .WithSummary("Records that the letter went out.");

        var nettings = receivables.MapGroup("/nettings");
        nettings.MapGet("/", async (Guid companyId, Guid? partnerId, string? status, NettingService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, partnerId, status, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead);
        nettings.MapPost("/", async (NettingRequest request, NettingService service, CancellationToken ct) => ApiProblems.Created(await service.NetAsync(request, ct), static n => $"/api/v1/receivables/nettings/{n.Id}"))
            .RequirePermission(ReceivablesPermissions.SettlementPost)
            .WithSummary("Sets a customer's open invoice against the same partner's open supplier invoice in their currency: Dr payables / Cr receivables at booked values, the difference realised FX.");
        nettings.MapGet("/{nettingId:guid}", async (Guid nettingId, NettingService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(nettingId, ct)))
            .RequirePermission(ReceivablesPermissions.OpenItemRead);
        nettings.MapPost("/{nettingId:guid}/reverse", async (Guid nettingId, ReverseNettingRequest request, NettingService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(nettingId, request, ct)))
            .RequirePermission(ReceivablesPermissions.SettlementPost)
            .WithSummary("Reverses a netting: both items reopen at their booked values.");

        return api;
    }
}

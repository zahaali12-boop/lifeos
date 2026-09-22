using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Integration.Application;
using Quicker.Kernel.Results;
using Quicker.Web;

namespace Quicker.Integration.Api;

/// <summary>Webhooks on /api/v1/integration: subscriptions (secret shown once), test delivery, delivery log, replay.</summary>
public static class IntegrationEndpoints
{
    public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var webhooks = api.MapGroup("/integration/webhooks").WithTags("Integration").RequireAuthorization();

        webhooks.MapGet("/", async (WebhookService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapGet("/{webhookId:guid}", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            await service.GetAsync(webhookId, ct) is { } w ? Results.Ok(w) : ApiProblems.From(Error.NotFound("webhook", webhookId)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapPost("/", async (SaveWebhookRequest request, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static c => Results.Created($"/api/v1/integration/webhooks/{c.Subscription.Id}", c)))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .WithSummary("Create a subscription; the response carries the signing secret once");
        webhooks.MapPut("/{webhookId:guid}", async (Guid webhookId, SaveWebhookRequest request, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(webhookId, request, ct), static w => Results.Ok(w)))
            .RequirePermission(IntegrationPermissions.WebhookManage);
        webhooks.MapDelete("/{webhookId:guid}", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteAsync(webhookId, ct), static () => Results.NoContent()))
            .RequirePermission(IntegrationPermissions.WebhookManage);
        webhooks.MapPost("/{webhookId:guid}/rotate-secret", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.RotateSecretAsync(webhookId, ct), static c => Results.Ok(c)))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .RequireRecentAuth();
        webhooks.MapPost("/{webhookId:guid}/test", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.TestAsync(webhookId, ct), static d => Results.Accepted($"/api/v1/integration/webhooks/deliveries/{d.Id}", d)))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .WithSummary("Queue a webhook.test delivery so the receiver can check URL, signature and parsing");
        webhooks.MapGet("/{webhookId:guid}/deliveries", async (Guid webhookId, string? status, int? limit, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListDeliveriesAsync(webhookId, status, limit ?? 100, ct), static d => Results.Ok(d)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapGet("/deliveries/{deliveryId:guid}", async (Guid deliveryId, WebhookService service, CancellationToken ct) =>
            await service.GetDeliveryAsync(deliveryId, ct) is { } d ? Results.Ok(d) : ApiProblems.From(Error.NotFound("webhook_delivery", deliveryId)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapPost("/deliveries/{deliveryId:guid}/replay", async (Guid deliveryId, WebhookService service, CancellationToken ct) =>
            ApiProblems.From(await service.ReplayAsync(deliveryId, ct), static d => Results.Accepted($"/api/v1/integration/webhooks/deliveries/{d.Id}", d)))
            .RequirePermission(IntegrationPermissions.WebhookManage);

        return api;
    }
}

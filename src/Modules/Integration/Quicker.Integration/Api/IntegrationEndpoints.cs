using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Integration.Application;
using Quicker.Web;

namespace Quicker.Integration.Api;

/// <summary>Webhooks on /api/v1/integration: subscriptions (secret shown once), test delivery, delivery log, replay.</summary>
public static class IntegrationEndpoints
{
    public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var webhooks = api.MapGroup("/integration/webhooks").WithTags("Integration").RequireAuthorization();

        webhooks.MapGet("/", async (WebhookService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapGet("/{webhookId:guid}", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(webhookId, ct), "webhook", webhookId))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapPost("/", async (SaveWebhookRequest request, WebhookService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static c => $"/api/v1/integration/webhooks/{c.Subscription.Id}"))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .WithSummary("Create a subscription; the response carries the signing secret once");
        webhooks.MapPut("/{webhookId:guid}", async (Guid webhookId, SaveWebhookRequest request, WebhookService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(webhookId, request, ct)))
            .RequirePermission(IntegrationPermissions.WebhookManage);
        webhooks.MapDelete("/{webhookId:guid}", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(webhookId, ct)))
            .RequirePermission(IntegrationPermissions.WebhookManage);
        webhooks.MapPost("/{webhookId:guid}/rotate-secret", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.RotateSecretAsync(webhookId, ct)))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .RequireRecentAuth();
        webhooks.MapPost("/{webhookId:guid}/test", async (Guid webhookId, WebhookService service, CancellationToken ct) =>
            ApiProblems.Accepted(await service.TestAsync(webhookId, ct), static d => $"/api/v1/integration/webhooks/deliveries/{d.Id}"))
            .RequirePermission(IntegrationPermissions.WebhookManage)
            .WithSummary("Queue a webhook.test delivery so the receiver can check URL, signature and parsing");
        webhooks.MapGet("/{webhookId:guid}/deliveries", async (Guid webhookId, string? status, [AsParameters] PageRequest page, WebhookService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListDeliveriesAsync(webhookId, status, page, ct)))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapGet("/deliveries/{deliveryId:guid}", async (Guid deliveryId, WebhookService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetDeliveryAsync(deliveryId, ct), "webhook_delivery", deliveryId))
            .RequirePermission(IntegrationPermissions.WebhookRead);
        webhooks.MapPost("/deliveries/{deliveryId:guid}/replay", async (Guid deliveryId, WebhookService service, CancellationToken ct) =>
            ApiProblems.Accepted(await service.ReplayAsync(deliveryId, ct), static d => $"/api/v1/integration/webhooks/deliveries/{d.Id}"))
            .RequirePermission(IntegrationPermissions.WebhookManage);

        return api;
    }
}

using Quicker.Identity.Contracts;

namespace Quicker.Integration;

public static class IntegrationPermissions
{
    public const string WebhookRead = "integration.webhook.read";
    public const string WebhookManage = "integration.webhook.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(WebhookRead, "integration", "See webhook subscriptions and their delivery log"),
        new(WebhookManage, "integration", "Create, change, test and replay webhooks", IsSensitive: true),
    ];
}

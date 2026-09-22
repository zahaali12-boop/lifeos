using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Integration.Application;
using Quicker.Integration.Persistence;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Integration;

public static class IntegrationModule
{
    public static IServiceCollection AddIntegrationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(IntegrationPermissions.All);
        services.AddHttpClient(WebhookDeliveryJob.HttpClientName, static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 1_048_576;
        });
        services.AddModuleDbContext<IntegrationDbContext>();
        services.AddScoped<WebhookService>();
        services.AddIntegrationEventObserver<WebhookFanout>();
        services.AddJobHandler<WebhookDeliveryJob, WebhookDeliveryPayload>();
        return services;
    }
}

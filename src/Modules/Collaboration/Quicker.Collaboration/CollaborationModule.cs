using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Application;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Collaboration;

public static class CollaborationModule
{
    /// <summary>Requires <c>AddQuickerStorage</c> and <c>AddQuickerEmail</c> on the host.</summary>
    public static IServiceCollection AddCollaborationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(CollaborationPermissions.All);
        services.AddModuleDbContext<CollaborationDbContext>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotifier>(static sp => sp.GetRequiredService<NotificationService>());
        services.AddScoped<RecordAccess>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<IActivityLog>(static sp => sp.GetRequiredService<ActivityService>());
        services.AddScoped<CommentService>();
        services.AddScoped<DocumentLinkService>();
        services.AddScoped<IDocumentLinks>(static sp => sp.GetRequiredService<DocumentLinkService>());
        services.AddScoped<SavedViewService>();
        services.AddSingleton<CustomFieldIndexer>();
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ICustomFieldValidator>(static sp => sp.GetRequiredService<CustomFieldService>());
        services.AddJobHandler<EmailSendJob, EmailSendPayload>();
        return services;
    }
}

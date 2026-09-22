using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Events;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Messaging.Worker;

namespace Quicker.Messaging;

public static class MessagingRegistration
{
    /// <summary>Outbox, job queue, registries and administration; every host (API, worker) calls this.</summary>
    public static IServiceCollection AddQuickerMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionCatalog.Register(PlatformPermissions.All);
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.SectionName));

        services.AddScoped<IOutbox, TransactionalOutbox>();
        services.AddScoped<IJobQueue, JobQueue>();
        services.AddSingleton<EventHandlerRegistry>();
        services.AddSingleton<JobHandlerRegistry>();
        services.AddSingleton<WorkerSignals>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<OutboxAdmin>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<JobAdmin>();
        services.AddSingleton<Scheduler>();

        services.AddJobHandler<OutboxArchiveJob, OutboxArchivePayload>();
        services.AddJobHandler<IdempotencySweepJob, IdempotencySweepPayload>();
        return services;
    }

    /// <summary>The background loops: LISTEN/NOTIFY wake-up, outbox dispatch, job slots with reclaim, scheduler ticks.</summary>
    public static IServiceCollection AddQuickerWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<NotifyListener>();
        services.AddHostedService<OutboxLoop>();
        services.AddHostedService<JobLoop>();
        services.AddHostedService<SchedulerLoop>();
        return services;
    }

    public static IServiceCollection AddIntegrationEventHandler<THandler, TEvent>(this IServiceCollection services)
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<THandler>();
        services.AddSingleton(new EventHandlerRegistration(TEvent.EventType, TEvent.EventVersion, typeof(TEvent), typeof(THandler)));
        return services;
    }

    public static IServiceCollection AddJobHandler<THandler, TPayload>(this IServiceCollection services)
        where THandler : class, IJobHandler<TPayload>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(THandler.JobType, typeof(TPayload), typeof(THandler)));
        return services;
    }
}

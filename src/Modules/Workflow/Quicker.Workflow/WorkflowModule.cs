using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;
using Quicker.Workflow.Application;
using Quicker.Workflow.Contracts;
using Quicker.Workflow.Persistence;

namespace Quicker.Workflow;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(WorkflowPermissions.All);
        services.AddModuleDbContext<WorkflowDbContext>();
        services.AddScoped<DefinitionService>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<IWorkflowEngine>(static sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddJobHandler<EscalationJob, EscalationPayload>();
        return services;
    }
}

using Quicker.Identity.Contracts;

namespace Quicker.Workflow;

public static class WorkflowPermissions
{
    public const string DefinitionRead = "workflow.definition.read";
    public const string DefinitionManage = "workflow.definition.manage";
    /// <summary>Every request of the tenant, not only the ones assigned to the reader.</summary>
    public const string RequestRead = "workflow.request.read";
    /// <summary>Cancelling requests and managing other members' delegations.</summary>
    public const string RequestManage = "workflow.request.manage";

    public static readonly PermissionDefinition[] All =
    [
        new(DefinitionRead, "workflow", "Read approval definitions and the rule catalogue"),
        new(DefinitionManage, "workflow", "Create, edit, activate and retire approval definitions"),
        new(RequestRead, "workflow", "Read every approval request of the tenant"),
        new(RequestManage, "workflow", "Cancel requests and manage delegations for other members"),
    ];
}

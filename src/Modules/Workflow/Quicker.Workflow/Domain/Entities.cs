using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Workflow.Domain;

/// <summary>app.wf_definitions: one entity type and trigger, versioned; the active version at trigger time is stamped on each request.</summary>
public sealed class Definition : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid LineageId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string Trigger { get; set; } = string.Empty;

    public string? BlockKind { get; set; }

    public LocalizedText Name { get; set; } = new();

    public LocalizedText Description { get; set; } = new();

    public int Version { get; set; } = 1;

    public string Status { get; set; } = "draft";

    public string ReapprovalPolicy { get; set; } = "reset";

    public int OverrideValidHours { get; set; } = 168;

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<Rule> Rules { get; set; } = [];
}

/// <summary>app.wf_rules: ordered within a definition; the first whose condition holds decides the steps.</summary>
public sealed class Rule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }

    public int SortOrder { get; set; }

    public LocalizedText Name { get; set; } = new();

    public string Condition { get; set; } = string.Empty;

    public List<Step> Steps { get; set; } = [];
}

/// <summary>app.wf_steps: who approves, how many of them, and what happens when they do not answer in time.</summary>
public sealed class Step : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RuleId { get; set; }

    public int SortOrder { get; set; }

    public LocalizedText Name { get; set; } = new();

    public string ApproverKind { get; set; } = "users";

    /// <summary>JSON: {"membershipIds":[...]} for users, {"roleCode":"finance","scopeToCompany":true} for a role.</summary>
    public string ApproverSpec { get; set; } = "{}";

    public string Mode { get; set; } = "any";

    public int? Quorum { get; set; }

    public int? TimeoutHours { get; set; }

    /// <summary>JSON like the approver spec, or null to remind the same approvers once.</summary>
    public string? Escalation { get; set; }

    public bool AllowDelegate { get; set; } = true;

    public bool RequireComment { get; set; }

    public bool RequireStepUp { get; set; }
}

/// <summary>app.wf_blocks: a refusal a module raised as data, so it can be routed and overridden.</summary>
public sealed class Block : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid? CompanyId { get; set; }

    public string Display { get; set; } = string.Empty;

    public string Why { get; set; } = "{}";

    public string Status { get; set; } = "open";

    public Guid? RequestId { get; set; }

    public Guid? RaisedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.wf_requests: one document's run through the definition that was active when it was submitted.</summary>
public sealed class Request : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid DefinitionId { get; set; }

    public int DefinitionVersion { get; set; }

    public Guid? RuleId { get; set; }

    public LocalizedText RuleName { get; set; } = new();

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid? CompanyId { get; set; }

    public string Display { get; set; } = string.Empty;

    public Guid? RequestedBy { get; set; }

    public string Status { get; set; } = "pending";

    public int? CurrentStepNo { get; set; }

    /// <summary>JSON: the rule that matched, its condition and the values it saw.</summary>
    public string Evaluation { get; set; } = "{}";

    /// <summary>JSON: the subject's values at submission.</summary>
    public string Subject { get; set; } = "{}";

    public Guid? BlockId { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public string? DecisionAction { get; set; }

    public string? DecisionComment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<RequestStep> Steps { get; set; } = [];
}

/// <summary>app.wf_request_steps: the steps of one request with the approvers resolved when the step opened.</summary>
public sealed class RequestStep : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public int StepNo { get; set; }

    public Guid? StepId { get; set; }

    public LocalizedText Name { get; set; } = new();

    public string Mode { get; set; } = "any";

    public int? Quorum { get; set; }

    /// <summary>JSON array of membership ids.</summary>
    public string Approvers { get; set; } = "[]";

    /// <summary>JSON array of membership ids that approved so far.</summary>
    public string ApprovedBy { get; set; } = "[]";

    public string Status { get; set; } = "waiting";

    public int? TimeoutHours { get; set; }

    public string? Escalation { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public bool AllowDelegate { get; set; } = true;

    public bool RequireComment { get; set; }

    public bool RequireStepUp { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>app.wf_actions: append-only history of a request.</summary>
public sealed class RequestAction : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public int? StepNo { get; set; }

    public Guid? Actor { get; set; }

    public Guid? OnBehalfOf { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? Comment { get; set; }

    public string Channel { get; set; } = "web";

    public DateTimeOffset ActedAt { get; set; }
}

/// <summary>app.wf_overrides: an approved block, honoured once by the module that raised it.</summary>
public sealed class Override : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid BlockId { get; set; }

    public Guid RequestId { get; set; }

    public Guid? ApprovedBy { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Consumed { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>app.wf_delegations: one member acts for another between two dates, for every definition or one.</summary>
public sealed class Delegation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid FromMembershipId { get; set; }

    public Guid ToMembershipId { get; set; }

    public Guid? DefinitionId { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly ValidTo { get; set; }

    public string? Reason { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

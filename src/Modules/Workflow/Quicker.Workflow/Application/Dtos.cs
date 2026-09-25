using System.Text.Json;

namespace Quicker.Workflow.Application;

// ------------------------------------------------------------------ definitions

/// <summary>Who approves: named members, or every active holder of a role (scoped to the document's company unless told otherwise).</summary>
public sealed record ApproverSpecRequest(IReadOnlyList<Guid>? MembershipIds = null, string? RoleCode = null, bool ScopeToCompany = true);

public sealed record SaveStepRequest(
    IReadOnlyDictionary<string, string> Name,
    string ApproverKind,
    ApproverSpecRequest Approvers,
    string Mode = "any",
    int? Quorum = null,
    int? TimeoutHours = null,
    ApproverSpecRequest? Escalation = null,
    bool AllowDelegate = true,
    bool RequireComment = false,
    bool RequireStepUp = false);

public sealed record SaveRuleRequest(IReadOnlyDictionary<string, string> Name, string Condition, IReadOnlyList<SaveStepRequest> Steps);

public sealed record SaveDefinitionRequest(
    string EntityType,
    string Trigger,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyList<SaveRuleRequest> Rules,
    string? BlockKind = null,
    IReadOnlyDictionary<string, string>? Description = null,
    string ReapprovalPolicy = "reset",
    int OverrideValidHours = 168);

public sealed record StepSummary(Guid Id, int SortOrder, IReadOnlyDictionary<string, string> Name, string ApproverKind, ApproverSpecRequest Approvers, string Mode, int? Quorum, int? TimeoutHours, ApproverSpecRequest? Escalation, bool AllowDelegate, bool RequireComment, bool RequireStepUp);

public sealed record RuleSummary(Guid Id, int SortOrder, IReadOnlyDictionary<string, string> Name, string Condition, IReadOnlyList<StepSummary> Steps);

public sealed record DefinitionSummary(
    Guid Id,
    Guid LineageId,
    string EntityType,
    string Trigger,
    string? BlockKind,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    int Version,
    string Status,
    string ReapprovalPolicy,
    int OverrideValidHours,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RuleSummary> Rules);

public sealed record CatalogueField(string Name, string Type, IReadOnlyDictionary<string, string> Label);

public sealed record CatalogueEntry(string EntityType, IReadOnlyDictionary<string, string> Label, IReadOnlyList<CatalogueField> Fields, IReadOnlyList<string> BlockKinds);

public sealed record WorkflowCatalogue(IReadOnlyList<CatalogueEntry> Subjects, IReadOnlyList<string> Triggers, IReadOnlyList<string> Functions);

/// <summary>For the on_block trigger the fields come from the block's values, so unknown names are allowed.</summary>
public sealed record ValidateExpressionRequest(string EntityType, string Expression, string? Trigger = null);

public sealed record ExpressionValidation(bool Valid, string? Message, int? Position, IReadOnlyList<string> Fields);

// ------------------------------------------------------------------ requests

public sealed record RequestStepSummary(int StepNo, IReadOnlyDictionary<string, string> Name, string Mode, int? Quorum, IReadOnlyList<Guid> Approvers, IReadOnlyList<string> ApproverNames, IReadOnlyList<Guid> ApprovedBy, string Status, DateTimeOffset? DueAt, DateTimeOffset? EscalatedAt, bool AllowDelegate, bool RequireComment);

public sealed record RequestActionSummary(Guid Id, int? StepNo, Guid? Actor, string? ActorName, Guid? OnBehalfOf, string Action, string? Comment, string Channel, DateTimeOffset ActedAt);

public sealed record RequestSummary(
    Guid Id,
    string EntityType,
    Guid EntityId,
    Guid? CompanyId,
    string Display,
    string Status,
    int? CurrentStepNo,
    Guid? RequestedBy,
    string? RequestedByName,
    IReadOnlyDictionary<string, string> RuleName,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    Guid? DecidedBy,
    string? DecisionAction,
    string? DecisionComment,
    Guid DefinitionId,
    int DefinitionVersion,
    Guid? BlockId,
    bool CanAct);

public sealed record BlockSummary(Guid Id, string Kind, string EntityType, Guid EntityId, Guid? CompanyId, string Display, JsonElement Why, string Status, Guid? RequestId, DateTimeOffset CreatedAt);

public sealed record OverrideSummary(Guid Id, Guid BlockId, Guid RequestId, Guid? ApprovedBy, string Reason, DateTimeOffset ExpiresAt, bool Consumed, DateTimeOffset? ConsumedAt);

public sealed record RequestDetail(RequestSummary Request, JsonElement Evaluation, JsonElement Subject, IReadOnlyList<RequestStepSummary> Steps, IReadOnlyList<RequestActionSummary> Actions, BlockSummary? Block, OverrideSummary? Override);

public sealed record ActionRequest(string? Comment = null);

public sealed record DelegateRequest(Guid ToMembershipId, string? Comment = null);

public sealed record CancelRequest(string Reason);

// ------------------------------------------------------------------ delegations

public sealed record SaveDelegationRequest(Guid ToMembershipId, DateOnly ValidFrom, DateOnly ValidTo, Guid? DefinitionId = null, string? Reason = null, Guid? FromMembershipId = null);

public sealed record DelegationSummary(Guid Id, Guid FromMembershipId, Guid ToMembershipId, Guid? DefinitionId, DateOnly ValidFrom, DateOnly ValidTo, string? Reason, DateTimeOffset CreatedAt);

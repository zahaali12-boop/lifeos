using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Workflow.Contracts;

/// <summary>When a definition runs (ADR-0020).</summary>
public static class WorkflowTriggers
{
    public const string OnSubmit = "on_submit";
    public const string OnPostAttempt = "on_post_attempt";
    public const string OnBlock = "on_block";

    public static readonly IReadOnlyList<string> All = [OnSubmit, OnPostAttempt, OnBlock];
}

/// <summary>Types a rule may reason about; a provider declares each field with one of these.</summary>
public static class WorkflowFieldTypes
{
    public const string Number = "number";
    public const string Text = "text";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string List = "list";

    public static readonly IReadOnlyList<string> All = [Number, Text, Boolean, Date, List];
}

/// <summary>One field of a subject as the rule builder shows it: <c>amount</c> and <c>currency</c> are conventional names the <c>amount_in()</c> function relies on.</summary>
public sealed record WorkflowField(string Name, string Type, LocalizedText Label);

/// <summary>A document (or master record) as the engine evaluates it: typed values keyed by the provider's field names.</summary>
public sealed record WorkflowSubject(string EntityType, Guid EntityId, Guid? CompanyId, string Display, IReadOnlyDictionary<string, object?> Values, Guid? RequestedBy = null);

public static class WorkflowOutcomes
{
    public const string AutoApproved = "auto_approved";
    public const string Pending = "pending";
}

/// <summary>What a submission led to: nothing to approve (recorded when a definition ran), or a pending request.</summary>
public sealed record WorkflowOutcome(string Status, Guid? RequestId, LocalizedText? RuleName, IReadOnlyDictionary<string, object?>? Why);

public static class WorkflowDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

/// <summary>Delivered to the subject's provider when a request ends; <c>Action</c> is the last action (approve, reject, request_changes, cancel, expire).</summary>
public sealed record WorkflowDecision(Guid RequestId, string EntityType, Guid EntityId, string Status, string Action, Guid? DecidedBy, string? Comment, Guid? OverrideId = null);

/// <summary>A structured block a module raises (credit limit, match variance, negative stock, price floor, budget, period).</summary>
public sealed record BlockRequest(string Kind, string EntityType, Guid EntityId, Guid? CompanyId, string Display, IReadOnlyDictionary<string, object?> Why, Guid? RequestedBy = null);

public static class BlockStatuses
{
    public const string Open = "open";
    public const string Pending = "pending";
    public const string Overridden = "overridden";
    public const string Cleared = "cleared";
}

/// <summary>The block as recorded: open (no definition routes it, the module keeps refusing), pending (an approval is on its way), or overridden.</summary>
public sealed record BlockOutcome(Guid BlockId, string Status, Guid? RequestId);

/// <summary>
/// What a module tells the engine about one of its entity types: the fields rules may use, the block kinds it raises,
/// how to load a subject by id (for the inbox and re-evaluation) and what to do when a request is decided.
/// Providers are scoped services; the engine resolves them by entity type at call time.
/// </summary>
public interface IWorkflowSubjectProvider
{
    string EntityType { get; }

    LocalizedText Label { get; }

    IReadOnlyList<WorkflowField> Fields { get; }

    IReadOnlyList<string> BlockKinds { get; }

    Task<WorkflowSubject?> LoadAsync(Guid entityId, CancellationToken cancellationToken = default);

    /// <summary>Runs inside the approver's unit of work, after the request row is updated: the module moves its document on (or back). A failure (a posting the books refuse) fails the decision and rolls everything back, so the approver sees why.</summary>
    Task<Result> OnDecidedAsync(WorkflowDecision decision, CancellationToken cancellationToken = default);
}

/// <summary>The engine (ADR-0020): documents submit to it, modules raise blocks through it and honour the overrides it grants.</summary>
public interface IWorkflowEngine
{
    Task<bool> HasActiveDefinitionAsync(string entityType, string trigger, string? blockKind = null, CancellationToken cancellationToken = default);

    /// <summary>Runs the active definition for the trigger: no definition or no matching rule approves at once; a matching rule opens a request.</summary>
    Task<Result<WorkflowOutcome>> SubmitAsync(WorkflowSubject subject, string trigger = WorkflowTriggers.OnSubmit, CancellationToken cancellationToken = default);

    /// <summary>Records a block and routes it to its definition when one is active; the module refuses its operation until an override exists.</summary>
    Task<Result<BlockOutcome>> RaiseBlockAsync(BlockRequest block, CancellationToken cancellationToken = default);

    /// <summary>Uses an unexpired override for the block once; the id of the override consumed, or null when there is none.</summary>
    Task<Guid?> ConsumeOverrideAsync(string kind, string entityType, Guid entityId, CancellationToken cancellationToken = default);

    /// <summary>Cancels the pending request of an entity (the document was changed or withdrawn); nothing to cancel is not an error.</summary>
    Task<Result> CancelAsync(string entityType, Guid entityId, string reason, CancellationToken cancellationToken = default);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Workflow.Contracts;
using Quicker.Workflow.Domain;
using Quicker.Workflow.Persistence;

namespace Quicker.Workflow.Application;

/// <summary>
/// The engine (ADR-0020): runs the active definition when a document is submitted or a block is raised, opens the
/// request with its steps, resolves approvers when a step opens, takes the approvers' actions, escalates on timeout,
/// and hands the decision back to the module that owns the document. Every action is an audit event and a row in the
/// append-only history; approvers and requesters are notified through Collaboration.
/// </summary>
public sealed class WorkflowEngine(
    WorkflowDbContext db,
    IServiceProvider services,
    IRoleDirectory roles,
    IMemberDirectory members,
    IExchangeRateResolver rates,
    INotifier notifier,
    IAuditSink audit,
    IClock clock,
    ICurrentPrincipal principal) : IWorkflowEngine
{
    private static readonly JsonSerializerOptions Json = DefinitionService.Json;

    public const string DecidedNotification = "workflow.approval_decided";

    private IWorkflowSubjectProvider? Provider(string entityType) => services.GetServices<IWorkflowSubjectProvider>().FirstOrDefault(p => string.Equals(p.EntityType, entityType, StringComparison.Ordinal));

    private Guid? Me => principal.Principal?.MembershipId.Value;

    // ------------------------------------------------------------------ submissions

    public async Task<bool> HasActiveDefinitionAsync(string entityType, string trigger, string? blockKind = null, CancellationToken cancellationToken = default) =>
        await db.Definitions.AnyAsync(d => d.EntityType == entityType && d.Trigger == trigger && d.BlockKind == blockKind && d.Status == "active", cancellationToken);

    public async Task<Result<WorkflowOutcome>> SubmitAsync(WorkflowSubject subject, string trigger = WorkflowTriggers.OnSubmit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var provider = Provider(subject.EntityType);
        if (provider is null)
        {
            return Error.Validation("workflow.entity_type_unknown", "No module registers this entity type for approvals.").WithWhy(("entityType", subject.EntityType));
        }

        if (await db.Requests.AnyAsync(r => r.EntityType == subject.EntityType && r.EntityId == subject.EntityId && r.Status == "pending", cancellationToken))
        {
            return Error.Conflict("workflow.request_pending", "This document already waits for a decision.").WithWhy(("entityType", subject.EntityType), ("entityId", subject.EntityId));
        }

        var definition = await ActiveDefinitionAsync(subject.EntityType, trigger, null, cancellationToken);
        if (definition is null)
        {
            return new WorkflowOutcome(WorkflowOutcomes.AutoApproved, null, null, null);
        }

        return await RunAsync(definition, provider, subject, null, cancellationToken);
    }

    public async Task<Result<BlockOutcome>> RaiseBlockAsync(BlockRequest block, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);
        var provider = Provider(block.EntityType);
        if (provider is null)
        {
            return Error.Validation("workflow.entity_type_unknown", "No module registers this entity type for approvals.").WithWhy(("entityType", block.EntityType));
        }

        var existing = await db.Blocks.SingleOrDefaultAsync(b => b.Kind == block.Kind && b.EntityType == block.EntityType && b.EntityId == block.EntityId && (b.Status == "open" || b.Status == "pending"), cancellationToken);
        if (existing is not null)
        {
            return new BlockOutcome(existing.Id, existing.Status, existing.RequestId);
        }

        var row = new Block
        {
            Id = Guid.CreateVersion7(),
            Kind = block.Kind,
            EntityType = block.EntityType,
            EntityId = block.EntityId,
            CompanyId = block.CompanyId,
            Display = block.Display,
            Why = JsonSerializer.Serialize(block.Why, Json),
            Status = BlockStatuses.Open,
            RaisedBy = block.RequestedBy ?? Me,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        db.Blocks.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_block", row.Id, row.Display, AuditActions.Created, After: new { row.Kind, row.EntityType, row.EntityId, why = block.Why }, CompanyId: row.CompanyId), cancellationToken);

        var definition = await ActiveDefinitionAsync(block.EntityType, WorkflowTriggers.OnBlock, block.Kind, cancellationToken);
        if (definition is null)
        {
            return new BlockOutcome(row.Id, BlockStatuses.Open, null);
        }

        var values = new Dictionary<string, object?>(block.Why, StringComparer.Ordinal) { ["block_kind"] = block.Kind };
        var subject = new WorkflowSubject(block.EntityType, block.EntityId, block.CompanyId, block.Display, values, block.RequestedBy ?? Me);
        var outcome = await RunAsync(definition, provider, subject, row, cancellationToken);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        return new BlockOutcome(row.Id, row.Status, row.RequestId);
    }

    public async Task<Guid?> ConsumeOverrideAsync(string kind, string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var candidate = await (from o in db.Overrides
                               join b in db.Blocks on new { o.TenantId, Id = o.BlockId } equals new { b.TenantId, b.Id }
                               where b.Kind == kind && b.EntityType == entityType && b.EntityId == entityId && !o.Consumed && o.ExpiresAt > now
                               orderby o.CreatedAt
                               select new { Override = o, Block = b }).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        candidate.Override.Consumed = true;
        candidate.Override.ConsumedAt = now;
        candidate.Block.Status = BlockStatuses.Cleared;
        candidate.Block.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_override", candidate.Override.Id, candidate.Block.Display, "consumed", After: new { kind, entityType, entityId }, CompanyId: candidate.Block.CompanyId), cancellationToken);
        return candidate.Override.Id;
    }

    public async Task<Result> CancelAsync(string entityType, Guid entityId, string reason, CancellationToken cancellationToken = default)
    {
        var pending = await db.Requests.Include(static r => r.Steps).Where(r => r.EntityType == entityType && r.EntityId == entityId && r.Status == "pending").ToListAsync(cancellationToken);
        foreach (var request in pending)
        {
            var closed = await CloseAsync(request, "cancelled", "cancel", reason, notifyProvider: false, cancellationToken);
            if (closed.IsFailure)
            {
                return closed;
            }
        }

        return Result.Success();
    }

    private async Task<Definition?> ActiveDefinitionAsync(string entityType, string trigger, string? blockKind, CancellationToken cancellationToken) =>
        await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).AsNoTracking()
            .SingleOrDefaultAsync(d => d.EntityType == entityType && d.Trigger == trigger && d.BlockKind == blockKind && d.Status == "active", cancellationToken);

    /// <summary>Evaluates the rules in order; the first that holds opens a request, none records an automatic approval.</summary>
    private async Task<Result<WorkflowOutcome>> RunAsync(Definition definition, IWorkflowSubjectProvider provider, WorkflowSubject subject, Block? block, CancellationToken cancellationToken)
    {
        var fieldTypes = block is null ? DefinitionService.FieldTypes(provider) : BlockFieldTypes(subject.Values);
        var today = clock.TodayIn("UTC");
        Rule? matched = null;
        var evaluations = new List<object>();
        foreach (var rule in definition.Rules.OrderBy(static r => r.SortOrder))
        {
            var compiled = Expressions.Compile(rule.Condition, fieldTypes);
            if (compiled.IsFailure)
            {
                return compiled.Error!.WithWhy(("definition", definition.Id), ("rule", rule.SortOrder));
            }

            var ratesTo = await RatesAsync(subject, compiled.Value.Currencies, today, cancellationToken);
            if (ratesTo.IsFailure)
            {
                return ratesTo.Error!;
            }

            var holds = Expressions.Evaluate(compiled.Value, new Expressions.EvaluationContext(subject.Values, ratesTo.Value, today));
            if (holds.IsFailure)
            {
                return holds.Error!.WithWhy(("definition", definition.Id), ("rule", rule.SortOrder));
            }

            evaluations.Add(new { rule = rule.SortOrder, name = rule.Name.Values, condition = rule.Condition, matched = holds.Value });
            if (holds.Value)
            {
                matched = rule;
                break;
            }
        }

        var request = new Request
        {
            Id = Guid.CreateVersion7(),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            RuleId = matched?.Id,
            RuleName = matched?.Name ?? new LocalizedText(),
            EntityType = subject.EntityType,
            EntityId = subject.EntityId,
            CompanyId = subject.CompanyId,
            Display = subject.Display,
            RequestedBy = subject.RequestedBy ?? Me,
            Status = matched is null ? "auto_approved" : "pending",
            Evaluation = JsonSerializer.Serialize(new { rules = evaluations, values = subject.Values, matched = matched?.SortOrder }, Json),
            Subject = JsonSerializer.Serialize(subject.Values, Json),
            BlockId = block?.Id,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        db.Requests.Add(request);
        db.Actions.Add(NewAction(request, null, "submit", null, request.RequestedBy));

        if (matched is null)
        {
            request.DecidedAt = clock.UtcNow;
            request.DecisionAction = "auto_approve";
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, AuditActions.Approved, After: new { status = request.Status, rule = (string?)null }, CompanyId: request.CompanyId), cancellationToken);
            if (block is not null)
            {
                await GrantOverrideAsync(request, block, definition.OverrideValidHours, null, "No rule of the active definition holds: overridden automatically.", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new WorkflowOutcome(WorkflowOutcomes.AutoApproved, request.Id, null, subject.Values);
        }

        var stepNo = 0;
        foreach (var step in matched.Steps.OrderBy(static s => s.SortOrder))
        {
            stepNo++;
            request.Steps.Add(new RequestStep
            {
                Id = Guid.CreateVersion7(),
                RequestId = request.Id,
                StepNo = stepNo,
                StepId = step.Id,
                Name = step.Name,
                Mode = step.Mode,
                Quorum = step.Quorum,
                Approvers = "[]",
                ApprovedBy = "[]",
                Status = "waiting",
                TimeoutHours = step.TimeoutHours,
                Escalation = step.Escalation,
                AllowDelegate = step.AllowDelegate,
                RequireComment = step.RequireComment,
                RequireStepUp = step.RequireStepUp,
            });
        }

        var opened = await OpenStepAsync(request, matched, request.Steps[0], cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

        if (block is not null)
        {
            block.Status = BlockStatuses.Pending;
            block.RequestId = request.Id;
            block.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, AuditActions.Created, After: new { rule = matched.Name.Values, condition = matched.Condition, steps = request.Steps.Count }, CompanyId: request.CompanyId), cancellationToken);
        await NotifyApproversAsync(request, request.Steps[0], cancellationToken);
        return new WorkflowOutcome(WorkflowOutcomes.Pending, request.Id, matched.Name, subject.Values);
    }

    /// <summary>A block's rule sees the block's own values; their types come from the values themselves.</summary>
    private static Dictionary<string, string> BlockFieldTypes(IReadOnlyDictionary<string, object?> values) =>
        values.ToDictionary(static v => v.Key, static v => v.Value switch
        {
            decimal or int or long => WorkflowFieldTypes.Number,
            bool => WorkflowFieldTypes.Boolean,
            DateOnly or DateTime or DateTimeOffset => WorkflowFieldTypes.Date,
            System.Collections.IEnumerable and not string => WorkflowFieldTypes.List,
            _ => WorkflowFieldTypes.Text,
        }, StringComparer.Ordinal);

    private async Task<Result<IReadOnlyDictionary<string, decimal>>> RatesAsync(WorkflowSubject subject, IReadOnlyList<string> currencies, DateOnly today, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (currencies.Count == 0)
        {
            return result;
        }

        var from = (subject.Values.TryGetValue("currency", out var c) ? c as string : null)?.ToUpperInvariant();
        if (from is null || subject.CompanyId is null)
        {
            return result;
        }

        foreach (var currency in currencies.Where(cur => cur != from))
        {
            var rate = await rates.ResolveAsync(new CompanyId(subject.CompanyId.Value), from, currency, today, RateTypes.Spot, cancellationToken);
            if (rate.IsFailure)
            {
                return rate.Error!.WithWhy(("from", from), ("to", currency));
            }

            result[currency] = rate.Value.Rate.Rate;
        }

        return result;
    }

    // ------------------------------------------------------------------ steps and approvers

    private async Task<Result> OpenStepAsync(Request request, Rule? rule, RequestStep step, CancellationToken cancellationToken)
    {
        var definitionStep = rule?.Steps.SingleOrDefault(s => s.Id == step.StepId);
        var spec = definitionStep is null ? null : DefinitionService.ReadSpec(definitionStep.ApproverSpec);
        var approvers = await ResolveApproversAsync(definitionStep?.ApproverKind ?? "users", spec, request.CompanyId, cancellationToken);
        approvers = approvers.Where(a => a != request.RequestedBy).Distinct().ToList();
        if (approvers.Count == 0)
        {
            return Error.Validation("workflow.no_approvers", "The step has no approver other than the requester; assign the role to someone or name other members.")
                .WithWhy(("step", step.StepNo), ("name", step.Name.Values), ("approverKind", definitionStep?.ApproverKind), ("roleCode", spec?.RoleCode));
        }

        step.Approvers = JsonSerializer.Serialize(approvers, Json);
        step.Status = "pending";
        step.DueAt = step.TimeoutHours is { } hours ? clock.UtcNow.AddHours(hours) : null;
        request.CurrentStepNo = step.StepNo;
        request.DueAt = step.DueAt;
        request.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    private async Task<List<Guid>> ResolveApproversAsync(string kind, ApproverSpecRequest? spec, Guid? companyId, CancellationToken cancellationToken)
    {
        if (spec is null)
        {
            return [];
        }

        if (kind == "users" || spec.RoleCode is null)
        {
            var result = new List<Guid>();
            foreach (var id in spec.MembershipIds ?? [])
            {
                var member = await members.FindAsync(new MembershipId(id), cancellationToken);
                if (member is { IsActive: true })
                {
                    result.Add(id);
                }
            }

            return result;
        }

        var role = await roles.FindRoleByCodeAsync(spec.RoleCode, cancellationToken);
        if (role is null)
        {
            return [];
        }

        return (await roles.MembersInRoleAsync(role.Id, spec.ScopeToCompany ? companyId : null, cancellationToken)).Select(static m => m.Value).ToList();
    }

    private static List<Guid> Ids(string json) => JsonSerializer.Deserialize<List<Guid>>(json, Json) ?? [];

    /// <summary>The members who may act on the step for the actor: the actor itself when assigned, or every assigned member who delegated to the actor today.</summary>
    private async Task<(bool Assigned, Guid? OnBehalfOf)> ActingRightsAsync(Request request, RequestStep step, Guid actor, CancellationToken cancellationToken)
    {
        var approvers = Ids(step.Approvers);
        if (approvers.Contains(actor))
        {
            return (true, null);
        }

        if (!step.AllowDelegate)
        {
            return (false, null);
        }

        var today = clock.TodayIn("UTC");
        var delegations = await db.Delegations.Where(d => d.ToMembershipId == actor && d.ValidFrom <= today && d.ValidTo >= today && (d.DefinitionId == null || d.DefinitionId == request.DefinitionId)).ToListAsync(cancellationToken);
        var principalOf = delegations.Select(static d => d.FromMembershipId).FirstOrDefault(approvers.Contains);
        return principalOf == Guid.Empty ? (false, null) : (true, principalOf);
    }

    // ------------------------------------------------------------------ actions

    public async Task<Result<Request>> ActAsync(Guid requestId, string action, string? comment, Guid? delegateTo, CancellationToken cancellationToken)
    {
        var actor = Me;
        if (actor is null)
        {
            return Error.Forbidden("workflow.actor_required", "Only a signed-in member acts on a request.");
        }

        var request = await db.Requests.Include(static r => r.Steps).SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return Error.NotFound("workflow_request", requestId);
        }

        if (action == "comment")
        {
            if (string.IsNullOrWhiteSpace(comment))
            {
                return Error.Validation("workflow.comment_required", "A comment has text.");
            }

            db.Actions.Add(NewAction(request, request.CurrentStepNo, "comment", comment.Trim(), actor));
            await db.SaveChangesAsync(cancellationToken);
            return request;
        }

        if (action == "cancel")
        {
            if (request.Status != "pending")
            {
                return Error.Conflict("workflow.not_pending", "Only a pending request is cancelled.").WithWhy(("status", request.Status));
            }

            if (request.RequestedBy != actor && principal.Principal?.Has(WorkflowPermissions.RequestManage) != true)
            {
                return Error.Forbidden("workflow.cancel_forbidden", "Only the requester or a workflow manager cancels a request.");
            }

            var cancelled = await CloseAsync(request, "cancelled", "cancel", comment, notifyProvider: true, cancellationToken);
            return cancelled.IsFailure ? cancelled.Error! : request;
        }

        if (request.Status != "pending" || request.CurrentStepNo is null)
        {
            return Error.Conflict("workflow.not_pending", "This request is already decided.").WithWhy(("status", request.Status));
        }

        var step = request.Steps.Single(s => s.StepNo == request.CurrentStepNo);
        if (request.RequestedBy == actor)
        {
            return Error.Forbidden("workflow.self_approval", "The person who submitted a document cannot decide on it.");
        }

        var (assigned, onBehalfOf) = await ActingRightsAsync(request, step, actor.Value, cancellationToken);
        if (!assigned)
        {
            return Error.Forbidden("workflow.not_an_approver", "This request is not assigned to you for the current step.").WithWhy(("step", step.StepNo));
        }

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        switch (action)
        {
            case "approve":
                {
                    if (step.RequireComment && text is null)
                    {
                        return Error.Validation("workflow.comment_required", "This step requires a comment with the decision.").WithWhy(("step", step.StepNo));
                    }

                    var approvedBy = Ids(step.ApprovedBy);
                    var credited = onBehalfOf ?? actor.Value;
                    if (!approvedBy.Contains(credited))
                    {
                        approvedBy.Add(credited);
                    }

                    step.ApprovedBy = JsonSerializer.Serialize(approvedBy, Json);
                    db.Actions.Add(NewAction(request, step.StepNo, "approve", text, actor, onBehalfOf));
                    await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, AuditActions.Approved, After: new { step = step.StepNo, approvals = approvedBy.Count }, Reason: text, CompanyId: request.CompanyId), cancellationToken);
                    var needed = step.Mode switch { "all" => Ids(step.Approvers).Count, "quorum" => step.Quorum ?? 1, _ => 1 };
                    if (approvedBy.Count < needed)
                    {
                        request.UpdatedAt = clock.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                        return request;
                    }

                    step.Status = "approved";
                    step.DecidedAt = clock.UtcNow;
                    var next = request.Steps.Where(s => s.StepNo > step.StepNo).OrderBy(static s => s.StepNo).FirstOrDefault();
                    if (next is null)
                    {
                        var closed = await CloseAsync(request, "approved", "approve", text, notifyProvider: true, cancellationToken, actor.Value);
                        return closed.IsFailure ? closed.Error! : request;
                    }

                    var definition = await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).AsNoTracking().SingleAsync(d => d.Id == request.DefinitionId, cancellationToken);
                    var rule = definition.Rules.SingleOrDefault(r => r.Id == request.RuleId);
                    var opened = await OpenStepAsync(request, rule, next, cancellationToken);
                    if (opened.IsFailure)
                    {
                        return opened.Error!;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    await NotifyApproversAsync(request, next, cancellationToken);
                    return request;
                }

            case "reject" or "request_changes":
                {
                    if (text is null)
                    {
                        return Error.Validation("workflow.comment_required", "A rejection or a request for changes says why.").WithWhy(("step", step.StepNo));
                    }

                    step.Status = "rejected";
                    step.DecidedAt = clock.UtcNow;
                    db.Actions.Add(NewAction(request, step.StepNo, action, text, actor, onBehalfOf));
                    var rejected = await CloseAsync(request, "rejected", action, text, notifyProvider: true, cancellationToken, actor.Value);
                    return rejected.IsFailure ? rejected.Error! : request;
                }

            case "delegate":
                {
                    if (!step.AllowDelegate)
                    {
                        return Error.Conflict("workflow.delegation_not_allowed", "This step does not allow delegation.").WithWhy(("step", step.StepNo));
                    }

                    if (delegateTo is null || delegateTo == actor)
                    {
                        return Error.Validation("workflow.delegate_required", "Name the member to delegate to.");
                    }

                    var member = await members.FindAsync(new MembershipId(delegateTo.Value), cancellationToken);
                    if (member is not { IsActive: true })
                    {
                        return Error.Validation("workflow.approver_unknown", "The delegate is not an active member.").WithWhy(("membershipId", delegateTo));
                    }

                    if (delegateTo == request.RequestedBy)
                    {
                        return Error.Validation("workflow.self_approval", "A request cannot be delegated to its requester.");
                    }

                    var approvers = Ids(step.Approvers);
                    if (!approvers.Contains(delegateTo.Value))
                    {
                        approvers.Add(delegateTo.Value);
                        step.Approvers = JsonSerializer.Serialize(approvers, Json);
                    }

                    db.Actions.Add(NewAction(request, step.StepNo, "delegate", text, actor, onBehalfOf, delegateTo));
                    request.UpdatedAt = clock.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, AuditActions.StateChanged, After: new { step = step.StepNo, delegatedTo = delegateTo }, Reason: text, CompanyId: request.CompanyId), cancellationToken);
                    await notifier.NotifyAsync(new NotificationRequest(NotificationKinds.ApprovalRequested, LocalizedText.Bilingual("Approval delegated to you", "أُحيل إليك اعتماد"), LocalizedText.Bilingual(request.Display, request.Display), [new MembershipId(delegateTo.Value)], "/approvals?open=" + request.Id, "workflow_request", request.Id), cancellationToken);
                    return request;
                }

            default:
                return Error.Validation("workflow.action_invalid", "The action is approve, reject, request_changes, delegate, comment or cancel.").WithWhy(("action", action));
        }
    }

    private async Task<Result> CloseAsync(Request request, string status, string action, string? comment, bool notifyProvider, CancellationToken cancellationToken, Guid? decidedBy = null)
    {
        request.Status = status;
        request.DecidedAt = clock.UtcNow;
        request.DecidedBy = decidedBy ?? Me;
        request.DecisionAction = action;
        request.DecisionComment = comment;
        request.CurrentStepNo = null;
        request.DueAt = null;
        request.UpdatedAt = clock.UtcNow;
        foreach (var waiting in request.Steps.Where(static s => s.Status is "waiting" or "pending"))
        {
            waiting.Status = status == "approved" ? waiting.Status : "skipped";
        }

        if (action is "cancel" or "expire")
        {
            db.Actions.Add(NewAction(request, null, action, comment, Me));
        }

        Guid? overrideId = null;
        Block? block = null;
        if (request.BlockId is { } blockId)
        {
            block = await db.Blocks.SingleAsync(b => b.Id == blockId, cancellationToken);
            if (status == "approved")
            {
                var definition = await db.Definitions.AsNoTracking().SingleAsync(d => d.Id == request.DefinitionId, cancellationToken);
                overrideId = await GrantOverrideAsync(request, block, definition.OverrideValidHours, request.DecidedBy, comment ?? "Approved.", cancellationToken);
            }
            else
            {
                block.Status = BlockStatuses.Open;
                block.UpdatedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, status == "approved" ? AuditActions.Approved : status == "rejected" ? AuditActions.Rejected : AuditActions.StateChanged, After: new { status, action, overrideId }, Reason: comment, CompanyId: request.CompanyId), cancellationToken);

        if (request.RequestedBy is { } requester && requester != request.DecidedBy)
        {
            var title = status switch
            {
                "approved" => LocalizedText.Bilingual("Approved: " + request.Display, "اعتُمد: " + request.Display),
                "rejected" => LocalizedText.Bilingual("Not approved: " + request.Display, "لم يُعتمد: " + request.Display),
                _ => LocalizedText.Bilingual("Request closed: " + request.Display, "أُغلق الطلب: " + request.Display),
            };
            await notifier.NotifyAsync(new NotificationRequest(DecidedNotification, title, LocalizedText.Bilingual(comment ?? string.Empty, comment ?? string.Empty), [new MembershipId(requester)], "/approvals?open=" + request.Id, "workflow_request", request.Id), cancellationToken);
        }

        if (notifyProvider)
        {
            var provider = Provider(request.EntityType);
            if (provider is not null)
            {
                return await provider.OnDecidedAsync(new WorkflowDecision(request.Id, request.EntityType, request.EntityId, status, action, request.DecidedBy, comment, overrideId), cancellationToken);
            }
        }

        return Result.Success();
    }

    private async Task<Guid> GrantOverrideAsync(Request request, Block block, int validHours, Guid? approvedBy, string reason, CancellationToken cancellationToken)
    {
        var row = new Override { Id = Guid.CreateVersion7(), BlockId = block.Id, RequestId = request.Id, ApprovedBy = approvedBy, Reason = reason, ExpiresAt = clock.UtcNow.AddHours(validHours), CreatedAt = clock.UtcNow };
        db.Overrides.Add(row);
        block.Status = BlockStatuses.Overridden;
        block.RequestId = request.Id;
        block.UpdatedAt = clock.UtcNow;
        await audit.RecordAsync(new AuditEntry("workflow_override", row.Id, block.Display, AuditActions.Override, After: new { block.Kind, block.EntityType, block.EntityId, approvedBy, expiresAt = row.ExpiresAt }, Reason: reason, CompanyId: block.CompanyId), cancellationToken);
        return row.Id;
    }

    private RequestAction NewAction(Request request, int? stepNo, string action, string? comment, Guid? actor, Guid? onBehalfOf = null, Guid? delegateTo = null) => new()
    {
        Id = Guid.CreateVersion7(),
        RequestId = request.Id,
        StepNo = stepNo,
        Actor = actor,
        OnBehalfOf = onBehalfOf ?? delegateTo,
        Action = action,
        Comment = comment,
        Channel = principal.Principal?.ActorType == "user" ? "web" : "system",
        ActedAt = clock.UtcNow,
    };

    private async Task NotifyApproversAsync(Request request, RequestStep step, CancellationToken cancellationToken)
    {
        var approvers = Ids(step.Approvers);
        if (approvers.Count == 0)
        {
            return;
        }

        await notifier.NotifyAsync(new NotificationRequest(
            NotificationKinds.ApprovalRequested,
            LocalizedText.Bilingual("Approval needed: " + request.Display, "بانتظار اعتمادك: " + request.Display),
            LocalizedText.Bilingual(step.Name.Resolve("en"), step.Name.Resolve("ar")),
            approvers.Select(static a => new MembershipId(a)).ToList(),
            "/approvals?open=" + request.Id,
            "workflow_request",
            request.Id), cancellationToken);
    }

    // ------------------------------------------------------------------ escalation

    /// <summary>Steps past their due time: reassigned to the escalation target (or their approvers reminded once), recorded and notified.</summary>
    public async Task<int> EscalateDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await (from s in db.RequestSteps
                         join r in db.Requests on new { s.TenantId, Id = s.RequestId } equals new { r.TenantId, r.Id }
                         where s.Status == "pending" && s.DueAt != null && s.DueAt < now && s.EscalatedAt == null && r.Status == "pending"
                         select new { Step = s, Request = r }).ToListAsync(cancellationToken);
        foreach (var item in due)
        {
            var step = item.Step;
            var request = item.Request;
            var spec = step.Escalation is null ? null : DefinitionService.ReadSpec(step.Escalation);
            var targets = spec is null ? [] : await ResolveApproversAsync(spec.RoleCode is null ? "users" : "role", spec, request.CompanyId, cancellationToken);
            targets = targets.Where(t => t != request.RequestedBy).ToList();
            var approvers = Ids(step.Approvers);
            foreach (var target in targets.Where(t => !approvers.Contains(t)))
            {
                approvers.Add(target);
            }

            step.Approvers = JsonSerializer.Serialize(approvers, Json);
            step.EscalatedAt = now;
            step.DueAt = step.TimeoutHours is { } hours ? now.AddHours(hours) : null;
            request.DueAt = step.DueAt;
            request.UpdatedAt = now;
            db.Actions.Add(NewAction(request, step.StepNo, "escalate", targets.Count == 0 ? "Reminder sent: no escalation target." : $"Escalated to {targets.Count} member(s).", null));
            await audit.RecordAsync(new AuditEntry("workflow_request", request.Id, request.Display, AuditActions.StateChanged, After: new { step = step.StepNo, escalatedTo = targets, reminded = targets.Count == 0 }, CompanyId: request.CompanyId), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var recipients = (targets.Count == 0 ? approvers : targets).Select(static a => new MembershipId(a)).ToList();
            await notifier.NotifyAsync(new NotificationRequest(NotificationKinds.ApprovalRequested, LocalizedText.Bilingual("Overdue approval: " + request.Display, "اعتماد متأخر: " + request.Display), LocalizedText.Bilingual(step.Name.Resolve("en"), step.Name.Resolve("ar")), recipients, "/approvals?open=" + request.Id, "workflow_request", request.Id), cancellationToken);
        }

        return due.Count;
    }

    // ------------------------------------------------------------------ inbox and inquiry

    /// <summary>Requests the current member may act on: assigned on the current step, or delegated to them today.</summary>
    public async Task<IReadOnlyList<RequestSummary>> ListMineAsync(CancellationToken cancellationToken)
    {
        var me = Me;
        if (me is null)
        {
            return [];
        }

        var today = clock.TodayIn("UTC");
        var delegatedFrom = await db.Delegations.Where(d => d.ToMembershipId == me && d.ValidFrom <= today && d.ValidTo >= today).Select(static d => new { d.FromMembershipId, d.DefinitionId }).ToListAsync(cancellationToken);
        var pending = await db.Requests.Include(static r => r.Steps).AsNoTracking().Where(r => r.Status == "pending").OrderBy(static r => r.CreatedAt).ToListAsync(cancellationToken);
        var mine = pending.Where(r =>
        {
            var step = r.Steps.SingleOrDefault(s => s.StepNo == r.CurrentStepNo);
            if (step is null || r.RequestedBy == me)
            {
                return false;
            }

            var approvers = Ids(step.Approvers);
            return approvers.Contains(me.Value) || (step.AllowDelegate && delegatedFrom.Any(d => approvers.Contains(d.FromMembershipId) && (d.DefinitionId == null || d.DefinitionId == r.DefinitionId)));
        }).ToList();
        return await MapManyAsync(mine, canAct: true, cancellationToken);
    }

    public async Task<IReadOnlyList<RequestSummary>> ListAsync(string? status, string? entityType, Guid? entityId, CancellationToken cancellationToken)
    {
        var query = db.Requests.Include(static r => r.Steps).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(r => r.EntityType == entityType);
        }

        if (entityId is { } id)
        {
            query = query.Where(r => r.EntityId == id);
        }

        var rows = await query.OrderByDescending(static r => r.CreatedAt).Take(500).ToListAsync(cancellationToken);
        return await MapManyAsync(rows, canAct: false, cancellationToken);
    }

    public async Task<Result<RequestDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await db.Requests.Include(static r => r.Steps).AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return Error.NotFound("workflow_request", id);
        }

        var me = Me;
        var actions = await db.Actions.AsNoTracking().Where(a => a.RequestId == id).OrderBy(static a => a.ActedAt).ToListAsync(cancellationToken);
        var involved = request.RequestedBy == me || request.Steps.Any(s => Ids(s.Approvers).Contains(me ?? Guid.Empty)) || actions.Any(a => a.Actor == me);
        if (!involved && principal.Principal?.Has(WorkflowPermissions.RequestRead) != true)
        {
            var mine = await ListMineAsync(cancellationToken);
            if (mine.All(m => m.Id != id))
            {
                return Error.Forbidden("workflow.request_forbidden", "This request is not yours to see.");
            }
        }

        var names = await NamesAsync(request.Steps.SelectMany(s => Ids(s.Approvers)).Concat(actions.Select(static a => a.Actor ?? Guid.Empty)).Append(request.RequestedBy ?? Guid.Empty).Where(static g => g != Guid.Empty), cancellationToken);
        var canAct = me is not null && request.Status == "pending" && request.RequestedBy != me && (await ListMineAsync(cancellationToken)).Any(m => m.Id == id);
        var summary = Map(request, names, canAct);
        var steps = request.Steps.OrderBy(static s => s.StepNo).Select(s =>
        {
            var approvers = Ids(s.Approvers);
            return new RequestStepSummary(s.StepNo, s.Name.Values, s.Mode, s.Quorum, approvers, approvers.Select(a => names.GetValueOrDefault(a, string.Empty)).ToList(), Ids(s.ApprovedBy), s.Status, s.DueAt, s.EscalatedAt, s.AllowDelegate, s.RequireComment);
        }).ToList();
        var history = actions.Select(a => new RequestActionSummary(a.Id, a.StepNo, a.Actor, a.Actor is { } actor ? names.GetValueOrDefault(actor) : null, a.OnBehalfOf, a.Action, a.Comment, a.Channel, a.ActedAt)).ToList();
        BlockSummary? block = null;
        OverrideSummary? granted = null;
        if (request.BlockId is { } blockId)
        {
            var row = await db.Blocks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == blockId, cancellationToken);
            if (row is not null)
            {
                block = new BlockSummary(row.Id, row.Kind, row.EntityType, row.EntityId, row.CompanyId, row.Display, JsonDocument.Parse(row.Why).RootElement.Clone(), row.Status, row.RequestId, row.CreatedAt);
            }

            var o = await db.Overrides.AsNoTracking().Where(x => x.RequestId == id).OrderByDescending(static x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (o is not null)
            {
                granted = new OverrideSummary(o.Id, o.BlockId, o.RequestId, o.ApprovedBy, o.Reason, o.ExpiresAt, o.Consumed, o.ConsumedAt);
            }
        }

        return new RequestDetail(summary, JsonDocument.Parse(request.Evaluation).RootElement.Clone(), JsonDocument.Parse(request.Subject).RootElement.Clone(), steps, history, block, granted);
    }

    public async Task<IReadOnlyList<BlockSummary>> ListBlocksAsync(string? entityType, Guid? entityId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Blocks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(b => b.EntityType == entityType);
        }

        if (entityId is { } id)
        {
            query = query.Where(b => b.EntityId == id);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(b => b.Status == status);
        }

        var rows = await query.OrderByDescending(static b => b.CreatedAt).Take(500).ToListAsync(cancellationToken);
        return rows.Select(static b => new BlockSummary(b.Id, b.Kind, b.EntityType, b.EntityId, b.CompanyId, b.Display, JsonDocument.Parse(b.Why).RootElement.Clone(), b.Status, b.RequestId, b.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<OverrideSummary>> ListOverridesAsync(Guid? blockId, CancellationToken cancellationToken)
    {
        var query = db.Overrides.AsNoTracking().AsQueryable();
        if (blockId is { } id)
        {
            query = query.Where(o => o.BlockId == id);
        }

        var rows = await query.OrderByDescending(static o => o.CreatedAt).Take(500).ToListAsync(cancellationToken);
        return rows.Select(static o => new OverrideSummary(o.Id, o.BlockId, o.RequestId, o.ApprovedBy, o.Reason, o.ExpiresAt, o.Consumed, o.ConsumedAt)).ToList();
    }

    // ------------------------------------------------------------------ delegations

    public async Task<IReadOnlyList<DelegationSummary>> ListDelegationsAsync(bool all, CancellationToken cancellationToken)
    {
        var me = Me;
        var query = db.Delegations.AsNoTracking().AsQueryable();
        if (!all)
        {
            query = query.Where(d => d.FromMembershipId == me || d.ToMembershipId == me);
        }

        var rows = await query.OrderByDescending(static d => d.ValidFrom).ToListAsync(cancellationToken);
        return rows.Select(static d => new DelegationSummary(d.Id, d.FromMembershipId, d.ToMembershipId, d.DefinitionId, d.ValidFrom, d.ValidTo, d.Reason, d.CreatedAt)).ToList();
    }

    public async Task<Result<DelegationSummary>> CreateDelegationAsync(SaveDelegationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var me = Me;
        var from = request.FromMembershipId ?? me;
        if (from is null)
        {
            return Error.Forbidden("workflow.actor_required", "Only a signed-in member delegates.");
        }

        if (from != me && principal.Principal?.Has(WorkflowPermissions.RequestManage) != true)
        {
            return Error.Forbidden("workflow.delegation_forbidden", "Delegating for another member needs the workflow manager permission.");
        }

        if (request.ToMembershipId == from)
        {
            return Error.Validation("workflow.delegate_required", "A member cannot delegate to themselves.");
        }

        if (request.ValidTo < request.ValidFrom)
        {
            return Error.Validation("workflow.delegation_period_invalid", "The delegation ends on or after the day it starts.");
        }

        var to = await members.FindAsync(new MembershipId(request.ToMembershipId), cancellationToken);
        if (to is not { IsActive: true })
        {
            return Error.Validation("workflow.approver_unknown", "The delegate is not an active member.").WithWhy(("membershipId", request.ToMembershipId));
        }

        if (request.DefinitionId is { } definitionId && !await db.Definitions.AnyAsync(d => d.Id == definitionId, cancellationToken))
        {
            return Error.NotFound("workflow_definition", definitionId);
        }

        var row = new Delegation { Id = Guid.CreateVersion7(), FromMembershipId = from.Value, ToMembershipId = request.ToMembershipId, DefinitionId = request.DefinitionId, ValidFrom = request.ValidFrom, ValidTo = request.ValidTo, Reason = request.Reason, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        db.Delegations.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_delegation", row.Id, $"{row.FromMembershipId} → {row.ToMembershipId}", AuditActions.Created, After: new { row.FromMembershipId, row.ToMembershipId, row.DefinitionId, row.ValidFrom, row.ValidTo }, Reason: row.Reason), cancellationToken);
        return new DelegationSummary(row.Id, row.FromMembershipId, row.ToMembershipId, row.DefinitionId, row.ValidFrom, row.ValidTo, row.Reason, row.CreatedAt);
    }

    public async Task<Result> DeleteDelegationAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Delegations.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null)
        {
            return Error.NotFound("workflow_delegation", id);
        }

        if (row.FromMembershipId != Me && principal.Principal?.Has(WorkflowPermissions.RequestManage) != true)
        {
            return Error.Forbidden("workflow.delegation_forbidden", "Only the delegating member or a workflow manager removes a delegation.");
        }

        db.Delegations.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_delegation", row.Id, $"{row.FromMembershipId} → {row.ToMembershipId}", AuditActions.Deleted), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ mapping

    private async Task<IReadOnlyList<RequestSummary>> MapManyAsync(IReadOnlyList<Request> rows, bool canAct, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(rows.Select(static r => r.RequestedBy ?? Guid.Empty).Where(static g => g != Guid.Empty), cancellationToken);
        return rows.Select(r => Map(r, names, canAct)).ToList();
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> membershipIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var id in membershipIds.Distinct())
        {
            var member = await members.FindAsync(new MembershipId(id), cancellationToken);
            if (member is not null)
            {
                names[id] = member.DisplayName;
            }
        }

        return names;
    }

    private static RequestSummary Map(Request r, IReadOnlyDictionary<Guid, string> names, bool canAct) => new(
        r.Id, r.EntityType, r.EntityId, r.CompanyId, r.Display, r.Status, r.CurrentStepNo, r.RequestedBy, r.RequestedBy is { } by ? names.GetValueOrDefault(by) : null, r.RuleName.Values,
        r.DueAt, r.CreatedAt, r.DecidedAt, r.DecidedBy, r.DecisionAction, r.DecisionComment, r.DefinitionId, r.DefinitionVersion, r.BlockId, canAct);

    public async Task<Result<RequestDetail>> DetailAfterAsync(Result<Request> acted, CancellationToken cancellationToken) => acted.IsFailure ? acted.Error! : await GetAsync(acted.Value.Id, cancellationToken);
}

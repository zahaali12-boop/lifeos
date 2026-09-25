using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Workflow.Contracts;
using Quicker.Workflow.Domain;
using Quicker.Workflow.Persistence;

namespace Quicker.Workflow.Application;

/// <summary>Definitions, rules and steps (ADR-0020): versioned configuration, validated against the subject catalogue and the tenant's members and roles.</summary>
public sealed class DefinitionService(WorkflowDbContext db, IServiceProvider services, IRoleDirectory roles, IMemberDirectory members, IAuditSink audit, IClock clock, ICurrentPrincipal principal)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] ApproverKinds = ["users", "role"];
    private static readonly string[] Modes = ["any", "all", "quorum"];
    private static readonly string[] ReapprovalPolicies = ["reset", "none"];

    public IReadOnlyList<IWorkflowSubjectProvider> Providers() => services.GetServices<IWorkflowSubjectProvider>().OrderBy(static p => p.EntityType, StringComparer.Ordinal).ToList();

    public IWorkflowSubjectProvider? Provider(string entityType) => services.GetServices<IWorkflowSubjectProvider>().FirstOrDefault(p => string.Equals(p.EntityType, entityType, StringComparison.Ordinal));

    public WorkflowCatalogue Catalogue() =>
        new(Providers().Select(static p => new CatalogueEntry(p.EntityType, p.Label.Values, p.Fields.Select(static f => new CatalogueField(f.Name, f.Type, f.Label.Values)).ToList(), p.BlockKinds)).ToList(), WorkflowTriggers.All, Expressions.FunctionNames);

    public static IReadOnlyDictionary<string, string> FieldTypes(IWorkflowSubjectProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Fields.ToDictionary(static f => f.Name, static f => f.Type, StringComparer.Ordinal);
    }

    public ExpressionValidation Validate(ValidateExpressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = Provider(request.EntityType);
        if (provider is null)
        {
            return new ExpressionValidation(false, $"Unknown entity type '{request.EntityType}'.", null, []);
        }

        var compiled = Expressions.Compile(request.Expression, FieldTypes(provider), openFields: request.Trigger == WorkflowTriggers.OnBlock);
        if (compiled.IsFailure)
        {
            var position = compiled.Error!.Why?.TryGetValue("position", out var p) == true && p is int pos ? pos : (int?)null;
            return new ExpressionValidation(false, compiled.Error.Message, position, []);
        }

        return new ExpressionValidation(true, null, null, compiled.Value.Fields);
    }

    public async Task<IReadOnlyList<DefinitionSummary>> ListAsync(string? entityType, string? status, CancellationToken cancellationToken)
    {
        var query = db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(d => d.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        var list = await query.OrderBy(static d => d.EntityType).ThenBy(static d => d.Trigger).ThenByDescending(static d => d.Version).ToListAsync(cancellationToken);
        return list.Select(Map).ToList();
    }

    public async Task<DefinitionSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return definition is null ? null : Map(definition);
    }

    public async Task<Result<DefinitionSummary>> CreateAsync(SaveDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = new Definition { Id = Guid.CreateVersion7(), LineageId = Guid.CreateVersion7(), Version = 1, Status = "draft", CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(definition, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Definitions.Add(definition);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_definition", definition.Id, definition.Name.Resolve("en"), AuditActions.Created, After: new { definition.EntityType, definition.Trigger, definition.Version }), cancellationToken);
        return Map(definition);
    }

    /// <summary>A draft is edited in place; an active definition gets a new draft version in its lineage; a retired one is not edited.</summary>
    public async Task<Result<DefinitionSummary>> UpdateAsync(Guid id, SaveDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (existing is null)
        {
            return Error.NotFound("workflow_definition", id);
        }

        if (existing.Status == "retired")
        {
            return Error.Conflict("workflow.definition_retired", "A retired definition is not edited; create a new one.").WithWhy(("status", existing.Status));
        }

        Definition target;
        if (existing.Status == "active")
        {
            var latest = await db.Definitions.Where(d => d.LineageId == existing.LineageId).MaxAsync(static d => d.Version, cancellationToken);
            target = new Definition { Id = Guid.CreateVersion7(), LineageId = existing.LineageId, Version = latest + 1, Status = "draft", CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
            db.Definitions.Add(target);
        }
        else
        {
            target = existing;
            db.Rules.RemoveRange(target.Rules);
            target.Rules = [];
            target.UpdatedAt = clock.UtcNow;
        }

        var applied = await ApplyAsync(target, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_definition", target.Id, target.Name.Resolve("en"), AuditActions.Updated, After: new { target.EntityType, target.Trigger, target.Version, target.Status }), cancellationToken);
        return Map(target);
    }

    /// <summary>Activates a draft: the definition active for the same entity type, trigger and block kind is retired in the same step.</summary>
    public async Task<Result<DefinitionSummary>> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is null)
        {
            return Error.NotFound("workflow_definition", id);
        }

        if (definition.Status != "draft")
        {
            return Error.Conflict("workflow.definition_not_draft", "Only a draft definition is activated.").WithWhy(("status", definition.Status));
        }

        if (definition.Rules.Count == 0)
        {
            return Error.Validation("workflow.rules_required", "A definition needs at least one rule before it is activated.");
        }

        var previous = await db.Definitions.Where(d => d.EntityType == definition.EntityType && d.Trigger == definition.Trigger && d.BlockKind == definition.BlockKind && d.Status == "active").ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Status = "retired";
            old.RetiredAt = clock.UtcNow;
            old.UpdatedAt = clock.UtcNow;
        }

        definition.Status = "active";
        definition.ActivatedAt = clock.UtcNow;
        definition.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_definition", definition.Id, definition.Name.Resolve("en"), AuditActions.StateChanged, After: new { status = "active", retired = previous.Select(static p => p.Id) }), cancellationToken);
        return Map(definition);
    }

    public async Task<Result<DefinitionSummary>> RetireAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.Include(static d => d.Rules).ThenInclude(static r => r.Steps).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is null)
        {
            return Error.NotFound("workflow_definition", id);
        }

        if (definition.Status == "retired")
        {
            return Map(definition);
        }

        definition.Status = "retired";
        definition.RetiredAt = clock.UtcNow;
        definition.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("workflow_definition", definition.Id, definition.Name.Resolve("en"), AuditActions.StateChanged, After: new { status = "retired" }), cancellationToken);
        return Map(definition);
    }

    private async Task<Result> ApplyAsync(Definition definition, SaveDefinitionRequest request, CancellationToken cancellationToken)
    {
        var provider = Provider(request.EntityType?.Trim() ?? string.Empty);
        if (provider is null)
        {
            return Error.Validation("workflow.entity_type_unknown", "No module registers this entity type for approvals.").WithWhy(("entityType", request.EntityType), ("known", Providers().Select(static p => p.EntityType)));
        }

        if (!WorkflowTriggers.All.Contains(request.Trigger, StringComparer.Ordinal))
        {
            return Error.Validation("workflow.trigger_invalid", "The trigger is on_submit, on_post_attempt or on_block.").WithWhy(("trigger", request.Trigger));
        }

        var blockKind = string.IsNullOrWhiteSpace(request.BlockKind) ? null : request.BlockKind.Trim();
        if (request.Trigger == WorkflowTriggers.OnBlock)
        {
            if (blockKind is null || !provider.BlockKinds.Contains(blockKind, StringComparer.Ordinal))
            {
                return Error.Validation("workflow.block_kind_invalid", "A definition on a block names one of the kinds the module raises.").WithWhy(("blockKind", request.BlockKind), ("known", provider.BlockKinds));
            }
        }
        else if (blockKind is not null)
        {
            return Error.Validation("workflow.block_kind_invalid", "Only a definition on a block names a block kind.").WithWhy(("trigger", request.Trigger));
        }

        if (request.Name is null || request.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("workflow.name_required", "A definition has a name.");
        }

        if (!ReapprovalPolicies.Contains(request.ReapprovalPolicy, StringComparer.Ordinal))
        {
            return Error.Validation("workflow.reapproval_policy_invalid", "The re-approval policy is 'reset' or 'none'.").WithWhy(("reapprovalPolicy", request.ReapprovalPolicy));
        }

        if (request.OverrideValidHours <= 0)
        {
            return Error.Validation("workflow.override_hours_invalid", "An override is valid for at least one hour.");
        }

        if (request.Rules is null || request.Rules.Count == 0)
        {
            return Error.Validation("workflow.rules_required", "A definition needs at least one rule.");
        }

        var fieldTypes = FieldTypes(provider);
        var rules = new List<Rule>();
        for (var r = 0; r < request.Rules.Count; r++)
        {
            var ruleRequest = request.Rules[r];
            var compiled = Expressions.Compile(ruleRequest.Condition, fieldTypes, openFields: request.Trigger == WorkflowTriggers.OnBlock);
            if (compiled.IsFailure)
            {
                return compiled.Error!.WithWhy(("rule", r + 1));
            }

            if (ruleRequest.Steps is null || ruleRequest.Steps.Count == 0)
            {
                return Error.Validation("workflow.steps_required", "A rule needs at least one approval step.").WithWhy(("rule", r + 1));
            }

            var rule = new Rule { Id = Guid.CreateVersion7(), TenantId = definition.TenantId, DefinitionId = definition.Id, SortOrder = r + 1, Name = new LocalizedText(ruleRequest.Name ?? new Dictionary<string, string>(StringComparer.Ordinal)), Condition = ruleRequest.Condition.Trim() };
            for (var s = 0; s < ruleRequest.Steps.Count; s++)
            {
                var stepRequest = ruleRequest.Steps[s];
                var validated = await ValidateStepAsync(stepRequest, r + 1, s + 1, cancellationToken);
                if (validated.IsFailure)
                {
                    return validated.Error!;
                }

                rule.Steps.Add(new Step
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = definition.TenantId,
                    RuleId = rule.Id,
                    SortOrder = s + 1,
                    Name = new LocalizedText(stepRequest.Name ?? new Dictionary<string, string>(StringComparer.Ordinal)),
                    ApproverKind = stepRequest.ApproverKind,
                    ApproverSpec = JsonSerializer.Serialize(stepRequest.Approvers, Json),
                    Mode = stepRequest.Mode,
                    Quorum = stepRequest.Mode == "quorum" ? stepRequest.Quorum : null,
                    TimeoutHours = stepRequest.TimeoutHours,
                    Escalation = stepRequest.Escalation is null ? null : JsonSerializer.Serialize(stepRequest.Escalation, Json),
                    AllowDelegate = stepRequest.AllowDelegate,
                    RequireComment = stepRequest.RequireComment,
                    RequireStepUp = stepRequest.RequireStepUp,
                });
            }

            rules.Add(rule);
        }

        definition.EntityType = provider.EntityType;
        definition.Trigger = request.Trigger;
        definition.BlockKind = blockKind;
        definition.Name = new LocalizedText(request.Name);
        definition.Description = new LocalizedText(request.Description ?? new Dictionary<string, string>(StringComparer.Ordinal));
        definition.ReapprovalPolicy = request.ReapprovalPolicy;
        definition.OverrideValidHours = request.OverrideValidHours;
        definition.Rules = rules;
        return Result.Success();
    }

    private async Task<Result> ValidateStepAsync(SaveStepRequest step, int ruleNo, int stepNo, CancellationToken cancellationToken)
    {
        var where = new (string, object?)[] { ("rule", ruleNo), ("step", stepNo) };
        if (!ApproverKinds.Contains(step.ApproverKind, StringComparer.Ordinal))
        {
            return Error.Validation("workflow.approver_kind_invalid", "Approvers are named members ('users') or the holders of a role ('role').").WithWhy(where);
        }

        if (!Modes.Contains(step.Mode, StringComparer.Ordinal))
        {
            return Error.Validation("workflow.mode_invalid", "A step is decided by any one approver, all of them, or a quorum.").WithWhy(where);
        }

        if (step.Mode == "quorum" && (step.Quorum is null || step.Quorum < 1))
        {
            return Error.Validation("workflow.quorum_invalid", "A quorum step names how many approvals it needs.").WithWhy(where);
        }

        if (step.TimeoutHours is <= 0)
        {
            return Error.Validation("workflow.timeout_invalid", "A timeout is a positive number of hours.").WithWhy(where);
        }

        var approvers = await ValidateSpecAsync(step.ApproverKind, step.Approvers, where, cancellationToken);
        if (approvers.IsFailure)
        {
            return approvers;
        }

        if (step.Escalation is not null)
        {
            var kind = step.Escalation.RoleCode is not null ? "role" : "users";
            var escalation = await ValidateSpecAsync(kind, step.Escalation, where, cancellationToken);
            if (escalation.IsFailure)
            {
                return escalation;
            }
        }

        return Result.Success();
    }

    private async Task<Result> ValidateSpecAsync(string kind, ApproverSpecRequest? spec, (string, object?)[] where, CancellationToken cancellationToken)
    {
        if (spec is null)
        {
            return Error.Validation("workflow.approvers_required", "A step names its approvers.").WithWhy(where);
        }

        if (kind == "users")
        {
            if (spec.MembershipIds is null || spec.MembershipIds.Count == 0)
            {
                return Error.Validation("workflow.approvers_required", "A step with named approvers lists at least one member.").WithWhy(where);
            }

            foreach (var membershipId in spec.MembershipIds)
            {
                var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
                if (member is null || !member.IsActive)
                {
                    return Error.Validation("workflow.approver_unknown", "An approver is not an active member of this workspace.").WithWhy([.. where, ("membershipId", membershipId)]);
                }
            }

            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(spec.RoleCode))
        {
            return Error.Validation("workflow.role_required", "A step approved by a role names the role.").WithWhy(where);
        }

        var role = await roles.FindRoleByCodeAsync(spec.RoleCode.Trim(), cancellationToken);
        if (role is null || !role.IsActive)
        {
            return Error.Validation("workflow.role_unknown", "No active role has this code.").WithWhy([.. where, ("roleCode", spec.RoleCode)]);
        }

        return Result.Success();
    }

    internal static DefinitionSummary Map(Definition d) => new(
        d.Id, d.LineageId, d.EntityType, d.Trigger, d.BlockKind, d.Name.Values, d.Description.Values, d.Version, d.Status, d.ReapprovalPolicy, d.OverrideValidHours, d.ActivatedAt, d.UpdatedAt,
        d.Rules.OrderBy(static r => r.SortOrder).Select(static r => new RuleSummary(r.Id, r.SortOrder, r.Name.Values, r.Condition,
            r.Steps.OrderBy(static s => s.SortOrder).Select(static s => new StepSummary(s.Id, s.SortOrder, s.Name.Values, s.ApproverKind, ReadSpec(s.ApproverSpec) ?? new ApproverSpecRequest(), s.Mode, s.Quorum, s.TimeoutHours, s.Escalation is null ? null : ReadSpec(s.Escalation), s.AllowDelegate, s.RequireComment, s.RequireStepUp)).ToList())).ToList());

    internal static ApproverSpecRequest? ReadSpec(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ApproverSpecRequest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Identity.Application;

/// <summary>Roles, permission grants, field and document-type rules, assignments with scopes, members, SoD checks.</summary>
public sealed class RoleService(IdentityDbContext db, IUnitOfWorkAccessor unitOfWork, ITenantDirectory tenants, IAuditSink audit, IClock clock)
{
    private Guid TenantId => unitOfWork.Current.Context.TenantId.Value;

    // ------------------------------------------------------------------ roles

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await Query().OrderBy(static r => r.Code).ToListAsync(cancellationToken);
        return roles.Select(Map).ToList();
    }

    public async Task<RoleSummary?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await Query().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        return role is null ? null : Map(role);
    }

    public async Task<Result<RoleSummary>> CreateRoleAsync(SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request);
        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var code = request.Code.Trim().ToLowerInvariant();
        if (await db.Roles.AnyAsync(r => r.Code == code, cancellationToken))
        {
            return Error.Conflict("role.code_taken", $"A role with code '{code}' already exists.");
        }

        var conflicts = await SodConflictsForGrantsAsync(request.Grants, null, cancellationToken);
        if (conflicts.Any(static c => c.Severity == "block"))
        {
            return Error.Conflict("role.sod_conflict", "The role combines duties that must be segregated.").WithWhy(("conflicts", conflicts));
        }

        var now = clock.UtcNow;
        var role = new Role { Id = Guid.CreateVersion7(), Code = code, Name = new LocalizedText(request.Name), Description = request.Description.Trim(), IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now };
        Apply(role, request);
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken); // the audit capture records "created" with the full snapshot
        await tenants.BumpPermissionsEpochAsync(new TenantId(TenantId), cancellationToken);
        return Map(role);
    }

    public async Task<Result<RoleSummary>> UpdateRoleAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var role = await Query().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return Error.NotFound("role", roleId);
        }

        if (role.IsSystem && role.Code == "owner")
        {
            return Error.Forbidden("role.system_locked", "The owner role cannot be edited.");
        }

        var validation = Validate(request);
        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var conflicts = await SodConflictsForGrantsAsync(request.Grants, null, cancellationToken);
        if (conflicts.Any(static c => c.Severity == "block"))
        {
            return Error.Conflict("role.sod_conflict", "The role combines duties that must be segregated.").WithWhy(("conflicts", conflicts));
        }

        var before = Map(role);
        role.Name = new LocalizedText(request.Name);
        role.Description = request.Description.Trim();
        role.IsActive = request.IsActive;
        role.UpdatedAt = clock.UtcNow;
        db.RemoveRange(role.Permissions);
        db.RemoveRange(role.FieldRules);
        db.RemoveRange(role.DocumentTypeRules);
        role.Permissions.Clear();
        role.FieldRules.Clear();
        role.DocumentTypeRules.Clear();
        Apply(role, request);
        await db.SaveChangesAsync(cancellationToken);
        await tenants.BumpPermissionsEpochAsync(new TenantId(TenantId), cancellationToken);
        await audit.RecordAsync(new AuditEntry("role", role.Id, role.Code, AuditActions.PermissionChanged, Before: before, After: Map(role)), cancellationToken);
        return Map(role);
    }

    public async Task<Result> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await Query().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return Error.NotFound("role", roleId);
        }

        if (role.IsSystem)
        {
            return Error.Forbidden("role.system_locked", "System roles cannot be deleted; deactivate them instead.");
        }

        if (await db.Assignments.AnyAsync(a => a.RoleId == roleId, cancellationToken))
        {
            return Error.Conflict("role.in_use", "Remove the role from all members before deleting it.");
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(cancellationToken); // captured as "deleted" with the last snapshot
        return Result.Success();
    }

    /// <summary>Creates the shipped roles and SoD rules for a new tenant; returns the owner role.</summary>
    public async Task<Role> SeedDefaultsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        Role? owner = null;
        foreach (var template in RoleTemplates.All)
        {
            var role = new Role { Id = Guid.CreateVersion7(), Code = template.Code, Name = template.Name, Description = template.Description, IsSystem = true, TemplateCode = template.Code, CreatedAt = now, UpdatedAt = now };
            role.Permissions.AddRange(template.Grants.Select(g => new RolePermission { RoleId = role.Id, PermissionKey = g }));
            db.Roles.Add(role);
            owner ??= role.Code == "owner" ? role : null;
        }

        foreach (var rule in DefaultSodRules.All)
        {
            db.SodRules.Add(new SodRule { Id = Guid.CreateVersion7(), PermissionA = rule.PermissionA, PermissionB = rule.PermissionB, Severity = rule.Severity, Rationale = rule.Rationale, IsSystem = true });
        }

        await db.SaveChangesAsync(cancellationToken);
        return owner!;
    }

    // ------------------------------------------------------------------ assignments

    public async Task<Result<AssignmentSummary>> AssignAsync(Guid membershipId, AssignRoleRequest request, Guid actorUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == TenantId, cancellationToken);
        if (membership is null)
        {
            return Error.NotFound("member", membershipId);
        }

        var role = await Query().SingleOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken);
        if (role is null)
        {
            return Error.NotFound("role", request.RoleId);
        }

        if (request.Scopes?.Any(static s => s.ScopeType is not ("company" or "branch" or "warehouse")) == true)
        {
            return Error.Validation("assignment.scope_invalid", "Scope type must be company, branch or warehouse.");
        }

        if (request.ValidFrom is { } from && request.ValidTo is { } to && to < from)
        {
            return Error.Validation("assignment.dates_invalid", "Valid-to must be on or after valid-from.");
        }

        var existingGrants = await GrantsOfMembershipAsync(membershipId, cancellationToken);
        var combined = existingGrants.Select(static g => g.Permission).Concat(role.Permissions.Select(static p => p.PermissionKey)).Distinct(StringComparer.Ordinal).ToList();
        var conflicts = await SodConflictsForGrantsAsync(combined, membershipId, cancellationToken);
        var blocking = conflicts.Where(static c => c.Severity == "block" && !c.HasException).ToList();
        if (blocking.Count > 0)
        {
            return Error.Conflict("assignment.sod_conflict", "This assignment would give the member conflicting duties.").WithWhy(("conflicts", blocking));
        }

        var warnings = conflicts.Where(static c => c.Severity == "warn" && !c.HasException).ToList();
        if (warnings.Count > 0 && !request.AcknowledgeWarnings)
        {
            return Error.Conflict("assignment.sod_warning", "This assignment creates duties that should be segregated; acknowledge to proceed.").WithWhy(("conflicts", warnings));
        }

        var assignment = new RoleAssignment { Id = Guid.CreateVersion7(), MembershipId = membershipId, RoleId = role.Id, ValidFrom = request.ValidFrom, ValidTo = request.ValidTo, CreatedAt = clock.UtcNow, CreatedBy = actorUserId, Role = role };
        assignment.Scopes.AddRange((request.Scopes ?? []).Select(s => new AssignmentScope { AssignmentId = assignment.Id, ScopeType = s.ScopeType, ScopeId = s.ScopeId }));
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);
        await tenants.BumpPermissionsEpochAsync(new TenantId(TenantId), cancellationToken);
        var summary = Map(assignment);
        await audit.RecordAsync(new AuditEntry("membership", membershipId, membership.UserId.ToString(), AuditActions.PermissionChanged, After: summary,
            Details: warnings.Count > 0 ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["acknowledgedSodWarnings"] = warnings } : null), cancellationToken);
        return summary;
    }

    public async Task<Result> UnassignAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await db.Assignments.Include(static a => a.Role).SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return Error.NotFound("assignment", assignmentId);
        }

        var membership = await db.Memberships.SingleAsync(m => m.Id == assignment.MembershipId, cancellationToken);
        if (membership.IsOwner && assignment.Role.Code == "owner")
        {
            var otherOwners = await db.Memberships.CountAsync(m => m.TenantId == TenantId && m.IsOwner && m.Status == "active" && m.Id != membership.Id, cancellationToken);
            if (otherOwners == 0)
            {
                return Error.Conflict("assignment.last_owner", "The tenant must keep at least one owner.");
            }
        }

        db.Assignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
        await tenants.BumpPermissionsEpochAsync(new TenantId(TenantId), cancellationToken);
        await audit.RecordAsync(new AuditEntry("membership", assignment.MembershipId, assignment.MembershipId.ToString(), AuditActions.PermissionChanged, Before: Map(assignment)), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ members

    public async Task<IReadOnlyList<MemberSummary>> ListMembersAsync(CancellationToken cancellationToken)
    {
        var memberships = await db.Memberships.Include(static m => m.User).ThenInclude(static u => u.MfaMethods)
            .Where(m => m.TenantId == TenantId).OrderBy(static m => m.User.Email).ToListAsync(cancellationToken);
        var assignments = await db.Assignments.Include(static a => a.Role).Include(static a => a.Scopes).ToListAsync(cancellationToken);
        return memberships.Select(m => Map(m, assignments.Where(a => a.MembershipId == m.Id))).ToList();
    }

    public async Task<MemberSummary?> MemberAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.Include(static m => m.User).ThenInclude(static u => u.MfaMethods).SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == TenantId, cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var assignments = await db.Assignments.Include(static a => a.Role).Include(static a => a.Scopes).Where(a => a.MembershipId == membershipId).ToListAsync(cancellationToken);
        return Map(membership, assignments);
    }

    public async Task<Result<MemberSummary>> SetMemberStatusAsync(Guid membershipId, bool enabled, Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.Include(static m => m.User).SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == TenantId, cancellationToken);
        if (membership is null)
        {
            return Error.NotFound("member", membershipId);
        }

        if (!enabled && membership.Id == actorMembershipId)
        {
            return Error.Conflict("member.self_disable", "You cannot disable your own membership.");
        }

        if (!enabled && membership.IsOwner && await db.Memberships.CountAsync(m => m.TenantId == TenantId && m.IsOwner && m.Status == "active" && m.Id != membershipId, cancellationToken) == 0)
        {
            return Error.Conflict("member.last_owner", "The tenant must keep at least one active owner.");
        }

        var before = membership.Status;
        membership.Status = enabled ? "active" : "disabled";
        if (!enabled)
        {
            var now = clock.UtcNow;
            await db.Sessions.Where(s => s.MembershipId == membershipId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(static x => x.RevokedAt, now).SetProperty(static x => x.RevokedReason, "membership_disabled"), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tenants.BumpPermissionsEpochAsync(new TenantId(TenantId), cancellationToken);
        await audit.RecordAsync(new AuditEntry("membership", membership.Id, membership.User.Email, AuditActions.StateChanged, Before: new { Status = before }, After: new { membership.Status }), cancellationToken);
        return (await MemberAsync(membershipId, cancellationToken))!;
    }

    // ------------------------------------------------------------------ effective grants

    public async Task<(IReadOnlyList<Grant> Grants, IReadOnlyList<FieldRule> FieldRules, IReadOnlyList<DocumentTypeRule> DocumentTypeRules)> EffectiveAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var assignments = await db.Assignments
            .Include(static a => a.Role).ThenInclude(static r => r.Permissions)
            .Include(static a => a.Role).ThenInclude(static r => r.FieldRules)
            .Include(static a => a.Role).ThenInclude(static r => r.DocumentTypeRules)
            .Include(static a => a.Scopes)
            .Where(a => a.MembershipId == membershipId && a.Role.IsActive)
            .ToListAsync(cancellationToken);

        var grants = new List<Grant>();
        var fieldRules = new List<FieldRule>();
        var documentRules = new List<DocumentTypeRule>();
        foreach (var assignment in assignments.Where(a => a.IsValidOn(today)))
        {
            var scopes = new RecordScopes(
                assignment.Scopes.Where(static s => s.ScopeType == "company").Select(static s => s.ScopeId).ToHashSet(),
                assignment.Scopes.Where(static s => s.ScopeType == "branch").Select(static s => s.ScopeId).ToHashSet(),
                assignment.Scopes.Where(static s => s.ScopeType == "warehouse").Select(static s => s.ScopeId).ToHashSet());
            grants.AddRange(assignment.Role.Permissions.Select(p => new Grant(p.PermissionKey, scopes)));
            fieldRules.AddRange(assignment.Role.FieldRules.Select(static f => new FieldRule(f.EntityType, f.Field, f.Access)));
            documentRules.AddRange(assignment.Role.DocumentTypeRules.Select(static d => new DocumentTypeRule(d.DocumentType, d.Action, d.Allowed)));
        }

        return (grants, fieldRules, documentRules);
    }

    private async Task<IReadOnlyList<Grant>> GrantsOfMembershipAsync(Guid membershipId, CancellationToken cancellationToken) => (await EffectiveAsync(membershipId, cancellationToken)).Grants;

    // ------------------------------------------------------------------ segregation of duties

    public async Task<IReadOnlyList<SodRuleSummary>> ListSodRulesAsync(CancellationToken cancellationToken)
    {
        var rules = await db.SodRules.OrderBy(static r => r.PermissionA).ThenBy(static r => r.PermissionB).ToListAsync(cancellationToken);
        return rules.Select(static r => new SodRuleSummary(r.Id, r.PermissionA, r.PermissionB, r.Severity, r.Rationale.Values, r.IsSystem, r.IsActive)).ToList();
    }

    public async Task<Result<SodRuleSummary>> SaveSodRuleAsync(Guid? id, SaveSodRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Severity is not ("warn" or "block"))
        {
            return Error.Validation("sod.severity_invalid", "Severity must be 'warn' or 'block'.");
        }

        if (!PermissionCatalog.IsValidKey(request.PermissionA) || !PermissionCatalog.IsValidKey(request.PermissionB) || request.PermissionA == request.PermissionB)
        {
            return Error.Validation("sod.permissions_invalid", "Provide two different, well-formed permission keys.");
        }

        SodRule rule;
        if (id is { } ruleId)
        {
            rule = await db.SodRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken) ?? new SodRule { Id = Guid.Empty };
            if (rule.Id == Guid.Empty)
            {
                return Error.NotFound("sod_rule", ruleId);
            }
        }
        else
        {
            rule = new SodRule { Id = Guid.CreateVersion7() };
            db.SodRules.Add(rule);
        }

        rule.PermissionA = request.PermissionA;
        rule.PermissionB = request.PermissionB;
        rule.Severity = request.Severity;
        rule.Rationale = new LocalizedText(request.Rationale);
        rule.IsActive = request.IsActive;
        await db.SaveChangesAsync(cancellationToken); // captured as created/updated with the field diff
        return new SodRuleSummary(rule.Id, rule.PermissionA, rule.PermissionB, rule.Severity, rule.Rationale.Values, rule.IsSystem, rule.IsActive);
    }

    public async Task<Result<Guid>> AddSodExceptionAsync(SodExceptionRequest request, Guid approvedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("sod.reason_required", "A reason is required for a segregation-of-duties exception.");
        }

        if (!await db.SodRules.AnyAsync(r => r.Id == request.RuleId, cancellationToken))
        {
            return Error.NotFound("sod_rule", request.RuleId);
        }

        var exception = new SodException { Id = Guid.CreateVersion7(), SodRuleId = request.RuleId, MembershipId = request.MembershipId, Reason = request.Reason.Trim(), ApprovedBy = approvedBy, ExpiresOn = request.ExpiresOn, CreatedAt = clock.UtcNow };
        db.SodExceptions.Add(exception);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("sod_exception", exception.Id, request.MembershipId.ToString(), AuditActions.Override, Reason: request.Reason, After: request), cancellationToken);
        return exception.Id;
    }

    public async Task<IReadOnlyList<SodReportRow>> SodReportAsync(CancellationToken cancellationToken)
    {
        var rows = new List<SodReportRow>();
        foreach (var member in await ListMembersAsync(cancellationToken))
        {
            var grants = await GrantsOfMembershipAsync(member.MembershipId, cancellationToken);
            var conflicts = await SodConflictsForGrantsAsync(grants.Select(static g => g.Permission).ToList(), member.MembershipId, cancellationToken);
            var superUser = member.IsOwner || grants.Any(static g => string.Equals(g.Permission, "*", StringComparison.Ordinal));
            if (conflicts.Count > 0 || superUser)
            {
                rows.Add(new SodReportRow(member.MembershipId, member.Email, member.DisplayName, conflicts, superUser));
            }
        }

        return rows;
    }

    private async Task<IReadOnlyList<SodConflict>> SodConflictsForGrantsAsync(IReadOnlyList<string> grants, Guid? membershipId, CancellationToken cancellationToken)
    {
        var rules = await db.SodRules.Where(static r => r.IsActive).ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var exceptions = membershipId is { } mid
            ? await db.SodExceptions.Where(e => e.MembershipId == mid && (e.ExpiresOn == null || e.ExpiresOn >= today)).Select(static e => e.SodRuleId).ToListAsync(cancellationToken)
            : [];

        // The all-access grant is a deliberate super-user decision, reported as such rather than as a conflict per rule.
        var scoped = grants.Where(static g => !string.Equals(g, "*", StringComparison.Ordinal)).ToList();
        var conflicts = new List<SodConflict>();
        foreach (var rule in rules)
        {
            var hasA = scoped.Any(g => PermissionCatalog.Covers(g, rule.PermissionA));
            var hasB = scoped.Any(g => PermissionCatalog.Covers(g, rule.PermissionB));
            if (hasA && hasB)
            {
                conflicts.Add(new SodConflict(rule.Id, rule.PermissionA, rule.PermissionB, rule.Severity, rule.Rationale.Values, exceptions.Contains(rule.Id)));
            }
        }

        return conflicts;
    }

    // ------------------------------------------------------------------ helpers

    private IQueryable<Role> Query() => db.Roles.Include(static r => r.Permissions).Include(static r => r.FieldRules).Include(static r => r.DocumentTypeRules);

    private static Result Validate(SaveRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64 || !request.Code.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return Error.Validation("role.code_invalid", "Role code must be lower-case letters, digits and underscores.");
        }

        if (request.Name.Count == 0 || request.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("role.name_required", "Role name is required in at least one language.");
        }

        var invalid = request.Grants.Where(static g => !PermissionCatalog.IsValidGrant(g)).ToList();
        if (invalid.Count > 0)
        {
            return Error.Validation("role.grant_unknown", "Unknown permission keys.").WithWhy(("keys", invalid));
        }

        if (request.FieldRules?.Any(static f => f.Access is not ("hidden" or "read_only" or "editable")) == true)
        {
            return Error.Validation("role.field_access_invalid", "Field access must be hidden, read_only or editable.");
        }

        if (request.DocumentTypeRules?.Any(static d => d.Action is not ("create" or "approve" or "post" or "reverse" or "export")) == true)
        {
            return Error.Validation("role.document_action_invalid", "Document action must be create, approve, post, reverse or export.");
        }

        return Result.Success();
    }

    private static void Apply(Role role, SaveRoleRequest request)
    {
        role.Permissions.AddRange(request.Grants.Distinct(StringComparer.Ordinal).Select(g => new RolePermission { RoleId = role.Id, PermissionKey = g }));
        role.FieldRules.AddRange((request.FieldRules ?? []).Select(f => new FieldRuleEntity { Id = Guid.CreateVersion7(), RoleId = role.Id, EntityType = f.EntityType, Field = f.Field, Access = f.Access }));
        role.DocumentTypeRules.AddRange((request.DocumentTypeRules ?? []).Select(d => new DocumentTypeRuleEntity { Id = Guid.CreateVersion7(), RoleId = role.Id, DocumentType = d.DocumentType, Action = d.Action, Allowed = d.Allowed }));
    }

    private static RoleSummary Map(Role role) => new(
        role.Id, role.Code, role.Name.Values, role.Description, role.IsSystem, role.TemplateCode, role.IsActive,
        role.Permissions.Select(static p => p.PermissionKey).OrderBy(static k => k, StringComparer.Ordinal).ToList(),
        role.FieldRules.Select(static f => new FieldRule(f.EntityType, f.Field, f.Access)).ToList(),
        role.DocumentTypeRules.Select(static d => new DocumentTypeRule(d.DocumentType, d.Action, d.Allowed)).ToList());

    private static AssignmentSummary Map(RoleAssignment a) => new(a.Id, a.RoleId, a.Role.Code, a.Scopes.Select(static s => new ScopeInput(s.ScopeType, s.ScopeId)).ToList(), a.ValidFrom, a.ValidTo);

    private static MemberSummary Map(TenantMembership m, IEnumerable<RoleAssignment> assignments) => new(
        m.Id, m.UserId, m.User.Email, m.User.DisplayName, m.Status, m.IsOwner, m.User.HasMfa, m.User.LastLoginAt, assignments.Select(Map).ToList());
}

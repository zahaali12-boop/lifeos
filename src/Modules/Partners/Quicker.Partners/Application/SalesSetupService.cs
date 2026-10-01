using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>
/// The sales organisation as configuration: sales reps (members of the workspace, optionally employee partners, for
/// one company or all), commission plans with tiered rates by item category and customer group, and the pipeline's
/// stages every opportunity moves through.
/// </summary>
public sealed class SalesSetupService(PartnersDbContext db, ICompanyDirectory companies, IMemberDirectory members, IItemDirectory items, IClock clock)
{
    // ------------------------------------------------------------------ sales reps

    public async Task<IReadOnlyList<SalesRepSummary>> ListRepsAsync(CancellationToken cancellationToken)
    {
        var reps = await db.SalesReps.AsNoTracking().OrderBy(static r => r.Code).ToListAsync(cancellationToken);
        return await MapRepsAsync(reps, cancellationToken);
    }

    public async Task<Result<SalesRepSummary>> SaveRepAsync(Guid? repId, SaveSalesRepRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "sales_rep");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "sales_rep");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var email = Validation.Email(request.Email, "sales_rep");
        if (email.IsFailure)
        {
            return email.Error!;
        }

        SalesRep? rep = null;
        if (repId is { } id)
        {
            rep = await db.SalesReps.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (rep is null)
            {
                return Error.NotFound("sales_rep", id);
            }
        }

        if (await db.SalesReps.AnyAsync(r => r.Code == code.Value && r.Id != repId, cancellationToken))
        {
            return Error.Conflict("sales_rep.code_taken", $"A sales rep with code '{code.Value}' already exists.");
        }

        if (request.MembershipId is { } membershipId)
        {
            var member = await members.FindAsync(new MembershipId(membershipId), cancellationToken);
            if (member is null)
            {
                return Error.Validation("sales_rep.member_unknown", "The member is not part of this workspace.").WithWhy(("membershipId", membershipId));
            }

            if (await db.SalesReps.AnyAsync(r => r.MembershipId == membershipId && r.Id != repId, cancellationToken))
            {
                return Error.Conflict("sales_rep.member_taken", "The member is already another sales rep.").WithWhy(("membershipId", membershipId));
            }
        }

        if (request.PartnerId is { } partnerId && !await db.Partners.AnyAsync(p => p.Id == partnerId && p.IsEmployee, cancellationToken))
        {
            return Error.Validation("sales_rep.partner_not_employee", "The partner must be an employee partner.").WithWhy(("partnerId", partnerId));
        }

        if (request.CompanyId is { } companyId && await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.Validation("sales_rep.company_unknown", "The company does not exist.").WithWhy(("companyId", companyId));
        }

        if (request.CommissionPlanId is { } planId && !await db.CommissionPlans.AnyAsync(p => p.Id == planId && p.IsActive, cancellationToken))
        {
            return Error.Validation("sales_rep.commission_plan_unknown", "The commission plan does not exist or is inactive.").WithWhy(("commissionPlanId", planId));
        }

        if (rep is not null && request.CompanyId is { } narrowed && narrowed != rep.CompanyId
            && await db.CustomerAccounts.AnyAsync(a => a.SalesRepId == rep.Id && a.CompanyId != narrowed, cancellationToken))
        {
            return Error.Conflict("sales_rep.customers_elsewhere", "The rep looks after customers of other companies; reassign them first.").WithWhy(("salesRep", rep.Code));
        }

        var isNew = rep is null;
        rep ??= new SalesRep { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        rep.Code = code.Value;
        rep.Name = name.Value;
        rep.MembershipId = request.MembershipId;
        rep.PartnerId = request.PartnerId;
        rep.CompanyId = request.CompanyId;
        rep.CommissionPlanId = request.CommissionPlanId;
        rep.Email = email.Value;
        rep.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        rep.IsActive = request.IsActive;
        rep.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.SalesReps.Add(rep);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapRepsAsync([rep], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<SalesRepSummary>> MapRepsAsync(IReadOnlyList<SalesRep> reps, CancellationToken cancellationToken)
    {
        var ids = reps.Select(static r => r.Id).ToList();
        var customers = await db.CustomerAccounts.AsNoTracking().Where(a => a.SalesRepId != null && ids.Contains(a.SalesRepId.Value)).GroupBy(static a => a.SalesRepId!.Value).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var open = await db.Opportunities.AsNoTracking().Where(o => o.Status == OpportunityOutcomes.Open && o.SalesRepId != null && ids.Contains(o.SalesRepId.Value)).GroupBy(static o => o.SalesRepId!.Value).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var partnerIds = reps.Where(static r => r.PartnerId != null).Select(static r => r.PartnerId!.Value).ToList();
        var partners = await db.Partners.AsNoTracking().Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(static p => p.Id, static p => p.Code, cancellationToken);
        var plans = await db.CommissionPlans.AsNoTracking().ToDictionaryAsync(static p => p.Id, static p => p.Code, cancellationToken);
        var companyCodes = (await companies.ListAsync(cancellationToken)).ToDictionary(static c => c.Id.Value, static c => c.Code);
        var memberNames = reps.Any(static r => r.MembershipId != null)
            ? (await members.ListActiveAsync(cancellationToken)).ToDictionary(static m => m.MembershipId.Value, static m => m.DisplayName)
            : [];
        return reps.Select(r => new SalesRepSummary(
            r.Id, r.Code, r.Name.Values, r.MembershipId, r.MembershipId is { } m ? memberNames.GetValueOrDefault(m) : null,
            r.PartnerId, r.PartnerId is { } p ? partners.GetValueOrDefault(p) : null,
            r.CompanyId, r.CompanyId is { } c ? companyCodes.GetValueOrDefault(c) : null,
            r.CommissionPlanId, r.CommissionPlanId is { } plan ? plans.GetValueOrDefault(plan) : null,
            r.Email, r.Phone, r.IsActive, customers.GetValueOrDefault(r.Id), open.GetValueOrDefault(r.Id), r.UpdatedAt)).ToList();
    }

    // ------------------------------------------------------------------ commission plans

    public async Task<IReadOnlyList<CommissionPlanSummary>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await db.CommissionPlans.AsNoTracking().Include(static p => p.Rules).OrderBy(static p => p.Code).ToListAsync(cancellationToken);
        return await MapPlansAsync(plans, cancellationToken);
    }

    public async Task<Result<CommissionPlanSummary>> SavePlanAsync(Guid? planId, SaveCommissionPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "commission_plan");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "commission_plan");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var basis = Validation.OneOf(request.Basis, "commission_plan.basis", CommissionBases.All);
        if (basis.IsFailure)
        {
            return basis.Error!;
        }

        var accrual = Validation.OneOf(request.AccrualPoint, "commission_plan.accrual_point", CommissionAccrualPoints.All);
        if (accrual.IsFailure)
        {
            return accrual.Error!;
        }

        if (basis.Value == CommissionBases.Collected && accrual.Value != CommissionAccrualPoints.Payment)
        {
            return Error.Validation("commission_plan.collected_accrues_at_payment", "Commission on what was collected accrues when the payment arrives.");
        }

        var period = Validation.OneOf(request.TierPeriod, "commission_plan.tier_period", CommissionTierPeriods.All);
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var currency = await companies.FindCurrencyAsync((request.Currency ?? string.Empty).Trim().ToUpperInvariant(), cancellationToken);
        if (currency is null)
        {
            return Error.Validation("commission_plan.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", request.Currency));
        }

        var rules = request.Rules ?? [];
        if (rules.Count == 0)
        {
            return Error.Validation("commission_plan.rules_required", "A plan has at least one rate.");
        }

        foreach (var rule in rules)
        {
            var rate = Validation.Percentage(rule.RatePct, "commission_plan.rate");
            if (rate.IsFailure)
            {
                return rate.Error!;
            }

            if (rule.FromAmount < 0m)
            {
                return Error.Validation("commission_plan.threshold_invalid", "A threshold is zero or more.").WithWhy(("fromAmount", rule.FromAmount));
            }

            if (rule.ItemCategoryId is { } category && (await items.CategoryLineageAsync(category, cancellationToken)).Count == 0)
            {
                return Error.Validation("commission_plan.category_unknown", "The item category does not exist.").WithWhy(("itemCategoryId", category));
            }

            if (rule.CustomerGroupId is { } group && !await db.CustomerGroups.AnyAsync(g => g.Id == group, cancellationToken))
            {
                return Error.Validation("commission_plan.customer_group_unknown", "The customer group does not exist.").WithWhy(("customerGroupId", group));
            }
        }

        var duplicate = rules.GroupBy(static r => (r.ItemCategoryId, r.CustomerGroupId, r.FromAmount)).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Validation("commission_plan.rule_duplicate", "Two rates share a scope and threshold.").WithWhy(("itemCategoryId", duplicate.Key.ItemCategoryId), ("customerGroupId", duplicate.Key.CustomerGroupId), ("fromAmount", duplicate.Key.FromAmount));
        }

        CommissionPlan? plan = null;
        if (planId is { } id)
        {
            plan = await db.CommissionPlans.Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (plan is null)
            {
                return Error.NotFound("commission_plan", id);
            }

            if (!request.IsActive && plan.IsActive && await db.SalesReps.AnyAsync(r => r.CommissionPlanId == id && r.IsActive, cancellationToken))
            {
                return Error.Conflict("commission_plan.in_use", "Active sales reps are paid under this plan; move them to another first.");
            }
        }

        if (await db.CommissionPlans.AnyAsync(p => p.Code == code.Value && p.Id != planId, cancellationToken))
        {
            return Error.Conflict("commission_plan.code_taken", $"A commission plan with code '{code.Value}' already exists.");
        }

        var isNew = plan is null;
        plan ??= new CommissionPlan { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        plan.Code = code.Value;
        plan.Name = name.Value;
        plan.Basis = basis.Value;
        plan.AccrualPoint = accrual.Value;
        plan.TierPeriod = period.Value;
        plan.Currency = currency.Value.Code;
        plan.IsActive = request.IsActive;
        plan.UpdatedAt = clock.UtcNow;
        plan.Rules.Clear();
        if (!isNew)
        {
            // The rules are replaced wholesale; removing the old rows first keeps (plan, sequence) free for the new ones.
            await db.SaveChangesAsync(cancellationToken);
        }

        plan.Rules.AddRange(rules.Select((r, i) => new CommissionRule { PlanId = plan.Id, Sequence = i + 1, ItemCategoryId = r.ItemCategoryId, CustomerGroupId = r.CustomerGroupId, FromAmount = r.FromAmount, RatePct = r.RatePct }));
        if (isNew)
        {
            db.CommissionPlans.Add(plan);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapPlansAsync([plan], cancellationToken))[0];
    }

    /// <summary>What a sale earns under the plan: the matching scope's bands from the period-to-date basis on (see <see cref="CommissionMath"/>).</summary>
    public async Task<Result<CommissionQuote>> QuoteAsync(Guid planId, Guid? itemCategoryId, Guid? customerGroupId, decimal periodToDate, decimal basis, CancellationToken cancellationToken)
    {
        var plan = await db.CommissionPlans.AsNoTracking().Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("commission_plan", planId);
        }

        var currency = await companies.FindCurrencyAsync(plan.Currency, cancellationToken);
        if (currency is null)
        {
            return Error.Validation("commission_plan.currency_unknown", "The plan's currency is not known.").WithWhy(("currency", plan.Currency));
        }

        var lineage = itemCategoryId is { } category ? (await items.CategoryLineageAsync(category, cancellationToken)).Select(static c => c.Id).ToList() : [];
        var (matchedCategory, matchedGroup, tiers) = MatchScope(plan.Rules, lineage, customerGroupId);
        var crossed = CommissionMath.Crossed(tiers, periodToDate, basis);
        var (bands, total) = CommissionMath.Round(crossed, currency.Value, RoundingPolicy.Default);
        return new CommissionQuote(plan.Id, plan.Code, plan.Currency, matchedCategory, matchedGroup, bands, total);
    }

    /// <summary>
    /// The most specific scope with rates for a sale: a rule naming both a category (the nearest in the item's lineage)
    /// and the customer's group, then a category alone (nearest first), then the group alone, then the plan-wide rates.
    /// </summary>
    internal static (Guid? Category, Guid? Group, IReadOnlyList<(decimal FromAmount, decimal RatePct)> Tiers) MatchScope(IReadOnlyList<CommissionRule> rules, IReadOnlyList<Guid> lineage, Guid? customerGroupId)
    {
        var candidates = new List<(Guid? Category, Guid? Group)>();
        if (customerGroupId is { } g)
        {
            candidates.AddRange(lineage.Select(c => ((Guid?)c, (Guid?)g)));
        }

        candidates.AddRange(lineage.Select(static c => ((Guid?)c, (Guid?)null)));
        if (customerGroupId is { } groupAlone)
        {
            candidates.Add((null, groupAlone));
        }

        candidates.Add((null, null));
        foreach (var (category, group) in candidates)
        {
            var tiers = rules.Where(r => r.ItemCategoryId == category && r.CustomerGroupId == group).Select(static r => (r.FromAmount, r.RatePct)).ToList();
            if (tiers.Count > 0)
            {
                return (category, group, tiers);
            }
        }

        return (null, null, []);
    }

    private async Task<IReadOnlyList<CommissionPlanSummary>> MapPlansAsync(IReadOnlyList<CommissionPlan> plans, CancellationToken cancellationToken)
    {
        var groups = await db.CustomerGroups.AsNoTracking().ToDictionaryAsync(static g => g.Id, static g => g.Code, cancellationToken);
        var categoryCodes = new Dictionary<Guid, string>();
        foreach (var categoryId in plans.SelectMany(static p => p.Rules).Where(static r => r.ItemCategoryId != null).Select(static r => r.ItemCategoryId!.Value).Distinct())
        {
            var lineage = await items.CategoryLineageAsync(categoryId, cancellationToken);
            if (lineage.Count > 0)
            {
                categoryCodes[categoryId] = lineage[0].Code;
            }
        }

        var ids = plans.Select(static p => p.Id).ToList();
        var reps = await db.SalesReps.AsNoTracking().Where(r => r.CommissionPlanId != null && ids.Contains(r.CommissionPlanId.Value)).GroupBy(static r => r.CommissionPlanId!.Value).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return plans.Select(p => new CommissionPlanSummary(
            p.Id, p.Code, p.Name.Values, p.Basis, p.AccrualPoint, p.TierPeriod, p.Currency,
            p.Rules.OrderBy(static r => r.Sequence).Select(r => new CommissionRuleSummary(
                r.Sequence, r.ItemCategoryId, r.ItemCategoryId is { } c ? categoryCodes.GetValueOrDefault(c) : null,
                r.CustomerGroupId, r.CustomerGroupId is { } g ? groups.GetValueOrDefault(g) : null, r.FromAmount, r.RatePct)).ToList(),
            p.IsActive, reps.GetValueOrDefault(p.Id), p.UpdatedAt)).ToList();
    }

    // ------------------------------------------------------------------ pipeline stages

    public async Task<IReadOnlyList<PipelineStageSummary>> ListStagesAsync(CancellationToken cancellationToken)
    {
        var stages = await db.PipelineStages.AsNoTracking().OrderBy(static s => s.SortOrder).ThenBy(static s => s.Code).ToListAsync(cancellationToken);
        var open = await OpenCountsAsync(cancellationToken);
        return stages.Select(s => Map(s, open.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<Result<PipelineStageSummary>> SaveStageAsync(Guid? stageId, SavePipelineStageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "pipeline_stage");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "pipeline_stage");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var outcome = Validation.OneOf(request.Outcome, "pipeline_stage.outcome", OpportunityOutcomes.All);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        if (request.DefaultProbability is < 0 or > 100)
        {
            return Error.Validation("pipeline_stage.probability_invalid", "A probability is between 0 and 100.").WithWhy(("defaultProbability", request.DefaultProbability));
        }

        if ((outcome.Value == OpportunityOutcomes.Won && request.DefaultProbability != 100) || (outcome.Value == OpportunityOutcomes.Lost && request.DefaultProbability != 0))
        {
            return Error.Validation("pipeline_stage.probability_outcome", "A won stage is 100% likely and a lost stage 0%.").WithWhy(("outcome", outcome.Value), ("defaultProbability", request.DefaultProbability));
        }

        PipelineStage? stage = null;
        if (stageId is { } id)
        {
            stage = await db.PipelineStages.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (stage is null)
            {
                return Error.NotFound("pipeline_stage", id);
            }

            if (stage.IsSystem && (code.Value != stage.Code || outcome.Value != stage.Outcome))
            {
                return Error.Conflict("pipeline_stage.system_locked", "A system stage keeps its code and outcome.");
            }

            if (outcome.Value != stage.Outcome && await db.Opportunities.AnyAsync(o => o.StageId == id, cancellationToken))
            {
                return Error.Conflict("pipeline_stage.outcome_in_use", "Opportunities sit in this stage; its outcome cannot change.");
            }

            if (!request.IsActive && stage.IsActive && await db.Opportunities.AnyAsync(o => o.StageId == id && o.Status == OpportunityOutcomes.Open, cancellationToken))
            {
                return Error.Conflict("pipeline_stage.in_use", "Open opportunities sit in this stage; move them before deactivating it.");
            }
        }

        if (await db.PipelineStages.AnyAsync(s => s.Code == code.Value && s.Id != stageId, cancellationToken))
        {
            return Error.Conflict("pipeline_stage.code_taken", $"A stage with code '{code.Value}' already exists.");
        }

        // The pipeline always keeps an active open, won and lost stage, or opportunities could not start or close.
        var remaining = await db.PipelineStages.AsNoTracking().Where(s => s.IsActive && s.Id != stageId).Select(static s => s.Outcome).ToListAsync(cancellationToken);
        if (request.IsActive)
        {
            remaining.Add(outcome.Value);
        }

        var missing = OpportunityOutcomes.All.FirstOrDefault(o => !remaining.Contains(o));
        if (missing is not null)
        {
            return Error.Conflict("pipeline_stage.outcome_required", "The pipeline keeps at least one active open, won and lost stage.").WithWhy(("missing", missing));
        }

        var isNew = stage is null;
        stage ??= new PipelineStage { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        stage.Code = code.Value;
        stage.Name = name.Value;
        stage.DefaultProbability = request.DefaultProbability;
        stage.Outcome = outcome.Value;
        stage.SortOrder = request.SortOrder ?? (isNew ? (await db.PipelineStages.MaxAsync(static s => (int?)s.SortOrder, cancellationToken) ?? 0) + 10 : stage.SortOrder);
        stage.IsActive = request.IsActive;
        stage.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.PipelineStages.Add(stage);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(stage, (await OpenCountsAsync(cancellationToken)).GetValueOrDefault(stage.Id));
    }

    public async Task<Result<IReadOnlyList<PipelineStageSummary>>> ReorderStagesAsync(ReorderStagesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stages = await db.PipelineStages.ToListAsync(cancellationToken);
        var ids = request.StageIds ?? [];
        if (ids.Count != stages.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => stages.All(s => s.Id != id)))
        {
            return Error.Validation("pipeline_stage.order_incomplete", "The order names every stage exactly once.").WithWhy(("stages", stages.Count), ("given", ids.Count));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var stage = stages.Single(s => s.Id == ids[i]);
            if (stage.SortOrder != (i + 1) * 10)
            {
                stage.SortOrder = (i + 1) * 10;
                stage.UpdatedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result<IReadOnlyList<PipelineStageSummary>>.Success(await ListStagesAsync(cancellationToken));
    }

    private async Task<Dictionary<Guid, int>> OpenCountsAsync(CancellationToken cancellationToken) =>
        await db.Opportunities.AsNoTracking().Where(static o => o.Status == OpportunityOutcomes.Open).GroupBy(static o => o.StageId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);

    internal static PipelineStageSummary Map(PipelineStage s, int open) => new(s.Id, s.Code, s.Name.Values, s.SortOrder, s.DefaultProbability, s.Outcome, s.IsSystem, s.IsActive, open, s.UpdatedAt);
}

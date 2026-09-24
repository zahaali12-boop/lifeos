using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>
/// Opportunities: numbered per company, pursued with a partner (a customer or a prospect), owned by a sales rep, moved
/// through the pipeline's stages with every move kept, won or lost with a reason. The board shows each active stage
/// with its opportunities and totals per currency, weighted by probability.
/// </summary>
public sealed class OpportunityService(
    PartnersDbContext db,
    ICompanyDirectory companies,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    CustomerService customers,
    CrmActivityService activities,
    IClock clock)
{
    public const string EntityType = "opportunity";

    /// <summary>How long won and lost opportunities stay on the board's closing columns.</summary>
    public const int ClosedDaysOnBoard = 90;

    private const int TitleMaxLength = 200;

    public async Task<IReadOnlyList<OpportunitySummary>> ListAsync(Guid? companyId, Guid? partnerId, string? status, Guid? stageId, Guid? salesRepId, bool? mine, string? q, CancellationToken cancellationToken)
    {
        var query = await ScopedAsync(companyId, salesRepId, mine, cancellationToken);
        if (partnerId is { } partner)
        {
            query = query.Where(o => o.PartnerId == partner);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(o => o.Status == s);
        }

        if (stageId is { } stage)
        {
            query = query.Where(o => o.StageId == stage);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            var partnerIds = db.Partners.FromSqlInterpolated($"SELECT * FROM app.ptr_partners WHERE code ILIKE {pattern} OR legal_name_i18n->>'en' ILIKE {pattern} OR legal_name_i18n->>'ar' ILIKE {pattern}").Select(static p => p.Id);
            query = query.Where(o => EF.Functions.ILike(o.Title, pattern) || EF.Functions.ILike(o.Number, pattern) || partnerIds.Contains(o.PartnerId));
        }

        var rows = await query.OrderByDescending(static o => o.UpdatedAt).Take(500).ToListAsync(cancellationToken);
        return await MapAsync(rows, cancellationToken);
    }

    public async Task<OpportunityDetail?> GetAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var opportunity = await (await ScopedAsync(null, null, null, cancellationToken)).SingleOrDefaultAsync(o => o.Id == opportunityId, cancellationToken);
        if (opportunity is null)
        {
            return null;
        }

        var stages = await db.PipelineStages.AsNoTracking().ToDictionaryAsync(static s => s.Id, static s => s.Code, cancellationToken);
        var history = await db.OpportunityStageChanges.AsNoTracking().Where(c => c.OpportunityId == opportunityId).OrderBy(static c => c.ChangedAt).ThenBy(static c => c.Id).ToListAsync(cancellationToken);
        var summary = (await MapAsync([opportunity], cancellationToken))[0];
        return new OpportunityDetail(
            summary,
            history.Select(c => new StageChangeSummary(c.Id, c.FromStageId, c.FromStageId is { } f ? stages.GetValueOrDefault(f) : null, c.ToStageId, stages.GetValueOrDefault(c.ToStageId, string.Empty), c.ProbabilityPct, c.ExpectedAmount, c.ChangedAt, c.ChangedBy)).ToList(),
            await activities.ListAsync(null, opportunityId, null, null, null, cancellationToken));
    }

    public async Task<PipelineBoard> BoardAsync(Guid? companyId, Guid? salesRepId, bool? mine, CancellationToken cancellationToken)
    {
        var stages = await db.PipelineStages.AsNoTracking().Where(static s => s.IsActive).OrderBy(static s => s.SortOrder).ThenBy(static s => s.Code).ToListAsync(cancellationToken);
        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-ClosedDaysOnBoard);
        var rows = await (await ScopedAsync(companyId, salesRepId, mine, cancellationToken))
            .Where(o => o.Status == OpportunityOutcomes.Open || o.ClosedOn >= since)
            .OrderBy(static o => o.ExpectedClose == null).ThenBy(static o => o.ExpectedClose).ThenBy(static o => o.Number)
            .Take(2000)
            .ToListAsync(cancellationToken);
        var mapped = await MapAsync(rows, cancellationToken);
        var open = mapped.Where(static o => o.Status == OpportunityOutcomes.Open).ToList();
        var columns = stages.Select(s =>
        {
            var inStage = mapped.Where(o => o.StageId == s.Id).ToList();
            return new PipelineColumn(SalesSetupService.Map(s, inStage.Count(static o => o.Status == OpportunityOutcomes.Open)), inStage, Totals(inStage));
        }).ToList();
        return new PipelineBoard(columns, Totals(open));
    }

    public async Task<Result<OpportunitySummary>> CreateAsync(CreateOpportunityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null || !customers.MayManageIn(request.CompanyId))
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.PartnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound(PartnerService.EntityType, request.PartnerId);
        }

        if (!partner.IsActive)
        {
            return Error.Conflict("opportunity.partner_inactive", "The partner is inactive.").WithWhy(("partner", partner.Code));
        }

        PipelineStage? stage;
        if (request.StageId is { } stageId)
        {
            stage = await db.PipelineStages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stageId && s.IsActive, cancellationToken);
            if (stage is null)
            {
                return Error.Validation("opportunity.stage_unknown", "The stage does not exist or is inactive.").WithWhy(("stageId", stageId));
            }
        }
        else
        {
            stage = await db.PipelineStages.AsNoTracking().Where(static s => s.IsActive && s.Outcome == OpportunityOutcomes.Open).OrderBy(static s => s.SortOrder).FirstOrDefaultAsync(cancellationToken);
            if (stage is null)
            {
                return Error.Conflict("opportunity.no_open_stage", "The pipeline has no active open stage.");
            }
        }

        if (stage.Outcome != OpportunityOutcomes.Open)
        {
            return Error.Validation("opportunity.stage_not_open", "A new opportunity starts in an open stage.").WithWhy(("stage", stage.Code));
        }

        var account = await db.CustomerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.PartnerId == partner.Id && a.CompanyId == request.CompanyId, cancellationToken);
        var salesRepId = request.SalesRepId ?? account?.SalesRepId;
        if (salesRepId is null)
        {
            var own = await customers.OwnSalesRepIdAsync(cancellationToken);
            salesRepId = own == Guid.Empty ? null : own;
        }

        var opportunity = new Opportunity
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            PartnerId = partner.Id,
            StageId = stage.Id,
            Status = OpportunityOutcomes.Open,
            CreatedBy = principal.Principal?.UserId.Value,
            CreatedAt = clock.UtcNow,
        };
        var applied = await ApplyAsync(opportunity, company, request.Title, request.ExpectedAmount, request.Currency ?? account?.Currency, request.ProbabilityPct ?? stage.DefaultProbability, request.ContactId, salesRepId, request.ExpectedClose, request.Source, request.Notes, request.CustomFields, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(EntityType, company.Id, "OPP", "OPP-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(EntityType, company.Id, null, clock.TodayIn(company.TimeZone), opportunity.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        opportunity.Number = number.Value.Text;
        db.Opportunities.Add(opportunity);
        db.OpportunityStageChanges.Add(new OpportunityStageChange { Id = Guid.CreateVersion7(), OpportunityId = opportunity.Id, ToStageId = stage.Id, ProbabilityPct = opportunity.ProbabilityPct, ExpectedAmount = opportunity.ExpectedAmount, ChangedAt = clock.UtcNow, ChangedBy = principal.Principal?.UserId.Value });
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync([opportunity], cancellationToken))[0];
    }

    public async Task<Result<OpportunitySummary>> UpdateAsync(Guid opportunityId, UpdateOpportunityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (opportunity, company) = await ForChangeAsync(opportunityId, cancellationToken);
        if (opportunity is null || company is null)
        {
            return Error.NotFound(EntityType, opportunityId);
        }

        if (opportunity.Status != OpportunityOutcomes.Open)
        {
            return Error.Conflict("opportunity.closed", "A won or lost opportunity is history; move it back to an open stage to change it.").WithWhy(("number", opportunity.Number), ("status", opportunity.Status));
        }

        var applied = await ApplyAsync(opportunity, company, request.Title, request.ExpectedAmount, request.Currency, request.ProbabilityPct, request.ContactId, request.SalesRepId, request.ExpectedClose, request.Source, request.Notes, request.CustomFields, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync([opportunity], cancellationToken))[0];
    }

    public async Task<Result<OpportunitySummary>> MoveAsync(Guid opportunityId, MoveOpportunityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (opportunity, company) = await ForChangeAsync(opportunityId, cancellationToken);
        if (opportunity is null || company is null)
        {
            return Error.NotFound(EntityType, opportunityId);
        }

        var stage = await db.PipelineStages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.StageId && s.IsActive, cancellationToken);
        if (stage is null)
        {
            return Error.Validation("opportunity.stage_unknown", "The stage does not exist or is inactive.").WithWhy(("stageId", request.StageId));
        }

        var probability = stage.Outcome switch
        {
            OpportunityOutcomes.Won => 100,
            OpportunityOutcomes.Lost => 0,
            _ => request.ProbabilityPct ?? stage.DefaultProbability,
        };
        if (probability is < 0 or > 100)
        {
            return Error.Validation("opportunity.probability_invalid", "A probability is between 0 and 100.").WithWhy(("probabilityPct", probability));
        }

        if (stage.Outcome == OpportunityOutcomes.Lost && string.IsNullOrWhiteSpace(request.LostReason))
        {
            return Error.Validation("opportunity.lost_reason_required", "A lost opportunity says why it was lost.");
        }

        var moved = stage.Id != opportunity.StageId;
        opportunity.StageId = stage.Id;
        opportunity.ProbabilityPct = probability;
        opportunity.Status = stage.Outcome;
        opportunity.LostReason = stage.Outcome == OpportunityOutcomes.Lost ? request.LostReason!.Trim() : null;
        opportunity.ClosedOn = stage.Outcome == OpportunityOutcomes.Open ? null : opportunity.ClosedOn is { } closed && !moved ? closed : clock.TodayIn(company.TimeZone);
        opportunity.UpdatedAt = clock.UtcNow;
        if (moved)
        {
            db.OpportunityStageChanges.Add(new OpportunityStageChange { Id = Guid.CreateVersion7(), OpportunityId = opportunity.Id, FromStageId = await db.OpportunityStageChanges.AsNoTracking().Where(c => c.OpportunityId == opportunity.Id).OrderByDescending(static c => c.ChangedAt).ThenByDescending(static c => c.Id).Select(static c => (Guid?)c.ToStageId).FirstOrDefaultAsync(cancellationToken), ToStageId = stage.Id, ProbabilityPct = probability, ExpectedAmount = opportunity.ExpectedAmount, ChangedAt = clock.UtcNow, ChangedBy = principal.Principal?.UserId.Value });
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync([opportunity], cancellationToken))[0];
    }

    private async Task<(Opportunity? Opportunity, CompanyInfo? Company)> ForChangeAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var opportunity = await db.Opportunities.SingleOrDefaultAsync(o => o.Id == opportunityId, cancellationToken);
        if (opportunity is null || !customers.MayManageIn(opportunity.CompanyId))
        {
            return (null, null);
        }

        return (opportunity, await companies.FindAsync(new CompanyId(opportunity.CompanyId), cancellationToken));
    }

    private async Task<Result> ApplyAsync(Opportunity opportunity, CompanyInfo company, string? title, decimal amount, string? currencyCode, int probability, Guid? contactId, Guid? salesRepId, DateOnly? expectedClose, string? source, string? notes, JsonElement? values, CancellationToken cancellationToken)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > TitleMaxLength)
        {
            return Error.Validation("opportunity.title_invalid", $"A title is 1–{TitleMaxLength} characters.");
        }

        var currency = await companies.FindCurrencyAsync((currencyCode ?? company.FunctionalCurrency.Code).Trim().ToUpperInvariant(), cancellationToken);
        if (currency is null)
        {
            return Error.Validation("opportunity.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", currencyCode));
        }

        if (amount < 0m)
        {
            return Error.Validation("opportunity.amount_invalid", "An expected amount is zero or more.");
        }

        if (decimal.Round(amount, currency.Value.MinorUnits) != amount)
        {
            return Error.Validation("opportunity.amount_precision", "The expected amount has more decimals than its currency.").WithWhy(("amount", amount), ("currency", currency.Value.Code));
        }

        if (probability is < 0 or > 100)
        {
            return Error.Validation("opportunity.probability_invalid", "A probability is between 0 and 100.").WithWhy(("probabilityPct", probability));
        }

        if (contactId is { } contact && !await db.Contacts.AnyAsync(c => c.Id == contact && c.PartnerId == opportunity.PartnerId, cancellationToken))
        {
            return Error.Validation("opportunity.contact_unknown", "The contact is not one of the partner's.").WithWhy(("contactId", contact));
        }

        if (salesRepId is { } repId)
        {
            var rep = await db.SalesReps.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repId, cancellationToken);
            if (rep is null || !rep.IsActive)
            {
                return Error.Validation("opportunity.sales_rep_unknown", "The sales rep does not exist or is inactive.").WithWhy(("salesRepId", repId));
            }

            if (rep.CompanyId is { } repCompany && repCompany != opportunity.CompanyId)
            {
                return Error.Validation("opportunity.sales_rep_other_company", "The sales rep sells for another company.").WithWhy(("salesRep", rep.Code));
            }
        }

        var validated = await customFields.ValidateAsync(EntityType, values ?? (opportunity.Number.Length == 0 ? null : JsonDocument.Parse(opportunity.CustomFields).RootElement), cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        opportunity.Title = trimmed;
        opportunity.ExpectedAmount = amount;
        opportunity.Currency = currency.Value.Code;
        opportunity.ProbabilityPct = probability;
        opportunity.ContactId = contactId;
        opportunity.SalesRepId = salesRepId;
        opportunity.ExpectedClose = expectedClose;
        opportunity.Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        opportunity.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        opportunity.CustomFields = validated.Value;
        opportunity.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Opportunities the member may read: in the companies their customer read permission reaches, filtered as asked.</summary>
    private async Task<IQueryable<Opportunity>> ScopedAsync(Guid? companyId, Guid? salesRepId, bool? mine, CancellationToken cancellationToken)
    {
        var query = db.Opportunities.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(o => o.CompanyId == c);
        }

        if (customers.ReadableCompanies() is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(o => ids.Contains(o.CompanyId));
        }

        if (salesRepId is { } rep)
        {
            query = query.Where(o => o.SalesRepId == rep);
        }

        if (mine == true)
        {
            var own = await customers.OwnSalesRepIdAsync(cancellationToken);
            query = query.Where(o => o.SalesRepId == own);
        }

        return query;
    }

    internal async Task<IReadOnlyList<OpportunitySummary>> MapAsync(IReadOnlyList<Opportunity> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(static o => o.Id).ToList();
        var partnerIds = rows.Select(static o => o.PartnerId).Distinct().ToList();
        var partners = await db.Partners.AsNoTracking().Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(static p => p.Id, cancellationToken);
        var stages = await db.PipelineStages.AsNoTracking().ToDictionaryAsync(static s => s.Id, cancellationToken);
        var reps = await db.SalesReps.AsNoTracking().ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        var since = await db.OpportunityStageChanges.AsNoTracking().Where(c => ids.Contains(c.OpportunityId)).GroupBy(static c => c.OpportunityId).Select(static g => new { g.Key, At = g.Max(static c => c.ChangedAt) }).ToDictionaryAsync(static g => g.Key, static g => g.At, cancellationToken);
        var companyRows = (await companies.ListAsync(cancellationToken)).ToDictionary(static c => c.Id.Value);
        return rows.Select(o =>
        {
            var partner = partners[o.PartnerId];
            var stage = stages[o.StageId];
            var company = companyRows.GetValueOrDefault(o.CompanyId);
            var today = company is null ? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime) : clock.TodayIn(company.TimeZone);
            return new OpportunitySummary(
                o.Id, o.CompanyId, company?.Code ?? string.Empty, o.Number, o.PartnerId, partner.Code, partner.LegalName.Values, o.ContactId, o.Title,
                o.StageId, stage.Code, stage.Name.Values, o.SalesRepId, o.SalesRepId is { } r ? reps.GetValueOrDefault(r) : null,
                o.ExpectedAmount, o.Currency, o.ProbabilityPct, Weighted(o.ExpectedAmount, o.ProbabilityPct), o.ExpectedClose,
                o.Status == OpportunityOutcomes.Open && o.ExpectedClose is { } close && close < today,
                o.Source, o.Status, o.LostReason, o.ClosedOn, o.Notes, JsonDocument.Parse(o.CustomFields).RootElement.Clone(),
                since.GetValueOrDefault(o.Id, o.CreatedAt), o.UpdatedAt);
        }).ToList();
    }

    /// <summary>The expected amount times the probability: an estimate for the forecast, never posted, so kept exact.</summary>
    private static decimal Weighted(decimal amount, int probability) => amount * probability / 100m;

    private static IReadOnlyList<PipelineTotal> Totals(IReadOnlyList<OpportunitySummary> opportunities) =>
        opportunities.GroupBy(static o => o.Currency, StringComparer.Ordinal).OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => new PipelineTotal(g.Key, g.Count(), g.Sum(static o => o.ExpectedAmount), g.Sum(static o => o.WeightedAmount))).ToList();
}

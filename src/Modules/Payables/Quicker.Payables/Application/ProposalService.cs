using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Payables.Domain;
using Quicker.Payables.Persistence;

namespace Quicker.Payables.Application;

/// <summary>
/// Payment proposals (roadmap 4.7): what to pay by a date, in one currency, optionally for one supplier: every unheld
/// invoice item due by then, plus the ones whose early-payment discount is still open, with the discount worked out;
/// lines are deselected or reduced (a partial payment forfeits the discount), the proposal approved, then paid by the
/// banking module, which marks each line with its payment.
/// </summary>
public sealed class ProposalService(PayablesDbContext db, PayablesService payables, ICompanyDirectory companies, INumberAllocator numbering, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    private const string DocumentType = PayablesDocumentTypes.Proposal;

    public async Task<IReadOnlyList<ProposalSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Proposals.AsNoTracking().Include(static p => p.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(p => p.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status == status);
        }

        var proposals = await query.OrderByDescending(static p => p.RunDate).ThenByDescending(static p => p.Number).Take(200).ToListAsync(cancellationToken);
        var result = new List<ProposalSummary>(proposals.Count);
        foreach (var proposal in proposals)
        {
            result.Add(await MapAsync(proposal, cancellationToken));
        }

        return result;
    }

    public async Task<Result<ProposalSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.Proposals.AsNoTracking().Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return proposal is null ? Error.NotFound(DocumentType, id) : await MapAsync(proposal, cancellationToken);
    }

    public async Task<Result<ProposalSummary>> CreateAsync(SaveProposalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("proposal.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3)
        {
            return Error.Validation("proposal.currency_required", "A proposal pays in one currency.").WithWhy(("currency", request.Currency));
        }

        var runDate = request.RunDate ?? clock.TodayIn(company.TimeZone);
        if (request.PayThrough < runDate)
        {
            return Error.Validation("proposal.pay_through_invalid", "The pay-through date is on or after the run date.").WithWhy(("runDate", runDate), ("payThrough", request.PayThrough));
        }

        var query = db.OpenItems.AsNoTracking().Where(i => i.CompanyId == request.CompanyId && i.Currency == currency && !i.PaymentBlocked && i.OriginalTc > 0m && (i.Status == "open" || i.Status == "partially_settled")
            && (i.DueDate <= request.PayThrough || (i.DiscountDate != null && i.DiscountDate >= runDate && i.DiscountDate <= request.PayThrough)));
        if (request.PartnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        var items = await query.OrderBy(static i => i.PartnerId).ThenBy(static i => i.DueDate).ThenBy(static i => i.DocumentNumber).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return Error.Validation("proposal.nothing_due", "Nothing is due for payment by that date in that currency.").WithWhy(("payThrough", request.PayThrough), ("currency", currency));
        }

        var proposal = new PaymentProposal
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            RunDate = runDate,
            PayThrough = request.PayThrough,
            Currency = currency,
            BankAccountId = request.BankAccountId,
            PartnerId = request.PartnerId,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        var minorUnits = await MinorUnitsAsync(currency, company, cancellationToken);
        foreach (var item in items)
        {
            var discount = request.TakeDiscounts && item.DiscountDate is { } d && d >= runDate && item.DiscountPct > 0m ? RoundingPolicy.Default.Round(item.RemainingTc * item.DiscountPct / 100m, minorUnits) : 0m;
            proposal.Lines.Add(new PaymentProposalLine { Id = Guid.CreateVersion7(), TenantId = proposal.TenantId, ProposalId = proposal.Id, OpenItemId = item.Id, PartnerId = item.PartnerId, AmountTc = item.RemainingTc, DiscountTc = discount, Selected = true });
        }

        Total(proposal);
        await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "PP", "PP-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, runDate, proposal.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        proposal.Number = number.Value.Text;
        db.Proposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, proposal.Id, proposal.Number, AuditActions.Created, After: new { proposal.PayThrough, proposal.Currency, lines = proposal.Lines.Count, proposal.TotalTc, proposal.DiscountTc }, CompanyId: proposal.CompanyId), cancellationToken);
        return await MapAsync(proposal, cancellationToken);
    }

    public async Task<Result<ProposalSummary>> UpdateLinesAsync(Guid id, UpdateProposalLinesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = await db.Proposals.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (proposal.Status != "draft")
        {
            return Error.Conflict("proposal.not_draft", "Only a draft proposal is edited.").WithWhy(("status", proposal.Status));
        }

        var itemIds = proposal.Lines.Select(static l => l.OpenItemId).ToList();
        var items = await db.OpenItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(static i => i.Id, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(proposal.CompanyId), cancellationToken);
        var minorUnits = await MinorUnitsAsync(proposal.Currency, company, cancellationToken);
        foreach (var change in request.Lines ?? [])
        {
            var line = proposal.Lines.SingleOrDefault(l => l.Id == change.LineId);
            if (line is null)
            {
                return Error.Validation("proposal.line_unknown", "The line is not on this proposal.").WithWhy(("lineId", change.LineId));
            }

            var item = items[line.OpenItemId];
            line.Selected = change.Selected;
            if (change.AmountTc is { } amount)
            {
                if (amount <= 0m || amount > item.RemainingTc)
                {
                    return Error.Validation("proposal.amount_invalid", "A line pays a positive amount up to what is open on the item.").WithWhy(("lineId", change.LineId), ("amount", amount), ("remaining", item.RemainingTc));
                }

                line.AmountTc = amount;
                // A partial payment forfeits the early-payment discount (ASSUMPTIONS A-113).
                line.DiscountTc = amount == item.RemainingTc && item.DiscountDate is { } d && d >= proposal.RunDate && item.DiscountPct > 0m ? RoundingPolicy.Default.Round(amount * item.DiscountPct / 100m, minorUnits) : 0m;
            }
        }

        Total(proposal);
        proposal.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(proposal, cancellationToken);
    }

    public async Task<Result<ProposalSummary>> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.Proposals.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (proposal.Status != "draft")
        {
            return Error.Conflict("proposal.not_draft", "Only a draft proposal is approved.").WithWhy(("status", proposal.Status));
        }

        if (!proposal.Lines.Any(static l => l.Selected))
        {
            return Error.Validation("proposal.nothing_selected", "Select at least one line to pay.");
        }

        proposal.Status = "approved";
        proposal.ApprovedBy = principal.Principal?.MembershipId.Value;
        proposal.ApprovedAt = clock.UtcNow;
        proposal.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, proposal.Id, proposal.Number, AuditActions.Approved, After: new { proposal.TotalTc, proposal.DiscountTc, selected = proposal.Lines.Count(static l => l.Selected) }, CompanyId: proposal.CompanyId), cancellationToken);
        return await MapAsync(proposal, cancellationToken);
    }

    public async Task<Result<ProposalSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.Proposals.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (proposal.Status is not ("draft" or "approved") || proposal.Lines.Any(static l => l.PaymentId is not null))
        {
            return Error.Conflict("proposal.not_cancellable", "A proposal is cancelled before any of it is paid.").WithWhy(("status", proposal.Status));
        }

        proposal.Status = "cancelled";
        proposal.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, proposal.Id, proposal.Number, AuditActions.StateChanged, After: new { status = proposal.Status }, CompanyId: proposal.CompanyId), cancellationToken);
        return await MapAsync(proposal, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.Proposals.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (proposal.Status != "draft")
        {
            return Error.Conflict("proposal.not_draft", "Only a draft proposal is deleted.").WithWhy(("status", proposal.Status));
        }

        db.Proposals.Remove(proposal);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static void Total(PaymentProposal proposal)
    {
        proposal.TotalTc = proposal.Lines.Where(static l => l.Selected).Sum(static l => l.AmountTc - l.DiscountTc);
        proposal.DiscountTc = proposal.Lines.Where(static l => l.Selected).Sum(static l => l.DiscountTc);
    }

    private async Task<int> MinorUnitsAsync(string currency, CompanyInfo? company, CancellationToken cancellationToken) =>
        company is not null && string.Equals(currency, company.FunctionalCurrency.Code, StringComparison.Ordinal) ? company.FunctionalCurrency.MinorUnits : (await companies.FindCurrencyAsync(currency, cancellationToken))?.MinorUnits ?? 2;

    private async Task<ProposalSummary> MapAsync(PaymentProposal proposal, CancellationToken cancellationToken)
    {
        var itemIds = proposal.Lines.Select(static l => l.OpenItemId).ToList();
        var items = await db.OpenItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(static i => i.Id, cancellationToken);
        var names = await payables.PartnersAsync(proposal.Lines.Select(static l => l.PartnerId), cancellationToken);
        var lines = proposal.Lines.OrderBy(l => names.GetValueOrDefault(l.PartnerId)?.Code, StringComparer.Ordinal).ThenBy(l => items[l.OpenItemId].DueDate).Select(l =>
        {
            var item = items[l.OpenItemId];
            var partner = names.GetValueOrDefault(l.PartnerId);
            return new ProposalLineSummary(l.Id, l.OpenItemId, l.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? PayablesService.Empty(), item.DocumentNumber, item.Kind, item.DueDate, item.DiscountDate, item.DiscountPct, item.RemainingTc, l.AmountTc, l.DiscountTc, l.Selected, l.PaymentId);
        }).ToList();
        return new ProposalSummary(proposal.Id, proposal.CompanyId, proposal.Number, proposal.RunDate, proposal.PayThrough, proposal.Currency, proposal.BankAccountId, proposal.PartnerId, proposal.Status, proposal.TotalTc, proposal.DiscountTc, proposal.Notes, lines, proposal.ApprovedAt, proposal.UpdatedAt);
    }
}

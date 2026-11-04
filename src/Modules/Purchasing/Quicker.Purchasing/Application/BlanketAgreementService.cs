using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>Blanket purchase agreements: agreed quantities and prices with a supplier for a period; purchase order lines release against them and cannot exceed them.</summary>
public sealed class BlanketAgreementService(PurchasingDbContext db, ICompanyDirectory companies, IItemDirectory items, IPartnerDirectory partners, INumberAllocator numbering, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    public const string DocumentType = PurchaseDocumentTypes.BlanketAgreement;

    public async Task<IReadOnlyList<BlanketAgreementSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Agreements.Include(static a => a.Lines).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(a => a.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(a => a.Status == s);
        }

        if (partnerId is { } p)
        {
            query = query.Where(a => a.PartnerId == p);
        }

        var rows = await query.OrderByDescending(static a => a.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<BlanketAgreementSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<BlanketAgreementSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var agreement = await db.Agreements.Include(static a => a.Lines).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return agreement is null ? null : await MapAsync(agreement, cancellationToken);
    }

    public async Task<Result<BlanketAgreementSummary>> CreateAsync(SaveBlanketAgreementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var agreement = new BlanketAgreement { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(agreement, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "BPA-" + company.Code, "BPA-{yyyy}-{seq:5}", "yearly", cancellationToken);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, clock.TodayIn(company.TimeZone), agreement.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        agreement.Number = number.Value.Text;
        db.Agreements.Add(agreement);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(agreement, cancellationToken);
    }

    public async Task<Result<BlanketAgreementSummary>> UpdateAsync(Guid id, SaveBlanketAgreementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agreement = await db.Agreements.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (agreement is null)
        {
            return Error.NotFound("purchase_agreement", id);
        }

        if (agreement.Status != "draft")
        {
            return Error.Conflict("agreement.not_draft", "Only a draft agreement can be edited; an active one is closed and replaced.").WithWhy(("status", agreement.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(agreement.CompanyId), cancellationToken))!;
        var applied = await ApplyAsync(agreement, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(agreement, cancellationToken);
    }

    private async Task<Result> ApplyAsync(BlanketAgreement agreement, SaveBlanketAgreementRequest request, CompanyInfo company, CancellationToken cancellationToken)
    {
        if (request.CompanyId != agreement.CompanyId)
        {
            return Error.Conflict("agreement.company_locked", "An agreement cannot move to another company.");
        }

        var supplier = await partners.EnsureSupplierAsync(company.Id.Value, request.PartnerId, SupplierPurposes.Purchase, cancellationToken);
        if (supplier.IsFailure)
        {
            return supplier.Error!;
        }

        if (request.ValidTo < request.ValidFrom)
        {
            return Error.Validation("agreement.period_invalid", "The agreement ends on or after the day it starts.");
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, supplier.Value.Currency, "agreement", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (request.CommittedAmount < 0m)
        {
            return Error.Validation("agreement.committed_invalid", "The committed amount is zero or more.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("agreement.lines_required", "An agreement has at least one line.");
        }

        var lines = new List<BlanketLine>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            var resolved = await Shared.ResolveLineAsync(items, "agreement", line.ItemId, line.ItemCode, line.AgreedQty, line.Uom, line.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            if (line.AgreedPrice < 0m)
            {
                return Error.Validation("agreement.price_invalid", "An agreed price is zero or more.").WithWhy(("item", resolved.Value.Item.Code));
            }

            lines.Add(new BlanketLine { Id = Guid.CreateVersion7(), AgreementId = agreement.Id, LineNo = ++lineNo, ItemId = resolved.Value.Item.Id, UomId = resolved.Value.Unit.UomId, AgreedQty = line.AgreedQty, AgreedPrice = Shared.Round(line.AgreedPrice, currency.Value) });
        }

        agreement.PartnerId = request.PartnerId;
        agreement.ValidFrom = request.ValidFrom;
        agreement.ValidTo = request.ValidTo;
        agreement.Currency = currency.Value.Code;
        agreement.CommittedAmount = Shared.Round(request.CommittedAmount, currency.Value);
        agreement.Notes = Shared.Trim(request.Notes);
        agreement.Lines.Clear();
        agreement.Lines.AddRange(lines);
        agreement.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    public async Task<Result<BlanketAgreementSummary>> SetStatusAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        var agreement = await db.Agreements.Include(static a => a.Lines).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (agreement is null)
        {
            return Error.NotFound("purchase_agreement", id);
        }

        switch (action)
        {
            case "activate" when agreement.Status == "draft":
                agreement.Status = "active";
                break;
            case "close" when agreement.Status == "active":
                agreement.Status = "closed";
                break;
            case "cancel" when agreement.Status is "draft" or "active":
                if (agreement.Lines.Any(static l => l.ReleasedQty > 0m))
                {
                    return Error.Conflict("agreement.has_releases", "An agreement with releases is closed, not cancelled.");
                }

                agreement.Status = "cancelled";
                break;
            default:
                return Error.Conflict("agreement.transition_invalid", $"'{action}' is not allowed from '{agreement.Status}'.").WithWhy(("status", agreement.Status), ("action", action));
        }

        agreement.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, agreement.Id, agreement.Number, AuditActions.StateChanged, After: new { status = agreement.Status }, CompanyId: agreement.CompanyId), cancellationToken);
        return await MapAsync(agreement, cancellationToken);
    }

    /// <summary>Checks and records a release from an agreement line when an order is approved; a negative quantity gives it back.</summary>
    internal async Task<Result> ReleaseAsync(Guid agreementLineId, Guid partnerId, Guid uomId, decimal quantity, decimal amount, DateOnly orderDate, CancellationToken cancellationToken)
    {
        var line = await db.AgreementLines.SingleOrDefaultAsync(l => l.Id == agreementLineId, cancellationToken);
        var agreement = line is null ? null : await db.Agreements.SingleOrDefaultAsync(a => a.Id == line.AgreementId, cancellationToken);
        if (line is null || agreement is null)
        {
            return Error.Validation("agreement.line_unknown", "The agreement line does not exist.").WithWhy(("blanketLineId", agreementLineId));
        }

        if (quantity > 0m)
        {
            if (agreement.Status != "active" || orderDate < agreement.ValidFrom || orderDate > agreement.ValidTo)
            {
                return Error.Conflict("agreement.not_active", "The agreement is not active on the order date.").WithWhy(("agreement", agreement.Number), ("status", agreement.Status), ("validFrom", agreement.ValidFrom), ("validTo", agreement.ValidTo));
            }

            if (agreement.PartnerId != partnerId)
            {
                return Error.Validation("agreement.other_supplier", "The agreement belongs to another supplier.").WithWhy(("agreement", agreement.Number));
            }

            if (line.UomId != uomId)
            {
                return Error.Validation("agreement.uom_mismatch", "A release is in the agreement line's unit.").WithWhy(("agreement", agreement.Number), ("lineNo", line.LineNo));
            }

            if (line.ReleasedQty + quantity > line.AgreedQty)
            {
                return Error.Conflict("agreement.over_release", "The release exceeds what the agreement still allows.").WithWhy(("agreement", agreement.Number), ("lineNo", line.LineNo), ("agreedQty", line.AgreedQty), ("releasedQty", line.ReleasedQty), ("requested", quantity));
            }

            if (agreement.CommittedAmount > 0m && agreement.ReleasedAmount + amount > agreement.CommittedAmount)
            {
                return Error.Conflict("agreement.over_committed", "The release exceeds the agreement's committed amount.").WithWhy(("agreement", agreement.Number), ("committedAmount", agreement.CommittedAmount), ("releasedAmount", agreement.ReleasedAmount), ("requested", amount));
            }
        }

        line.ReleasedQty = Math.Max(0m, line.ReleasedQty + quantity);
        agreement.ReleasedAmount = Math.Max(0m, agreement.ReleasedAmount + amount);
        agreement.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    internal async Task<BlanketLine?> FindLineAsync(Guid lineId, CancellationToken cancellationToken) => await db.AgreementLines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lineId, cancellationToken);

    private async Task<BlanketAgreementSummary> MapAsync(BlanketAgreement a, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(a.PartnerId, cancellationToken);
        var lines = new List<BlanketLineSummary>(a.Lines.Count);
        foreach (var l in a.Lines.OrderBy(static l => l.LineNo))
        {
            var item = (await items.FindAsync(l.ItemId, cancellationToken))!;
            var unit = (await items.UomsAsync(l.ItemId, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            lines.Add(new BlanketLineSummary(l.Id, l.LineNo, l.ItemId, item.Code, item.Name.Values, l.UomId, unit?.UomCode ?? string.Empty, l.AgreedQty, l.AgreedPrice, l.ReleasedQty, l.AgreedQty - l.ReleasedQty));
        }

        return new BlanketAgreementSummary(a.Id, a.CompanyId, a.Number, a.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), a.ValidFrom, a.ValidTo, a.Currency, a.CommittedAmount, a.ReleasedAmount, a.Status, a.Notes, lines, a.UpdatedAt);
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pricing.Contracts;
using Quicker.Sales.Domain;
using Quicker.Sales.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// Sales quotations (roadmap 5.4a, DOMAIN_MODEL §11): drafted with lines priced by the pricing engine (customer
/// agreements, price lists, promotions) and taxed by the tax engine on the pricing date, sent, accepted or rejected.
/// Conversion to a sales order arrives with 5.4b once the order document exists.
/// </summary>
public sealed class QuotationService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IItemDirectory items,
    ICustomerDirectory customers,
    IPricing pricing,
    ITaxDetermination tax,
    ITaxDirectory taxDirectory,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    IDimensionSets dimensionSets)
{
    public const string DocumentType = "sales_quotation";

    public async Task<IReadOnlyList<QuotationSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Quotations.Include(static q => q.Lines).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(q => q.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(q => q.Status == status);
        }

        if (partnerId is { } p)
        {
            query = query.Where(q => q.PartnerId == p);
        }

        var quotations = await query.OrderByDescending(static q => q.CreatedAt).ToListAsync(cancellationToken);
        return await MapManyAsync(quotations, cancellationToken);
    }

    public async Task<QuotationSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var quotation = await db.Quotations.Include(static q => q.Lines).AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        return quotation is null ? null : (await MapManyAsync([quotation], cancellationToken))[0];
    }

    public async Task<Result<QuotationSummary>> CreateAsync(SaveQuotationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var quotation = new SalesQuotation { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, BranchId = request.BranchId, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(quotation, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "QT-" + company.Code, "QT-{yyyy}-{seq:5}", "yearly", cancellationToken);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, quotation.QuoteDate, quotation.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        quotation.Number = number.Value.Text;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, quotation.Id, quotation.Number, AuditActions.Created, After: new { quotation.Status, quotation.TotalGross, quotation.Currency }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([quotation], cancellationToken))[0];
    }

    public async Task<Result<QuotationSummary>> UpdateAsync(Guid id, SaveQuotationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quotation = await db.Quotations.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (quotation is null)
        {
            return Error.NotFound("quotation", id);
        }

        if (quotation.Status != "draft")
        {
            return Error.Conflict("quotation.not_draft", "Only a draft quotation is edited.").WithWhy(("status", quotation.Status));
        }

        if (quotation.CompanyId != request.CompanyId)
        {
            return Error.Conflict("quotation.company_locked", "A quotation cannot move to another company.");
        }

        var company = (await companies.FindAsync(new CompanyId(quotation.CompanyId), cancellationToken))!;
        var applied = await ApplyAsync(quotation, request, company, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, quotation.Id, quotation.Number, AuditActions.Updated, After: new { quotation.TotalGross, quotation.Currency }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([quotation], cancellationToken))[0];
    }

    public async Task<Result<QuotationSummary>> SendAsync(Guid id, CancellationToken cancellationToken)
    {
        var quotation = await db.Quotations.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (quotation is null)
        {
            return Error.NotFound("quotation", id);
        }

        if (quotation.Status != "draft")
        {
            return Error.Conflict("quotation.not_draft", "Only a draft quotation is sent.").WithWhy(("status", quotation.Status));
        }

        quotation.Status = "sent";
        quotation.SentAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, quotation.Id, quotation.Number, AuditActions.StateChanged, After: new { quotation.Status }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([quotation], cancellationToken))[0];
    }

    public async Task<Result<QuotationSummary>> AcceptAsync(Guid id, CancellationToken cancellationToken)
    {
        var quotation = await db.Quotations.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (quotation is null)
        {
            return Error.NotFound("quotation", id);
        }

        if (quotation.Status != "sent")
        {
            return Error.Conflict("quotation.not_sent", "Only a sent quotation is accepted or rejected.").WithWhy(("status", quotation.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(quotation.CompanyId), cancellationToken))!;
        if (quotation.ValidUntil is { } validUntil && validUntil < clock.TodayIn(company.TimeZone))
        {
            return Error.Conflict("quotation.expired", "The quotation's validity has passed.").WithWhy(("validUntil", validUntil));
        }

        quotation.Status = "accepted";
        quotation.DecidedAt = clock.UtcNow;
        quotation.DecidedBy = principal.Principal?.MembershipId.Value;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, quotation.Id, quotation.Number, AuditActions.StateChanged, After: new { quotation.Status }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([quotation], cancellationToken))[0];
    }

    public async Task<Result<QuotationSummary>> RejectAsync(Guid id, RejectQuotationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quotation = await db.Quotations.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == id, cancellationToken);
        if (quotation is null)
        {
            return Error.NotFound("quotation", id);
        }

        if (quotation.Status != "sent")
        {
            return Error.Conflict("quotation.not_sent", "Only a sent quotation is accepted or rejected.").WithWhy(("status", quotation.Status));
        }

        var reason = Validation.Text(request.Reason);
        if (reason is null)
        {
            return Error.Validation("quotation.reason_required", "A rejection needs a reason.");
        }

        quotation.Status = "rejected";
        quotation.RejectionReason = reason;
        quotation.DecidedAt = clock.UtcNow;
        quotation.DecidedBy = principal.Principal?.MembershipId.Value;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, quotation.Id, quotation.Number, AuditActions.StateChanged, After: new { quotation.Status, reason }, CompanyId: quotation.CompanyId), cancellationToken);
        return (await MapManyAsync([quotation], cancellationToken))[0];
    }

    private async Task<Result> ApplyAsync(SalesQuotation quotation, SaveQuotationRequest request, CompanyInfo company, CancellationToken cancellationToken)
    {
        var customer = await customers.EnsureCustomerAsync(company.Id.Value, request.PartnerId, CustomerPurposes.Quote, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, customer.Value.Currency, "quotation", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var quoteDate = request.QuoteDate ?? clock.TodayIn(company.TimeZone);
        var validity = Validation.Period(quoteDate, request.ValidUntil);
        if (validity.IsFailure)
        {
            return validity.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("quotation.lines_required", "A quotation has at least one line.");
        }

        var validated = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var pricingDate = request.PricingDate ?? quoteDate;
        var pricingLines = new List<PricingLineRequest>();
        var resolvedItems = new List<(Guid ItemId, Guid? VariantId, string? Description, Guid? TaxCodeId)>();
        var dimensionSetIds = new List<Guid?>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            lineNo++;
            var dimensionSet = await Shared.DimensionSetAsync(dimensionSets, line.Dimensions, lineNo, cancellationToken);
            if (dimensionSet.IsFailure)
            {
                return dimensionSet.Error!;
            }

            dimensionSetIds.Add(dimensionSet.Value);
            var resolved = await Shared.ResolveLineAsync(items, "quotation", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!.WithWhy(("lineNo", lineNo));
            }

            var (item, unit, _) = resolved.Value;
            if (line.VariantId is { } variantId)
            {
                var variant = await items.FindVariantAsync(variantId, cancellationToken);
                if (variant is null || variant.ItemId != item.Id)
                {
                    return Error.Validation("quotation.variant_invalid", "The variant must belong to the item.").WithWhy(("lineNo", lineNo));
                }
            }

            if (line.DiscountPct is < 0m or > 100m)
            {
                return Error.Validation("quotation.discount_invalid", "A discount is between 0 and 100 percent.").WithWhy(("lineNo", lineNo));
            }

            var key = lineNo.ToString(CultureInfo.InvariantCulture);
            pricingLines.Add(new PricingLineRequest(key, item.Id, line.VariantId, unit.UomId, line.Quantity, line.UnitPrice, line.DiscountPct > 0m ? line.DiscountPct : null));
            resolvedItems.Add((item.Id, line.VariantId, Shared.Trim(line.Description), line.TaxCodeId));
        }

        var priced = await pricing.PriceAsync(new PricingRequest(company.Id.Value, request.PartnerId, currency.Value.Code, pricingDate, pricingLines, request.PriceListId), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        if (priced.Value.UnpricedLines > 0)
        {
            var problem = priced.Value.Lines.First(static l => l.Problem is not null).Problem!;
            return Error.Validation(problem.Code, problem.Message).WithWhy(("unpricedLines", priced.Value.UnpricedLines));
        }

        var pricedByKey = priced.Value.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        var taxLines = new List<TaxDocumentLine>();
        for (var i = 0; i < resolvedItems.Count; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var p = pricedByKey[key];
            taxLines.Add(new TaxDocumentLine(key, p.NetAmount, resolvedItems[i].ItemId, TaxCodeId: resolvedItems[i].TaxCodeId));
        }

        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(company.Id.Value, TaxDirections.Sales, pricingDate, currency.Value.Code, priced.Value.PricesIncludeTax ?? false, taxLines, request.PartnerId), cancellationToken);
        if (taxed.IsFailure)
        {
            return taxed.Error!;
        }

        var taxedByKey = taxed.Value.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        var taxWhyByKey = taxed.Value.Determinations.ToDictionary(static d => d.Key, StringComparer.Ordinal);

        quotation.PartnerId = request.PartnerId;
        quotation.OpportunityId = request.OpportunityId;
        quotation.Currency = currency.Value.Code;
        quotation.QuoteDate = quoteDate;
        quotation.ValidUntil = request.ValidUntil;
        quotation.PricingDate = pricingDate;
        quotation.PriceListId = request.PriceListId;
        quotation.CustomerSnapshot = JsonSerializer.Serialize(new { code = customer.Value.PartnerCode, name = customer.Value.PartnerName.Values });
        quotation.Notes = Shared.Trim(request.Notes);
        quotation.CustomFields = validated.Value;

        quotation.Lines.Clear();
        var totalNet = 0m;
        var totalTax = 0m;
        for (var i = 0; i < resolvedItems.Count; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var p = pricedByKey[key];
            var t = taxedByKey[key];
            totalNet += p.NetAmount;
            totalTax += t.Tax;
            quotation.Lines.Add(new SalesQuotationLine
            {
                Id = Guid.CreateVersion7(),
                QuotationId = quotation.Id,
                LineNo = i + 1,
                DimensionSetId = dimensionSetIds[i],
                ItemId = p.ItemId,
                VariantId = p.VariantId,
                Description = resolvedItems[i].Description,
                Quantity = p.Quantity,
                UomId = p.UomId,
                QuantityBase = p.BaseQuantity,
                UnitPrice = p.UnitPrice,
                DiscountPct = p.EffectiveDiscountPct,
                DiscountAmount = p.LineDiscountAmount + p.PromotionDiscountAmount + p.DocumentDiscountAmount,
                TaxCodeId = t.TaxCodeId,
                TaxRatePct = t.RatePct,
                TaxReverseCharge = t.IsReverseCharge,
                TaxRecoverable = t.IsRecoverable,
                TaxReason = taxWhyByKey[key].Reason,
                NetAmount = p.NetAmount,
                TaxAmount = t.Tax,
                PromotionId = p.PromotionId,
                PriceBreakdown = JsonSerializer.Serialize(p.Steps),
            });
        }

        quotation.TotalNet = totalNet;
        quotation.TotalTax = totalTax;
        quotation.TotalGross = totalNet + totalTax;
        return Result.Success();
    }

    private async Task<IReadOnlyList<QuotationSummary>> MapManyAsync(IReadOnlyList<SalesQuotation> quotations, CancellationToken cancellationToken)
    {
        if (quotations.Count == 0)
        {
            return [];
        }

        var partners = new Dictionary<Guid, CustomerTermsInfo>();
        foreach (var (partnerId, companyId) in quotations.Select(static q => (q.PartnerId, q.CompanyId)).Distinct())
        {
            if (await customers.FindCustomerAsync(companyId, partnerId, cancellationToken) is { } terms)
            {
                partners[partnerId] = terms;
            }
        }

        var itemIds = quotations.SelectMany(static q => q.Lines).Select(static l => l.ItemId).Distinct().ToList();
        var itemInfos = new Dictionary<Guid, ItemInfo>();
        var uomInfos = new Dictionary<Guid, string>();
        foreach (var itemId in itemIds)
        {
            if (await items.FindAsync(itemId, cancellationToken) is { } info)
            {
                itemInfos[itemId] = info;
                foreach (var u in await items.UomsAsync(itemId, cancellationToken))
                {
                    uomInfos.TryAdd(u.UomId, u.UomCode);
                }
            }
        }

        var taxCodeIds = quotations.SelectMany(static q => q.Lines).Select(static l => l.TaxCodeId).OfType<Guid>().Distinct().ToList();
        var taxCodes = await taxDirectory.DescribeCodesAsync(taxCodeIds, cancellationToken);
        var today = DateOnly.FromDateTime(clock.UtcNow.Date);
        var result = new List<QuotationSummary>();
        foreach (var quotation in quotations)
        {
            var partner = partners.GetValueOrDefault(quotation.PartnerId);
            var lines = new List<QuotationLineSummary>();
            foreach (var line in quotation.Lines.OrderBy(static l => l.LineNo))
            {
                var item = itemInfos.GetValueOrDefault(line.ItemId);
                var taxCode = line.TaxCodeId is { } tcid ? taxCodes.GetValueOrDefault(tcid) : null;
                lines.Add(new QuotationLineSummary(
                    line.Id, line.LineNo, line.ItemId, item?.Code ?? "?", item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    line.VariantId, line.Description, line.Quantity, line.UomId, uomInfos.GetValueOrDefault(line.UomId, "?"), line.QuantityBase,
                    line.UnitPrice, line.DiscountPct, line.NetAmount, line.TaxAmount, line.PromotionId, null,
                    line.TaxCodeId, taxCode?.Code, line.TaxRatePct, line.TaxReverseCharge, line.TaxRecoverable, line.TaxReason,
                    line.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null));
            }

            result.Add(new QuotationSummary(
                quotation.Id, quotation.CompanyId, quotation.Number, quotation.Status, quotation.PartnerId,
                partner?.PartnerCode ?? "?", partner?.PartnerName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                quotation.OpportunityId, quotation.Currency, quotation.QuoteDate, quotation.ValidUntil, quotation.PricingDate, quotation.PriceListId,
                quotation.TotalNet, quotation.TotalTax, quotation.TotalGross, quotation.RejectionReason, quotation.Notes,
                Shared.Parse(quotation.CustomFields), quotation.SentAt, quotation.DecidedAt, quotation.OrderId, lines, quotation.UpdatedAt,
                quotation.Status == "sent" && quotation.ValidUntil is { } vu && vu < today));
        }

        return result;
    }
}

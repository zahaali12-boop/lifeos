using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Domain;
using Quicker.Pricing.Engine;
using Quicker.Pricing.Persistence;

namespace Quicker.Pricing.Application;

/// <summary>
/// Prices a basket: reads the company's rules as of the pricing date, the items, the customer, the rates and the costs
/// the engine needs, and hands them to <see cref="PriceEngine"/>, which does no I/O. Also records the promotions a
/// confirmed document uses, under a lock, against their limits.
/// </summary>
public sealed class PricingService(
    PricingDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    ICustomerDirectory customers,
    IPartnerDirectory partners,
    IItemDirectory items,
    IExchangeRateResolver rates,
    IInventoryCosting costing,
    PricingAccess access,
    IClock clock) : IPricing
{
    public const int MaxLines = 500;

    public async Task<Result<PricingResult>> PriceAsync(PricingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > MaxLines)
        {
            return Error.Validation("pricing.lines_invalid", $"A basket has 1 to {MaxLines} lines.").WithWhy(("lines", request.Lines?.Count ?? 0));
        }

        var duplicate = request.Lines.GroupBy(static l => l.Key, StringComparer.Ordinal).FirstOrDefault(static g => string.IsNullOrWhiteSpace(g.Key) || g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Validation("pricing.line_keys_invalid", "Every line needs its own key.").WithWhy(("key", duplicate.Key));
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var readable = access.Require(company.Id.Value, PricingPermissions.Read);
        if (readable.IsFailure)
        {
            return readable.Error!;
        }

        var manual = request.DocumentDiscountPct is > 0m || request.Lines.Any(static l => l.ManualUnitPrice is not null || l.ManualDiscountPct is > 0m);
        if (manual && !access.MayIn(company.Id.Value, PricingPermissions.PriceOverride))
        {
            return Error.Forbidden("pricing.override_forbidden", "Typing a price or a discount by hand needs the price override permission.");
        }

        var invalid = request.Lines.FirstOrDefault(static l => l.ManualUnitPrice is < 0m || l.ManualDiscountPct is < 0m or > 100m);
        if (invalid is not null || request.DocumentDiscountPct is < 0m or > 100m)
        {
            return Error.Validation("pricing.manual_value_invalid", "A typed price is not negative and a typed discount is between 0 and 100%.").WithWhy(("key", invalid?.Key));
        }

        CustomerTermsInfo? terms = null;
        if (request.PartnerId is { } partnerId)
        {
            terms = await customers.FindCustomerAsync(company.Id.Value, partnerId, cancellationToken);
            if (terms is null && await partners.FindAsync(partnerId, cancellationToken) is null)
            {
                return Error.NotFound("partner", partnerId);
            }
        }

        var currencyCode = (request.Currency ?? terms?.Currency ?? company.FunctionalCurrency.Code).Trim().ToUpperInvariant();
        if (await companies.FindCurrencyAsync(currencyCode, cancellationToken) is not { } currency)
        {
            return Error.Validation("pricing.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", currencyCode));
        }

        var rateType = (request.RateType ?? RateTypes.Spot).Trim().ToLowerInvariant();
        var date = request.PricingDate ?? clock.TodayIn(company.TimeZone);
        var lists = await db.PriceLists.AsNoTracking().Where(l => l.CompanyId == company.Id.Value).ToListAsync(cancellationToken);
        if (request.PriceListId is { } documentList && lists.All(l => l.Id != documentList))
        {
            return Error.Validation("pricing.price_list_not_company", "The price list is not one of the company's.").WithWhy(("priceListId", documentList));
        }

        foreach (var line in request.Lines.Where(static l => l.VariantId is not null))
        {
            var variant = await items.FindVariantAsync(line.VariantId!.Value, cancellationToken);
            if (variant is null || variant.ItemId != line.ItemId)
            {
                return Error.Validation("pricing.variant_not_item", "The variant is not one of the item's.").WithWhy(("key", line.Key), ("variantId", line.VariantId));
            }
        }

        // The promotions in force decide which other items (goods given away) need reading.
        var promotions = await db.Promotions.AsNoTracking().Include(static p => p.Components).Include(static p => p.Tiers)
            .Where(p => p.CompanyId == company.Id.Value && p.IsActive && (p.ValidFrom == null || p.ValidFrom <= date) && (p.ValidTo == null || p.ValidTo >= date))
            .ToListAsync(cancellationToken);
        var itemIds = request.Lines.Select(static l => l.ItemId).Concat(promotions.Where(static p => p.GetItemId is not null).Select(static p => p.GetItemId!.Value)).Distinct().ToList();
        var engineItems = await ReadItemsAsync(itemIds, cancellationToken);
        var categoryIds = engineItems.Values.SelectMany(static i => i.CategoryLineage).Distinct().ToArray();
        var knownItems = engineItems.Keys.ToArray();

        var listIds = lists.Select(static l => l.Id).ToArray();
        var groupId = terms?.CustomerGroupId;
        var assignments = await db.Assignments.AsNoTracking()
            .Where(a => listIds.Contains(a.PriceListId) && ((request.PartnerId != null && a.PartnerId == request.PartnerId) || (groupId != null && a.CustomerGroupId == groupId)))
            .ToListAsync(cancellationToken);
        var listItems = await db.PriceListItems.AsNoTracking().Where(e => listIds.Contains(e.PriceListId) && knownItems.Contains(e.ItemId)).ToListAsync(cancellationToken);
        var agreements = request.PartnerId is { } customer
            ? await db.Agreements.AsNoTracking()
                .Where(a => a.CompanyId == company.Id.Value && a.PartnerId == customer && ((a.ItemId != null && knownItems.Contains(a.ItemId.Value)) || (a.CategoryId != null && categoryIds.Contains(a.CategoryId.Value))))
                .ToListAsync(cancellationToken)
            : [];
        var rules = await db.DiscountRules.AsNoTracking().Where(r => r.CompanyId == company.Id.Value && r.IsActive).ToListAsync(cancellationToken);
        var floors = await db.Floors.AsNoTracking()
            .Where(f => f.CompanyId == company.Id.Value && f.IsActive && ((f.ItemId != null && knownItems.Contains(f.ItemId.Value)) || (f.CategoryId != null && categoryIds.Contains(f.CategoryId.Value))))
            .ToListAsync(cancellationToken);
        var promotionIds = promotions.Select(static p => p.Id).ToArray();
        var usages = await db.PromotionUsages.AsNoTracking()
            .Where(u => promotionIds.Contains(u.PromotionId) && u.ReleasedAt == null)
            .GroupBy(static u => u.PromotionId)
            .Select(g => new { g.Key, Used = g.Count(), ByCustomer = g.Count(u => request.PartnerId != null && u.PartnerId == request.PartnerId) })
            .ToDictionaryAsync(static u => u.Key, cancellationToken);

        var ruleSet = new PricingRuleSet(
            lists.Select(l => new EngineList(l.Id, l.Code, l.Currency, l.PricesIncludeTax, l.ParentListId, l.ParentAdjustmentPct, l.RoundingIncrement, l.RoundingMode, l.PriceSurcharge, l.ValidFrom, l.ValidTo, l.Priority, l.IsDefault, l.IsActive,
                assignments.Where(a => a.PriceListId == l.Id && a.PartnerId is not null).Select(static a => a.PartnerId!.Value).ToList(),
                assignments.Where(a => a.PriceListId == l.Id && a.CustomerGroupId is not null).Select(static a => a.CustomerGroupId!.Value).ToList())).ToList(),
            listItems.Select(static e => new EngineListItem(e.Id, e.PriceListId, e.ItemId, e.VariantId, e.UomId, e.MinQuantity, e.Price, e.ValidFrom, e.ValidTo)).ToList(),
            agreements.Select(static a => new EngineAgreement(a.Id, a.Reference, a.ItemId, a.VariantId, a.CategoryId, a.UomId, a.MinQuantity, a.Price, a.Currency, a.DiscountPct, a.ValidFrom, a.ValidTo, a.IsActive)).ToList(),
            rules.Select(static r => new EngineDiscountRule(r.Id, r.Code, r.Level, r.ItemId, r.CategoryId, r.BrandId, r.PartnerId, r.CustomerGroupId, r.Channel, r.PaymentTermsId, r.MinQuantity, r.MinAmount, r.Weekdays,
                r.ValueType, r.Value, r.Currency, r.Combination, r.Priority, r.ValidFrom, r.ValidTo, r.IsActive)).ToList(),
            promotions.Select(p => new EnginePromotion(p.Id, p.Code, p.Kind, p.CouponCode, p.ItemId, p.CategoryId, p.BrandId, p.PartnerId, p.CustomerGroupId, p.Channel, p.BuyQuantity, p.GetItemId, p.GetQuantity, p.GetDiscountPct,
                p.MaxApplications, p.BundlePrice, p.Currency, p.DiscountPct, p.Combination, p.Priority, p.UsageLimit, p.UsageLimitPerCustomer,
                usages.TryGetValue(p.Id, out var used) ? used.Used : 0, used?.ByCustomer ?? 0, p.ValidFrom, p.ValidTo, p.IsActive,
                p.Components.Select(static c => new EngineComponent(c.ItemId, c.Quantity)).ToList(),
                p.Tiers.Select(static t => new EngineTier(t.MinQuantity, t.DiscountPct)).ToList())).ToList(),
            floors.Select(static f => new EngineFloor(f.Id, f.ItemId, f.CategoryId, f.MinPrice, f.Currency, f.MinMarginPct, f.OnBreach, f.IsActive)).ToList());

        var functional = company.FunctionalCurrency.Code;
        var engineRates = await ReadRatesAsync(company.Id, currency.Code, functional, date, rateType, ruleSet, engineItems.Values, cancellationToken);
        var unitCosts = await ReadCostsAsync(company.Id.Value, date, floors, engineItems.Values, cancellationToken);
        var context = new PricingContext(
            company.Id.Value,
            functional,
            request.PartnerId,
            groupId,
            request.PaymentTermsId ?? terms?.PaymentTermsId,
            string.IsNullOrWhiteSpace(request.Channel) ? null : request.Channel.Trim().ToLowerInvariant(),
            currency,
            date,
            rateType,
            request.PriceListId,
            request.PricesIncludeTax,
            (request.CouponCodes ?? []).Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal),
            request.DocumentDiscountPct,
            new RoundingPolicy(company.RoundingMode));
        var input = new PricingInput(
            context,
            request.Lines.Select(static l => new EngineLine(l.Key, l.ItemId, l.VariantId, l.UomId, l.Quantity, l.ManualUnitPrice, l.ManualDiscountPct)).ToList(),
            engineItems,
            ruleSet,
            engineRates,
            unitCosts);
        return PriceEngine.Price(input);
    }

    public async Task<Result> RecordPromotionUsageAsync(PromotionUsageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DocumentType))
        {
            return Error.Validation("promotion.document_type_required", "The document type is required.");
        }

        var uow = unitOfWork.Current;
        foreach (var promotionId in request.PromotionIds.Distinct().Order())
        {
            // The lock on the promotion row makes two documents confirmed at once take its uses one after the other.
            var locked = await uow.Connection.QuerySingleOrDefaultAsync<LockedPromotion>(new CommandDefinition(
                "SELECT code AS Code, company_id AS CompanyId, usage_limit AS UsageLimit, usage_limit_per_customer AS UsageLimitPerCustomer FROM app.prc_promotions WHERE tenant_id = @tenant AND id = @id FOR UPDATE",
                new { tenant = uow.Context.TenantId.Value, id = promotionId },
                uow.Transaction,
                cancellationToken: cancellationToken));
            if (locked is null || locked.CompanyId != request.CompanyId)
            {
                return Error.NotFound("promotion", promotionId);
            }

            if (await db.PromotionUsages.AnyAsync(u => u.PromotionId == promotionId && u.DocumentType == request.DocumentType && u.DocumentId == request.DocumentId && u.ReleasedAt == null, cancellationToken))
            {
                continue;
            }

            var used = await db.PromotionUsages.CountAsync(u => u.PromotionId == promotionId && u.ReleasedAt == null, cancellationToken);
            var byCustomer = request.PartnerId is { } partner ? await db.PromotionUsages.CountAsync(u => u.PromotionId == promotionId && u.PartnerId == partner && u.ReleasedAt == null, cancellationToken) : 0;
            if ((locked.UsageLimit is { } limit && used >= limit) || (locked.UsageLimitPerCustomer is { } perCustomer && request.PartnerId is not null && byCustomer >= perCustomer))
            {
                return Error.Conflict("promotion.usage_limit_reached", "The promotion has no uses left.").WithWhy(("promotion", locked.Code), ("used", used), ("limit", locked.UsageLimit), ("usedByCustomer", byCustomer), ("limitPerCustomer", locked.UsageLimitPerCustomer));
            }

            var released = await db.PromotionUsages.SingleOrDefaultAsync(u => u.PromotionId == promotionId && u.DocumentType == request.DocumentType && u.DocumentId == request.DocumentId, cancellationToken);
            if (released is not null)
            {
                released.ReleasedAt = null;
                released.UsedAt = clock.UtcNow;
            }
            else
            {
                db.PromotionUsages.Add(new PromotionUsage
                {
                    Id = Guid.CreateVersion7(),
                    PromotionId = promotionId,
                    PartnerId = request.PartnerId,
                    DocumentType = request.DocumentType,
                    DocumentId = request.DocumentId,
                    UsedAt = clock.UtcNow,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    public async Task<Result> ReleasePromotionUsageAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default)
    {
        var usages = await db.PromotionUsages.Where(u => u.DocumentType == documentType && u.DocumentId == documentId && u.ReleasedAt == null).ToListAsync(cancellationToken);
        foreach (var usage in usages)
        {
            usage.ReleasedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Dictionary<Guid, EngineItem>> ReadItemsAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, EngineItem>();
        foreach (var itemId in itemIds)
        {
            var item = await items.FindAsync(itemId, cancellationToken);
            if (item is null)
            {
                continue;
            }

            var uoms = await items.UomsAsync(itemId, cancellationToken);
            var lineage = item.CategoryId is { } category ? (await items.CategoryLineageAsync(category, cancellationToken)).Select(static c => c.Id).ToList() : [];
            result[itemId] = new EngineItem(item.Id, item.Code, item.BaseUomId, item.BasePrecision, item.SalesUomId, lineage, item.BrandId, item.ListPrice, item.ListPriceCurrency,
                uoms.Select(static u => new EngineUom(u.UomId, u.UomCode, u.Numerator, u.Denominator)).ToList(), item.IsActive);
        }

        return result;
    }

    /// <summary>Every rate the engine may need: each currency the rules and items price in to the document's, and each derived list's parent currency to its own.</summary>
    private async Task<Dictionary<string, EngineRate>> ReadRatesAsync(CompanyId companyId, string documentCurrency, string functional, DateOnly date, string rateType, PricingRuleSet ruleSet, IEnumerable<EngineItem> engineItems, CancellationToken cancellationToken)
    {
        var pairs = new HashSet<(string From, string To)>();
        var sources = ruleSet.Lists.Select(static l => l.Currency)
            .Concat(ruleSet.Agreements.Select(static a => a.Currency))
            .Concat(ruleSet.DiscountRules.Select(static r => r.Currency))
            .Concat(ruleSet.Promotions.Select(static p => p.Currency))
            .Concat(ruleSet.Floors.Select(static f => f.Currency))
            .Concat(engineItems.Select(i => i.ListPrice is null ? null : i.ListPriceCurrency ?? functional))
            .Append(functional)
            .OfType<string>();
        foreach (var source in sources)
        {
            pairs.Add((source, documentCurrency));
        }

        var byId = ruleSet.Lists.ToDictionary(static l => l.Id);
        foreach (var list in ruleSet.Lists.Where(static l => l.ParentId is not null))
        {
            if (byId.TryGetValue(list.ParentId!.Value, out var parent))
            {
                pairs.Add((parent.Currency, list.Currency));
            }
        }

        var result = new Dictionary<string, EngineRate>(StringComparer.Ordinal);
        foreach (var (from, to) in pairs.Where(static p => !string.Equals(p.From, p.To, StringComparison.Ordinal)))
        {
            var resolved = await rates.ResolveAsync(companyId, from, to, date, rateType, cancellationToken);
            if (resolved.IsSuccess)
            {
                result[$"{from}>{to}"] = new EngineRate(resolved.Value.Rate.Rate, resolved.Value.Method, resolved.Value.EffectiveFrom);
            }
        }

        return result;
    }

    /// <summary>The expected unit cost of each item a margin floor governs, per base unit in the company's currency; items with no cost yet are left out.</summary>
    private async Task<Dictionary<Guid, decimal>> ReadCostsAsync(Guid companyId, DateOnly date, IReadOnlyList<PriceFloor> floors, IEnumerable<EngineItem> engineItems, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, decimal>();
        var marginFloors = floors.Where(static f => f.MinMarginPct is not null).ToList();
        if (marginFloors.Count == 0)
        {
            return result;
        }

        foreach (var item in engineItems.Where(i => marginFloors.Any(f => f.ItemId == i.Id || (f.CategoryId is { } c && i.CategoryLineage.Contains(c)))))
        {
            var cost = await costing.CostAsync(companyId, item.Id, null, date, cancellationToken);
            if (cost is not null && (cost.ExpectedUnitCost > 0m || cost.Quantity > 0m))
            {
                result[item.Id] = cost.ExpectedUnitCost;
            }
        }

        return result;
    }

    private sealed record LockedPromotion(string Code, Guid CompanyId, int? UsageLimit, int? UsageLimitPerCustomer);
}

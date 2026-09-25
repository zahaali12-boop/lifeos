using Microsoft.EntityFrameworkCore;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Domain;
using Quicker.Pricing.Persistence;

namespace Quicker.Pricing.Application;

/// <summary>
/// The rules around the lists: prices and discounts agreed with a customer, discount rules, promotions and floors.
/// Each belongs to one company and is changed only by those who may manage it there.
/// </summary>
public sealed class PricingRulesService(PricingDbContext db, ICompanyDirectory companies, IItemDirectory items, IPartnerDirectory partners, PricingRefs refs, PricingAccess access, IClock clock)
{
    // ------------------------------------------------------------------ agreements

    public async Task<IReadOnlyList<PriceAgreementSummary>> ListAgreementsAsync(Guid? companyId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = Scoped(db.Agreements.AsNoTracking(), static a => a.CompanyId, companyId);
        if (partnerId is { } partner)
        {
            query = query.Where(a => a.PartnerId == partner);
        }

        return await MapAgreementsAsync(await query.ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result<PriceAgreementSummary>> SaveAgreementAsync(Guid? id, SavePriceAgreementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, PricingPermissions.AgreementManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var company = await CompanyAsync(request.CompanyId, "price_agreement", cancellationToken);
        if (company.IsFailure)
        {
            return company.Error!;
        }

        var partner = await partners.FindAsync(request.PartnerId, cancellationToken);
        if (partner is null || !partner.IsCustomer)
        {
            return Error.Validation("price_agreement.customer_unknown", "An agreement is made with a customer.").WithWhy(("partnerId", request.PartnerId));
        }

        if ((request.ItemId is null) == (request.CategoryId is null))
        {
            return Error.Validation("price_agreement.scope_invalid", "An agreement is for one item or one category.");
        }

        if ((request.Price is null) == (request.DiscountPct is null))
        {
            return Error.Validation("price_agreement.value_invalid", "An agreement gives either a price or a discount.");
        }

        if (request.Price is { } price && (price < 0m || request.ItemId is null))
        {
            return Error.Validation("price_agreement.price_invalid", "An agreed price is zero or more, for one item.");
        }

        var pct = Validation.Percentage(request.DiscountPct, "price_agreement.discount_pct");
        if (pct.IsFailure)
        {
            return pct.Error!;
        }

        if (request.MinQuantity < 0m)
        {
            return Error.Validation("price_agreement.min_quantity_invalid", "A quantity break is zero or more.");
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "price_agreement");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        Guid? uomId = null;
        string? currency = null;
        if (request.ItemId is { } itemId)
        {
            var item = await items.FindAsync(itemId, cancellationToken);
            if (item is null)
            {
                return Error.Validation("price_agreement.item_unknown", "The item does not exist.").WithWhy(("itemId", itemId));
            }

            if (request.VariantId is { } variantId && (await items.FindVariantAsync(variantId, cancellationToken))?.ItemId != itemId)
            {
                return Error.Validation("price_agreement.variant_not_item", "The variant is not one of the item's.").WithWhy(("variantId", variantId));
            }

            if (request.Price is not null)
            {
                uomId = request.UomId ?? item.SalesUomId ?? item.BaseUomId;
                if ((await items.UomsAsync(itemId, cancellationToken)).All(u => u.UomId != uomId))
                {
                    return Error.Validation("price_agreement.uom_not_item_unit", "The unit is not one of the item's units.").WithWhy(("uomId", uomId));
                }

                currency = Validation.Currency(request.Currency) ?? company.Value.FunctionalCurrency.Code;
                if (await companies.FindCurrencyAsync(currency, cancellationToken) is null)
                {
                    return Error.Validation("price_agreement.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", request.Currency));
                }
            }
        }
        else if (request.VariantId is not null)
        {
            return Error.Validation("price_agreement.variant_without_item", "A variant is named with its item.");
        }
        else if ((await items.DescribeAsync([request.CategoryId!.Value], cancellationToken)).GetValueOrDefault(request.CategoryId.Value)?.Kind != "category")
        {
            return Error.Validation("price_agreement.category_unknown", "The category does not exist.").WithWhy(("categoryId", request.CategoryId));
        }

        PriceAgreement? agreement = null;
        if (id is { } existingId)
        {
            agreement = await db.Agreements.SingleOrDefaultAsync(a => a.Id == existingId, cancellationToken);
            if (agreement is null || !access.MayIn(agreement.CompanyId, PricingPermissions.Read))
            {
                return Error.NotFound("price_agreement", existingId);
            }

            if (agreement.CompanyId != request.CompanyId)
            {
                return Error.Validation("price_agreement.company_fixed", "An agreement stays in the company it was made in.");
            }
        }

        if (await db.Agreements.AnyAsync(a => a.CompanyId == request.CompanyId && a.PartnerId == request.PartnerId && a.ItemId == request.ItemId && a.VariantId == request.VariantId && a.CategoryId == request.CategoryId
            && a.UomId == uomId && a.MinQuantity == request.MinQuantity && a.ValidFrom == request.ValidFrom && a.Id != id, cancellationToken))
        {
            return Error.Conflict("price_agreement.duplicate", "The customer already has an agreement for this, from that quantity and date.");
        }

        var isNew = agreement is null;
        agreement ??= new PriceAgreement { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        agreement.PartnerId = request.PartnerId;
        agreement.Reference = Validation.Text(request.Reference);
        agreement.ItemId = request.ItemId;
        agreement.VariantId = request.VariantId;
        agreement.CategoryId = request.CategoryId;
        agreement.UomId = uomId;
        agreement.MinQuantity = request.MinQuantity;
        agreement.Price = request.Price;
        agreement.Currency = currency;
        agreement.DiscountPct = request.DiscountPct;
        agreement.ValidFrom = request.ValidFrom;
        agreement.ValidTo = request.ValidTo;
        agreement.IsActive = request.IsActive;
        agreement.Notes = Validation.Text(request.Notes);
        agreement.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Agreements.Add(agreement);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapAgreementsAsync([agreement], cancellationToken))[0];
    }

    public async Task<Result> DeleteAgreementAsync(Guid id, CancellationToken cancellationToken)
    {
        var agreement = await db.Agreements.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (agreement is null || !access.MayIn(agreement.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_agreement", id);
        }

        var allowed = access.Require(agreement.CompanyId, PricingPermissions.AgreementManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.Agreements.Remove(agreement);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ discount rules

    public async Task<IReadOnlyList<DiscountRuleSummary>> ListRulesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var rules = await Scoped(db.DiscountRules.AsNoTracking(), static r => r.CompanyId, companyId).OrderBy(static r => r.Level).ThenBy(static r => r.Priority).ThenBy(static r => r.Code).ToListAsync(cancellationToken);
        return await MapRulesAsync(rules, cancellationToken);
    }

    public async Task<Result<DiscountRuleSummary>> SaveRuleAsync(Guid? id, SaveDiscountRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, PricingPermissions.PromotionManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var company = await CompanyAsync(request.CompanyId, "discount_rule", cancellationToken);
        if (company.IsFailure)
        {
            return company.Error!;
        }

        var code = Validation.Code(request.Code, "discount_rule");
        var name = Validation.Name(request.Name, "discount_rule");
        var level = Validation.OneOf(request.Level, "discount_rule.level", DiscountLevels.All);
        var valueType = Validation.OneOf(request.ValueType, "discount_rule.value_type", DiscountValueTypes.All);
        var combination = Validation.OneOf(request.Combination, "discount_rule.combination", Combinations.All, Combinations.Exclusive);
        foreach (var check in new[] { code.Error, name.Error, level.Error, valueType.Error, combination.Error })
        {
            if (check is not null)
            {
                return check;
            }
        }

        if (request.Value < 0m || (valueType.Value == DiscountValueTypes.Percentage && (request.Value <= 0m || request.Value > 100m)))
        {
            return Error.Validation("discount_rule.value_invalid", "A percentage is above 0 and at most 100; an amount or a price is zero or more.").WithWhy(("value", request.Value));
        }

        if (level.Value == DiscountLevels.Document && (valueType.Value == DiscountValueTypes.FixedPrice || request.ItemId is not null || request.CategoryId is not null || request.BrandId is not null || request.MinQuantity is not null))
        {
            return Error.Validation("discount_rule.document_scope_invalid", "A document discount is a percentage or an amount, for customers, groups, channels or terms; items and quantities belong to line discounts.");
        }

        var needsCurrency = valueType.Value != DiscountValueTypes.Percentage || request.MinAmount is not null;
        var currency = needsCurrency ? Validation.Currency(request.Currency) ?? company.Value.FunctionalCurrency.Code : null;
        if (currency is not null && await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("discount_rule.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", request.Currency));
        }

        if (request.MinQuantity is <= 0m || request.MinAmount is <= 0m)
        {
            return Error.Validation("discount_rule.condition_invalid", "A minimum quantity or amount is above zero.");
        }

        var weekdays = request.Weekdays is { Count: > 0 } days ? days.Distinct().Order().ToArray() : null;
        if (weekdays is not null && weekdays.Any(static d => d is < 0 or > 6))
        {
            return Error.Validation("discount_rule.weekdays_invalid", "Weekdays are 0 (Sunday) to 6 (Saturday).");
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "discount_rule");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var scope = await CheckScopeAsync("discount_rule", request.ItemId, request.CategoryId, request.BrandId, request.PartnerId, request.CustomerGroupId, request.PaymentTermsId, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        DiscountRule? rule = null;
        if (id is { } existingId)
        {
            rule = await db.DiscountRules.SingleOrDefaultAsync(r => r.Id == existingId, cancellationToken);
            if (rule is null || !access.MayIn(rule.CompanyId, PricingPermissions.Read))
            {
                return Error.NotFound("discount_rule", existingId);
            }

            if (rule.CompanyId != request.CompanyId)
            {
                return Error.Validation("discount_rule.company_fixed", "A rule stays in the company it was made in.");
            }
        }

        if (await db.DiscountRules.AnyAsync(r => r.CompanyId == request.CompanyId && r.Code == code.Value && r.Id != id, cancellationToken))
        {
            return Error.Conflict("discount_rule.code_taken", $"The company already has a discount rule '{code.Value}'.");
        }

        var isNew = rule is null;
        rule ??= new DiscountRule { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        rule.Code = code.Value;
        rule.Name = name.Value;
        rule.Level = level.Value;
        rule.ItemId = request.ItemId;
        rule.CategoryId = request.CategoryId;
        rule.BrandId = request.BrandId;
        rule.PartnerId = request.PartnerId;
        rule.CustomerGroupId = request.CustomerGroupId;
        rule.Channel = Validation.Text(request.Channel)?.ToLowerInvariant();
        rule.PaymentTermsId = request.PaymentTermsId;
        rule.MinQuantity = request.MinQuantity;
        rule.MinAmount = request.MinAmount;
        rule.Weekdays = weekdays;
        rule.ValueType = valueType.Value;
        rule.Value = request.Value;
        rule.Currency = currency;
        rule.Combination = combination.Value;
        rule.Priority = Math.Max(0, request.Priority);
        rule.ValidFrom = request.ValidFrom;
        rule.ValidTo = request.ValidTo;
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.DiscountRules.Add(rule);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapRulesAsync([rule], cancellationToken))[0];
    }

    public async Task<Result> DeleteRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.DiscountRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null || !access.MayIn(rule.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("discount_rule", id);
        }

        var allowed = access.Require(rule.CompanyId, PricingPermissions.PromotionManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.DiscountRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ promotions

    public async Task<IReadOnlyList<PromotionSummary>> ListPromotionsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var promotions = await Scoped(db.Promotions.AsNoTracking().Include(static p => p.Components).Include(static p => p.Tiers), static p => p.CompanyId, companyId)
            .OrderBy(static p => p.Code).ToListAsync(cancellationToken);
        return await MapPromotionsAsync(promotions, cancellationToken);
    }

    public async Task<Result<PromotionSummary>> SavePromotionAsync(Guid? id, SavePromotionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, PricingPermissions.PromotionManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var company = await CompanyAsync(request.CompanyId, "promotion", cancellationToken);
        if (company.IsFailure)
        {
            return company.Error!;
        }

        var code = Validation.Code(request.Code, "promotion");
        var name = Validation.Name(request.Name, "promotion");
        var kind = Validation.OneOf(request.Kind, "promotion.kind", PromotionKinds.All);
        var combination = Validation.OneOf(request.Combination, "promotion.combination", Combinations.All, Combinations.Exclusive);
        foreach (var check in new[] { code.Error, name.Error, kind.Error, combination.Error })
        {
            if (check is not null)
            {
                return check;
            }
        }

        var coupon = Validation.Text(request.CouponCode)?.ToUpperInvariant();
        var components = (request.Components ?? []).Where(static c => c.Quantity > 0m).GroupBy(static c => c.ItemId).Select(static g => new PromotionComponentDto(g.Key, g.Sum(static c => c.Quantity))).ToList();
        var tiers = (request.Tiers ?? []).GroupBy(static t => t.MinQuantity).Select(static g => g.Last()).OrderBy(static t => t.MinQuantity).ToList();
        var anyScope = request.ItemId is not null || request.CategoryId is not null || request.BrandId is not null;
        Error? shape = kind.Value switch
        {
            PromotionKinds.BuyXGetY when request.BuyQuantity is not > 0m || request.GetQuantity is not > 0m || !anyScope || (request.GetItemId is null && request.ItemId is null)
                => Error.Validation("promotion.buy_x_get_y_invalid", "Buy X get Y needs what is bought (an item, a category or a brand), how many, and how many of which item are given; the same item needs the bought item named."),
            PromotionKinds.Bundle when components.Count < 2 || request.BundlePrice is not >= 0m
                => Error.Validation("promotion.bundle_invalid", "A bundle has at least two components and a price for the set."),
            PromotionKinds.VolumeTier when tiers.Count == 0 || !anyScope || tiers.Any(static t => t.MinQuantity <= 0m || t.DiscountPct is <= 0m or > 100m)
                => Error.Validation("promotion.volume_tier_invalid", "A volume promotion has a scope (an item, a category or a brand) and tiers from a quantity above zero, each with a discount above 0 and at most 100%."),
            PromotionKinds.Coupon when coupon is null || request.DiscountPct is not (> 0m and <= 100m)
                => Error.Validation("promotion.coupon_invalid", "A coupon has its code and a discount above 0 and at most 100%."),
            _ => null,
        };
        if (shape is not null)
        {
            return shape;
        }

        var getPct = kind.Value == PromotionKinds.BuyXGetY ? request.GetDiscountPct ?? 100m : (decimal?)null;
        if (getPct is <= 0m or > 100m)
        {
            return Error.Validation("promotion.get_discount_invalid", "What is given is discounted above 0 and at most 100% (100% is free).");
        }

        if (request.UsageLimit is <= 0 || request.UsageLimitPerCustomer is <= 0 || request.MaxApplications is <= 0)
        {
            return Error.Validation("promotion.limit_invalid", "A limit is one or more.");
        }

        var currency = kind.Value == PromotionKinds.Bundle ? Validation.Currency(request.Currency) ?? company.Value.FunctionalCurrency.Code : null;
        if (currency is not null && await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("promotion.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", request.Currency));
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "promotion");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var scope = await CheckScopeAsync("promotion", request.ItemId, request.CategoryId, request.BrandId, request.PartnerId, request.CustomerGroupId, null, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var itemsNamed = components.Select(static c => (Guid?)c.ItemId).Append(request.GetItemId).OfType<Guid>().ToList();
        if (itemsNamed.Count > 0)
        {
            var known = await items.DescribeAsync(itemsNamed, cancellationToken);
            var unknown = itemsNamed.FirstOrDefault(i => known.GetValueOrDefault(i)?.Kind != "item");
            if (unknown != Guid.Empty)
            {
                return Error.Validation("promotion.item_unknown", "An item of the promotion does not exist.").WithWhy(("itemId", unknown));
            }
        }

        Promotion? promotion = null;
        if (id is { } existingId)
        {
            promotion = await db.Promotions.Include(static p => p.Components).Include(static p => p.Tiers).SingleOrDefaultAsync(p => p.Id == existingId, cancellationToken);
            if (promotion is null || !access.MayIn(promotion.CompanyId, PricingPermissions.Read))
            {
                return Error.NotFound("promotion", existingId);
            }

            if (promotion.CompanyId != request.CompanyId)
            {
                return Error.Validation("promotion.company_fixed", "A promotion stays in the company it was made in.");
            }
        }

        if (await db.Promotions.AnyAsync(p => p.CompanyId == request.CompanyId && p.Code == code.Value && p.Id != id, cancellationToken))
        {
            return Error.Conflict("promotion.code_taken", $"The company already has a promotion '{code.Value}'.");
        }

        if (coupon is not null && await db.Promotions.AnyAsync(p => p.CompanyId == request.CompanyId && p.CouponCode == coupon && p.Id != id, cancellationToken))
        {
            return Error.Conflict("promotion.coupon_taken", $"Another promotion already uses the coupon '{coupon}'.");
        }

        var isNew = promotion is null;
        promotion ??= new Promotion { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        var buyXGetY = kind.Value == PromotionKinds.BuyXGetY;
        promotion.Code = code.Value;
        promotion.Name = name.Value;
        promotion.Kind = kind.Value;
        promotion.CouponCode = coupon;
        promotion.ItemId = request.ItemId;
        promotion.CategoryId = request.CategoryId;
        promotion.BrandId = request.BrandId;
        promotion.PartnerId = request.PartnerId;
        promotion.CustomerGroupId = request.CustomerGroupId;
        promotion.Channel = Validation.Text(request.Channel)?.ToLowerInvariant();
        promotion.BuyQuantity = buyXGetY ? request.BuyQuantity : null;
        promotion.GetItemId = buyXGetY ? request.GetItemId : null;
        promotion.GetQuantity = buyXGetY ? request.GetQuantity : null;
        promotion.GetDiscountPct = getPct;
        promotion.MaxApplications = buyXGetY ? request.MaxApplications : null;
        promotion.BundlePrice = kind.Value == PromotionKinds.Bundle ? request.BundlePrice : null;
        promotion.Currency = currency;
        promotion.DiscountPct = kind.Value == PromotionKinds.Coupon ? request.DiscountPct : null;
        promotion.Combination = combination.Value;
        promotion.Priority = Math.Max(0, request.Priority);
        promotion.UsageLimit = request.UsageLimit;
        promotion.UsageLimitPerCustomer = request.UsageLimitPerCustomer;
        promotion.ValidFrom = request.ValidFrom;
        promotion.ValidTo = request.ValidTo;
        promotion.IsActive = request.IsActive;
        promotion.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Promotions.Add(promotion);
        }
        else
        {
            // The components and tiers are replaced as a whole; the old rows go first so a kept key is inserted again.
            promotion.Components.Clear();
            promotion.Tiers.Clear();
            await db.SaveChangesAsync(cancellationToken);
        }

        if (kind.Value == PromotionKinds.Bundle)
        {
            promotion.Components.AddRange(components.Select(c => new PromotionComponent { PromotionId = promotion.Id, ItemId = c.ItemId, Quantity = c.Quantity }));
        }

        if (kind.Value == PromotionKinds.VolumeTier)
        {
            promotion.Tiers.AddRange(tiers.Select(t => new PromotionTier { PromotionId = promotion.Id, MinQuantity = t.MinQuantity, DiscountPct = t.DiscountPct }));
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapPromotionsAsync([promotion], cancellationToken))[0];
    }

    public async Task<Result> DeletePromotionAsync(Guid id, CancellationToken cancellationToken)
    {
        var promotion = await db.Promotions.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null || !access.MayIn(promotion.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("promotion", id);
        }

        var allowed = access.Require(promotion.CompanyId, PricingPermissions.PromotionManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (await db.PromotionUsages.AnyAsync(u => u.PromotionId == id, cancellationToken))
        {
            return Error.Conflict("promotion.used", "Documents have used the promotion; deactivate it instead so their history keeps it.");
        }

        db.Promotions.Remove(promotion);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ floors

    public async Task<IReadOnlyList<PriceFloorSummary>> ListFloorsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var floors = await Scoped(db.Floors.AsNoTracking(), static f => f.CompanyId, companyId).ToListAsync(cancellationToken);
        return await MapFloorsAsync(floors, cancellationToken);
    }

    public async Task<Result<PriceFloorSummary>> SaveFloorAsync(Guid? id, SavePriceFloorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, PricingPermissions.FloorManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var company = await CompanyAsync(request.CompanyId, "price_floor", cancellationToken);
        if (company.IsFailure)
        {
            return company.Error!;
        }

        if ((request.ItemId is null) == (request.CategoryId is null))
        {
            return Error.Validation("price_floor.scope_invalid", "A floor is for one item or one category.");
        }

        if (request.MinPrice is null && request.MinMarginPct is null)
        {
            return Error.Validation("price_floor.value_required", "A floor sets a minimum price, a minimum margin or both.");
        }

        if (request.MinPrice is < 0m || request.MinMarginPct is <= -100m or >= 100m)
        {
            return Error.Validation("price_floor.value_invalid", "A minimum price is zero or more; a minimum margin is above -100% and below 100%.");
        }

        var onBreach = Validation.OneOf(request.OnBreach, "price_floor.on_breach", FloorBreachActions.All, FloorBreachActions.Block);
        if (onBreach.IsFailure)
        {
            return onBreach.Error!;
        }

        var currency = request.MinPrice is null ? null : Validation.Currency(request.Currency) ?? company.Value.FunctionalCurrency.Code;
        if (currency is not null && await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("price_floor.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", request.Currency));
        }

        var scope = await CheckScopeAsync("price_floor", request.ItemId, request.CategoryId, null, null, null, null, cancellationToken);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        PriceFloor? floor = null;
        if (id is { } existingId)
        {
            floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == existingId, cancellationToken);
            if (floor is null || !access.MayIn(floor.CompanyId, PricingPermissions.Read))
            {
                return Error.NotFound("price_floor", existingId);
            }

            if (floor.CompanyId != request.CompanyId)
            {
                return Error.Validation("price_floor.company_fixed", "A floor stays in the company it was made in.");
            }
        }

        if (await db.Floors.AnyAsync(f => f.CompanyId == request.CompanyId && f.ItemId == request.ItemId && f.CategoryId == request.CategoryId && f.Id != id, cancellationToken))
        {
            return Error.Conflict("price_floor.duplicate", "There is already a floor for this item or category.");
        }

        var isNew = floor is null;
        floor ??= new PriceFloor { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        floor.ItemId = request.ItemId;
        floor.CategoryId = request.CategoryId;
        floor.MinPrice = request.MinPrice;
        floor.Currency = currency;
        floor.MinMarginPct = request.MinMarginPct;
        floor.OnBreach = onBreach.Value;
        floor.IsActive = request.IsActive;
        floor.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Floors.Add(floor);
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapFloorsAsync([floor], cancellationToken))[0];
    }

    public async Task<Result> DeleteFloorAsync(Guid id, CancellationToken cancellationToken)
    {
        var floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (floor is null || !access.MayIn(floor.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_floor", id);
        }

        var allowed = access.Require(floor.CompanyId, PricingPermissions.FloorManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.Floors.Remove(floor);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    private IQueryable<T> Scoped<T>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, Guid>> companyOf, Guid? companyId)
    {
        if (access.CompaniesFor(PricingPermissions.Read) is { } readable)
        {
            var ids = readable.ToArray();
            var parameter = companyOf.Parameters[0];
            var contains = System.Linq.Expressions.Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], System.Linq.Expressions.Expression.Constant(ids), companyOf.Body);
            query = query.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(contains, parameter));
        }

        if (companyId is { } company)
        {
            var parameter = companyOf.Parameters[0];
            var equals = System.Linq.Expressions.Expression.Equal(companyOf.Body, System.Linq.Expressions.Expression.Constant(company));
            query = query.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(equals, parameter));
        }

        return query;
    }

    private async Task<Result<CompanyInfo>> CompanyAsync(Guid companyId, string field, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        return company is null ? Error.Validation($"{field}.company_unknown", "The company does not exist.").WithWhy(("companyId", companyId)) : company;
    }

    /// <summary>Every scope a rule names exists and is what it claims to be.</summary>
    private async Task<Result> CheckScopeAsync(string field, Guid? itemId, Guid? categoryId, Guid? brandId, Guid? partnerId, Guid? groupId, Guid? termsId, CancellationToken cancellationToken)
    {
        var catalog = await items.DescribeAsync(new[] { itemId, categoryId, brandId }.OfType<Guid>().ToList(), cancellationToken);
        foreach (var (id, kind) in new[] { (itemId, "item"), (categoryId, "category"), (brandId, "brand") })
        {
            if (id is { } key && catalog.GetValueOrDefault(key)?.Kind != kind)
            {
                return Error.Validation($"{field}.{kind}_unknown", $"The {kind} does not exist.").WithWhy(($"{kind}Id", (object?)key));
            }
        }

        var partnerSide = await partners.DescribeAsync(new[] { partnerId, groupId, termsId }.OfType<Guid>().ToList(), cancellationToken);
        foreach (var (id, kind) in new[] { (partnerId, "partner"), (groupId, "customer_group"), (termsId, "payment_terms") })
        {
            if (id is { } key && partnerSide.GetValueOrDefault(key)?.Kind != kind)
            {
                return Error.Validation($"{field}.{kind}_unknown", $"The {kind.Replace('_', ' ')} does not exist.").WithWhy(("id", (object?)key));
            }
        }

        return Result.Success();
    }

    private async Task<IReadOnlyList<PriceAgreementSummary>> MapAgreementsAsync(IReadOnlyList<PriceAgreement> agreements, CancellationToken cancellationToken)
    {
        var book = await refs.ReadAsync(agreements.SelectMany(static a => new[] { a.ItemId, a.VariantId, a.CategoryId }), agreements.Select(static a => (Guid?)a.PartnerId), cancellationToken);
        var uomCodes = new Dictionary<Guid, string>();
        foreach (var itemId in agreements.Where(static a => a.UomId is not null).Select(static a => a.ItemId!.Value).Distinct())
        {
            foreach (var uom in await items.UomsAsync(itemId, cancellationToken))
            {
                uomCodes[uom.UomId] = uom.UomCode;
            }
        }

        return agreements
            .Select(a => new PriceAgreementSummary(
                a.Id, a.CompanyId, book.Partner(a.PartnerId) ?? new RecordRef(a.PartnerId, string.Empty, new Dictionary<string, string>(StringComparer.Ordinal)), a.Reference,
                book.Catalog(a.ItemId), a.VariantId, book.Catalog(a.VariantId)?.Code, book.Catalog(a.CategoryId), a.UomId, a.UomId is { } u ? uomCodes.GetValueOrDefault(u) : null,
                a.MinQuantity, a.Price, a.Currency, a.DiscountPct, a.ValidFrom, a.ValidTo, a.IsActive, a.Notes, a.UpdatedAt))
            .OrderBy(static a => a.Partner.Code, StringComparer.Ordinal)
            .ThenBy(static a => a.Item?.Code ?? a.Category?.Code, StringComparer.Ordinal)
            .ThenBy(static a => a.MinQuantity)
            .ToList();
    }

    private async Task<IReadOnlyList<DiscountRuleSummary>> MapRulesAsync(IReadOnlyList<DiscountRule> rules, CancellationToken cancellationToken)
    {
        var book = await refs.ReadAsync(rules.SelectMany(static r => new[] { r.ItemId, r.CategoryId, r.BrandId }), rules.SelectMany(static r => new[] { r.PartnerId, r.CustomerGroupId, r.PaymentTermsId }), cancellationToken);
        return rules.Select(r => new DiscountRuleSummary(
            r.Id, r.CompanyId, r.Code, r.Name.Values, r.Level, r.ValueType, r.Value, r.Currency, r.Combination, r.Priority,
            book.Catalog(r.ItemId), book.Catalog(r.CategoryId), book.Catalog(r.BrandId), book.Partner(r.PartnerId), book.Partner(r.CustomerGroupId), r.Channel, book.Partner(r.PaymentTermsId),
            r.MinQuantity, r.MinAmount, r.Weekdays, r.ValidFrom, r.ValidTo, r.IsActive, r.UpdatedAt)).ToList();
    }

    private async Task<IReadOnlyList<PromotionSummary>> MapPromotionsAsync(IReadOnlyList<Promotion> promotions, CancellationToken cancellationToken)
    {
        var ids = promotions.Select(static p => p.Id).ToArray();
        var used = await db.PromotionUsages.AsNoTracking().Where(u => ids.Contains(u.PromotionId) && u.ReleasedAt == null).GroupBy(static u => u.PromotionId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var book = await refs.ReadAsync(
            promotions.SelectMany(static p => new[] { p.ItemId, p.CategoryId, p.BrandId, p.GetItemId }.Concat(p.Components.Select(static c => (Guid?)c.ItemId))),
            promotions.SelectMany(static p => new[] { p.PartnerId, p.CustomerGroupId }),
            cancellationToken);
        return promotions.Select(p => new PromotionSummary(
            p.Id, p.CompanyId, p.Code, p.Name.Values, p.Kind, p.CouponCode, book.Catalog(p.ItemId), book.Catalog(p.CategoryId), book.Catalog(p.BrandId), book.Partner(p.PartnerId), book.Partner(p.CustomerGroupId), p.Channel,
            p.BuyQuantity, book.Catalog(p.GetItemId), p.GetQuantity, p.GetDiscountPct, p.MaxApplications, p.BundlePrice, p.Currency, p.DiscountPct, p.Combination, p.Priority,
            p.UsageLimit, p.UsageLimitPerCustomer, used.GetValueOrDefault(p.Id), p.ValidFrom, p.ValidTo, p.IsActive,
            p.Components.Select(c => new PromotionComponentSummary(c.ItemId, book.Catalog(c.ItemId)?.Code ?? string.Empty, book.Catalog(c.ItemId)?.Name ?? new Dictionary<string, string>(StringComparer.Ordinal), c.Quantity))
                .OrderBy(static c => c.ItemCode, StringComparer.Ordinal).ToList(),
            p.Tiers.OrderBy(static t => t.MinQuantity).Select(static t => new PromotionTierDto(t.MinQuantity, t.DiscountPct)).ToList(),
            p.UpdatedAt)).ToList();
    }

    private async Task<IReadOnlyList<PriceFloorSummary>> MapFloorsAsync(IReadOnlyList<PriceFloor> floors, CancellationToken cancellationToken)
    {
        var book = await refs.ReadAsync(floors.SelectMany(static f => new[] { f.ItemId, f.CategoryId }), [], cancellationToken);
        return floors
            .Select(f => new PriceFloorSummary(f.Id, f.CompanyId, book.Catalog(f.ItemId), book.Catalog(f.CategoryId), f.MinPrice, f.Currency, f.MinMarginPct, f.OnBreach, f.IsActive, f.UpdatedAt))
            .OrderBy(static f => f.Item is null ? 1 : 0)
            .ThenBy(static f => f.Item?.Code ?? f.Category?.Code, StringComparer.Ordinal)
            .ToList();
    }
}

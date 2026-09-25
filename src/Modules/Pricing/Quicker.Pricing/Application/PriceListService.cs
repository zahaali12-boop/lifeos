using Microsoft.EntityFrameworkCore;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pricing.Contracts;
using Quicker.Pricing.Domain;
using Quicker.Pricing.Engine;
using Quicker.Pricing.Persistence;

namespace Quicker.Pricing.Application;

/// <summary>
/// Price lists as configuration: a company's prices in one currency (own entries with quantity breaks and validity, or
/// derived from a parent list by a percentage and a rounding rule), who they are for, and a yearly increase in one go.
/// </summary>
public sealed class PriceListService(PricingDbContext db, ICompanyDirectory companies, IItemDirectory items, IPartnerDirectory partners, PricingRefs refs, PricingAccess access, IClock clock)
{
    private const int MaxDerivationDepth = 5;

    public async Task<IReadOnlyList<PriceListSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.PriceLists.AsNoTracking();
        if (access.CompaniesFor(PricingPermissions.Read) is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(l => ids.Contains(l.CompanyId));
        }

        if (companyId is { } company)
        {
            query = query.Where(l => l.CompanyId == company);
        }

        var lists = await query.OrderBy(static l => l.Code).ToListAsync(cancellationToken);
        return await MapAsync(lists, cancellationToken);
    }

    public async Task<Result<PriceListSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", id);
        }

        return (await MapAsync([list], cancellationToken))[0];
    }

    public async Task<Result<PriceListSummary>> SaveAsync(Guid? id, SavePriceListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = access.Require(request.CompanyId, PricingPermissions.PriceListManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var code = Validation.Code(request.Code, "price_list");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "price_list");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var roundingMode = Validation.OneOf(request.RoundingMode, "price_list.rounding_mode", PriceRoundingModes.All, PriceRoundingModes.Nearest);
        if (roundingMode.IsFailure)
        {
            return roundingMode.Error!;
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "price_list");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        if (await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken) is null)
        {
            return Error.Validation("price_list.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var currency = Validation.Currency(request.Currency);
        if (currency is null || await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("price_list.currency_unknown", "The currency is not an ISO 4217 code.").WithWhy(("currency", request.Currency));
        }

        if (request.Priority < 0)
        {
            return Error.Validation("price_list.priority_invalid", "The priority is zero or more; lower numbers are looked at first.");
        }

        PriceList? list = null;
        if (id is { } existingId)
        {
            list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == existingId, cancellationToken);
            if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
            {
                return Error.NotFound("price_list", existingId);
            }

            if (list.CompanyId != request.CompanyId)
            {
                return Error.Validation("price_list.company_fixed", "A price list stays in the company it was made for.");
            }
        }

        if (await db.PriceLists.AnyAsync(l => l.CompanyId == request.CompanyId && l.Code == code.Value && l.Id != id, cancellationToken))
        {
            return Error.Conflict("price_list.code_taken", $"The company already has a price list '{code.Value}'.");
        }

        if (request.ParentListId is { } parentId)
        {
            var derivation = await CheckParentAsync(id, request.CompanyId, parentId, cancellationToken);
            if (derivation.IsFailure)
            {
                return derivation.Error!;
            }

            if (request.ParentAdjustmentPct is not { } adjustment || adjustment <= -100m || adjustment > 1000m)
            {
                return Error.Validation("price_list.adjustment_invalid", "A derived list changes its parent's prices by a percentage above -100 and at most 1000.").WithWhy(("parentAdjustmentPct", request.ParentAdjustmentPct));
            }

            if (request.RoundingIncrement is <= 0m)
            {
                return Error.Validation("price_list.rounding_increment_invalid", "A rounding increment is above zero.");
            }
        }
        else if (request.ParentAdjustmentPct is not null || request.RoundingIncrement is not null || request.PriceSurcharge != 0m)
        {
            return Error.Validation("price_list.derivation_without_parent", "An adjustment, a rounding rule and a surcharge apply only to a list derived from a parent.");
        }

        var assigned = await CheckAssignmentsAsync(request.PartnerIds ?? [], request.CustomerGroupIds ?? [], cancellationToken);
        if (assigned.IsFailure)
        {
            return assigned.Error!;
        }

        var isNew = list is null;
        list ??= new PriceList { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        if (request.IsDefault && !list.IsDefault)
        {
            // One default per company: making a list the default hands the role over from the previous one.
            foreach (var previous in await db.PriceLists.Where(l => l.CompanyId == request.CompanyId && l.IsDefault && l.Id != list.Id).ToListAsync(cancellationToken))
            {
                previous.IsDefault = false;
                previous.UpdatedAt = clock.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        list.Code = code.Value;
        list.Name = name.Value;
        list.Currency = currency;
        list.PricesIncludeTax = request.PricesIncludeTax;
        list.ParentListId = request.ParentListId;
        list.ParentAdjustmentPct = request.ParentListId is null ? null : request.ParentAdjustmentPct;
        list.RoundingIncrement = request.ParentListId is null ? null : request.RoundingIncrement;
        list.RoundingMode = roundingMode.Value;
        list.PriceSurcharge = request.ParentListId is null ? 0m : request.PriceSurcharge;
        list.ValidFrom = request.ValidFrom;
        list.ValidTo = request.ValidTo;
        list.Priority = request.Priority;
        list.IsDefault = request.IsDefault;
        list.IsActive = request.IsActive;
        list.Notes = Validation.Text(request.Notes);
        list.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.PriceLists.Add(list);
        }

        await db.SaveChangesAsync(cancellationToken);
        var existing = await db.Assignments.Where(a => a.PriceListId == list.Id).ToListAsync(cancellationToken);
        var wantedPartners = (request.PartnerIds ?? []).Distinct().ToHashSet();
        var wantedGroups = (request.CustomerGroupIds ?? []).Distinct().ToHashSet();
        db.Assignments.RemoveRange(existing.Where(a => (a.PartnerId is { } p && !wantedPartners.Contains(p)) || (a.CustomerGroupId is { } g && !wantedGroups.Contains(g))));
        foreach (var partnerId in wantedPartners.Where(p => existing.All(a => a.PartnerId != p)))
        {
            db.Assignments.Add(new PriceListAssignment { Id = Guid.CreateVersion7(), PriceListId = list.Id, PartnerId = partnerId, CreatedAt = clock.UtcNow });
        }

        foreach (var groupId in wantedGroups.Where(g => existing.All(a => a.CustomerGroupId != g)))
        {
            db.Assignments.Add(new PriceListAssignment { Id = Guid.CreateVersion7(), PriceListId = list.Id, CustomerGroupId = groupId, CreatedAt = clock.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync([list], cancellationToken))[0];
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", id);
        }

        var allowed = access.Require(list.CompanyId, PricingPermissions.PriceListManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var children = await db.PriceLists.Where(l => l.ParentListId == id).Select(static l => l.Code).ToListAsync(cancellationToken);
        if (children.Count > 0)
        {
            return Error.Conflict("price_list.has_derived_lists", "Other lists are derived from this one; change or delete them first.").WithWhy(("derivedLists", string.Join(", ", children)));
        }

        db.PriceListItems.RemoveRange(await db.PriceListItems.Where(e => e.PriceListId == id).ToListAsync(cancellationToken));
        db.Assignments.RemoveRange(await db.Assignments.Where(a => a.PriceListId == id).ToListAsync(cancellationToken));
        db.PriceLists.Remove(list);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ entries

    public async Task<Result<IReadOnlyList<PriceListItemSummary>>> ListItemsAsync(Guid listId, string? q, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.AsNoTracking().SingleOrDefaultAsync(l => l.Id == listId, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", listId);
        }

        var entries = await db.PriceListItems.AsNoTracking().Where(e => e.PriceListId == listId).ToListAsync(cancellationToken);
        var mapped = await MapItemsAsync(entries, cancellationToken);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            mapped = mapped.Where(e => e.ItemCode.Contains(term, StringComparison.OrdinalIgnoreCase) || e.ItemName.Values.Any(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        return Result<IReadOnlyList<PriceListItemSummary>>.Success(mapped);
    }

    public async Task<Result<PriceListItemSummary>> SaveItemAsync(Guid listId, Guid? entryId, SavePriceListItemRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == listId, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", listId);
        }

        var allowed = access.Require(list.CompanyId, PricingPermissions.PriceListManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var item = await items.FindAsync(request.ItemId, cancellationToken);
        if (item is null)
        {
            return Error.Validation("price_list_item.item_unknown", "The item does not exist.").WithWhy(("itemId", request.ItemId));
        }

        if (request.VariantId is { } variantId && (await items.FindVariantAsync(variantId, cancellationToken))?.ItemId != item.Id)
        {
            return Error.Validation("price_list_item.variant_not_item", "The variant is not one of the item's.").WithWhy(("variantId", variantId));
        }

        var uomId = request.UomId ?? item.SalesUomId ?? item.BaseUomId;
        var uoms = await items.UomsAsync(item.Id, cancellationToken);
        if (uoms.All(u => u.UomId != uomId))
        {
            return Error.Validation("price_list_item.uom_not_item_unit", "The unit is not one of the item's units.").WithWhy(("item", item.Code), ("uomId", uomId));
        }

        if (request.MinQuantity < 0m)
        {
            return Error.Validation("price_list_item.min_quantity_invalid", "A quantity break is zero or more.");
        }

        if (request.Price < 0m)
        {
            return Error.Validation("price_list_item.price_invalid", "A price is zero or more.");
        }

        var period = Validation.Period(request.ValidFrom, request.ValidTo, "price_list_item");
        if (period.IsFailure)
        {
            return period.Error!;
        }

        PriceListItem? entry = null;
        if (entryId is { } existingId)
        {
            entry = await db.PriceListItems.SingleOrDefaultAsync(e => e.Id == existingId && e.PriceListId == listId, cancellationToken);
            if (entry is null)
            {
                return Error.NotFound("price_list_item", existingId);
            }
        }

        if (await db.PriceListItems.AnyAsync(e => e.PriceListId == listId && e.ItemId == item.Id && e.VariantId == request.VariantId && e.UomId == uomId && e.MinQuantity == request.MinQuantity && e.ValidFrom == request.ValidFrom && e.Id != entryId, cancellationToken))
        {
            return Error.Conflict("price_list_item.duplicate", "The list already prices this item, unit and quantity break from that date.").WithWhy(("item", item.Code), ("minQuantity", request.MinQuantity), ("validFrom", request.ValidFrom));
        }

        var isNew = entry is null;
        entry ??= new PriceListItem { Id = Guid.CreateVersion7(), PriceListId = listId, CreatedAt = clock.UtcNow };
        entry.ItemId = item.Id;
        entry.VariantId = request.VariantId;
        entry.UomId = uomId;
        entry.MinQuantity = request.MinQuantity;
        entry.Price = request.Price;
        entry.ValidFrom = request.ValidFrom;
        entry.ValidTo = request.ValidTo;
        entry.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.PriceListItems.Add(entry);
        }

        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await MapItemsAsync([entry], cancellationToken))[0];
    }

    public async Task<Result> DeleteItemAsync(Guid listId, Guid entryId, CancellationToken cancellationToken)
    {
        var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == listId, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", listId);
        }

        var allowed = access.Require(list.CompanyId, PricingPermissions.PriceListManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var entry = await db.PriceListItems.SingleOrDefaultAsync(e => e.Id == entryId && e.PriceListId == listId, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound("price_list_item", entryId);
        }

        db.PriceListItems.Remove(entry);
        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Changes every price of the list by a percentage, rounded to an increment (or the currency's price decimals).</summary>
    public async Task<Result<AdjustPriceListResult>> AdjustAsync(Guid listId, AdjustPriceListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = await db.PriceLists.SingleOrDefaultAsync(l => l.Id == listId, cancellationToken);
        if (list is null || !access.MayIn(list.CompanyId, PricingPermissions.Read))
        {
            return Error.NotFound("price_list", listId);
        }

        var allowed = access.Require(list.CompanyId, PricingPermissions.PriceListManage);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (request.Pct <= -100m || request.Pct > 1000m || request.Pct == 0m)
        {
            return Error.Validation("price_list.adjustment_invalid", "An adjustment is a percentage above -100 and at most 1000, other than zero.").WithWhy(("pct", request.Pct));
        }

        if (request.RoundingIncrement is <= 0m)
        {
            return Error.Validation("price_list.rounding_increment_invalid", "A rounding increment is above zero.");
        }

        var mode = Validation.OneOf(request.RoundingMode, "price_list.rounding_mode", PriceRoundingModes.All, PriceRoundingModes.Nearest);
        if (mode.IsFailure)
        {
            return mode.Error!;
        }

        var company = await companies.FindAsync(new CompanyId(list.CompanyId), cancellationToken);
        var currency = await companies.FindCurrencyAsync(list.Currency, cancellationToken);
        var decimals = currency is { } known ? PriceEngine.PriceDecimals(known) : 6;
        var rounding = new RoundingPolicy(company?.RoundingMode ?? RoundingMode.HalfAwayFromZero);
        var entries = await db.PriceListItems.Where(e => e.PriceListId == listId).ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            var raised = entry.Price * (1m + (request.Pct / 100m));
            entry.Price = Math.Max(0m, request.RoundingIncrement is { } increment
                ? rounding.RoundToMultiple(raised, increment, PriceEngine.Direction(mode.Value))
                : rounding.Round(raised, decimals));
            entry.UpdatedAt = clock.UtcNow;
        }

        list.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new AdjustPriceListResult(entries.Count);
    }

    private async Task<Result> CheckParentAsync(Guid? id, Guid companyId, Guid parentId, CancellationToken cancellationToken)
    {
        var lists = await db.PriceLists.AsNoTracking().Where(l => l.CompanyId == companyId).ToDictionaryAsync(static l => l.Id, cancellationToken);
        if (!lists.TryGetValue(parentId, out var parent))
        {
            return Error.Validation("price_list.parent_not_company", "A list derives from another list of the same company.").WithWhy(("parentListId", parentId));
        }

        var depth = 1;
        for (var current = parent; current is not null; current = current.ParentListId is { } next ? lists.GetValueOrDefault(next) : null)
        {
            if (current.Id == id)
            {
                return Error.Validation("price_list.derivation_cycle", "A list cannot derive, directly or through others, from itself.").WithWhy(("parent", parent.Code));
            }

            if (++depth > MaxDerivationDepth)
            {
                return Error.Validation("price_list.derivation_too_deep", $"A list derives through at most {MaxDerivationDepth} levels.").WithWhy(("parent", parent.Code));
            }
        }

        return Result.Success();
    }

    private async Task<Result> CheckAssignmentsAsync(IReadOnlyList<Guid> partnerIds, IReadOnlyList<Guid> groupIds, CancellationToken cancellationToken)
    {
        var known = await partners.DescribeAsync(partnerIds.Concat(groupIds).ToList(), cancellationToken);
        var unknownPartner = partnerIds.FirstOrDefault(p => !known.TryGetValue(p, out var r) || r.Kind != "partner");
        if (unknownPartner != Guid.Empty)
        {
            return Error.Validation("price_list.customer_unknown", "A customer the list is for does not exist.").WithWhy(("partnerId", unknownPartner));
        }

        var unknownGroup = groupIds.FirstOrDefault(g => !known.TryGetValue(g, out var r) || r.Kind != "customer_group");
        return unknownGroup != Guid.Empty
            ? Error.Validation("price_list.customer_group_unknown", "A customer group the list is for does not exist.").WithWhy(("customerGroupId", unknownGroup))
            : Result.Success();
    }

    private async Task<IReadOnlyList<PriceListSummary>> MapAsync(IReadOnlyList<PriceList> lists, CancellationToken cancellationToken)
    {
        var ids = lists.Select(static l => l.Id).ToArray();
        var counts = await db.PriceListItems.AsNoTracking().Where(e => ids.Contains(e.PriceListId)).GroupBy(static e => e.PriceListId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        var assignments = await db.Assignments.AsNoTracking().Where(a => ids.Contains(a.PriceListId)).ToListAsync(cancellationToken);
        var parentIds = lists.Where(static l => l.ParentListId is not null).Select(static l => l.ParentListId!.Value).ToArray();
        var parents = await db.PriceLists.AsNoTracking().Where(l => parentIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, static l => l.Code, cancellationToken);
        var book = await refs.ReadAsync([], assignments.Select(static a => a.PartnerId).Concat(assignments.Select(static a => a.CustomerGroupId)), cancellationToken);
        return lists.Select(l => new PriceListSummary(
            l.Id, l.CompanyId, l.Code, l.Name.Values, l.Currency, l.PricesIncludeTax, l.ParentListId, l.ParentListId is { } p ? parents.GetValueOrDefault(p) : null,
            l.ParentAdjustmentPct, l.RoundingIncrement, l.RoundingMode, l.PriceSurcharge, l.ValidFrom, l.ValidTo, l.Priority, l.IsDefault, l.IsActive, l.Notes,
            counts.GetValueOrDefault(l.Id),
            assignments.Where(a => a.PriceListId == l.Id).Select(a => book.Partner(a.PartnerId)).OfType<RecordRef>().OrderBy(static r => r.Code, StringComparer.Ordinal).ToList(),
            assignments.Where(a => a.PriceListId == l.Id).Select(a => book.Partner(a.CustomerGroupId)).OfType<RecordRef>().OrderBy(static r => r.Code, StringComparer.Ordinal).ToList(),
            l.UpdatedAt)).ToList();
    }

    private async Task<IReadOnlyList<PriceListItemSummary>> MapItemsAsync(IReadOnlyList<PriceListItem> entries, CancellationToken cancellationToken)
    {
        var book = await items.DescribeAsync(entries.Select(static e => e.ItemId).Concat(entries.Where(static e => e.VariantId is not null).Select(static e => e.VariantId!.Value)).Distinct().ToList(), cancellationToken);
        var uomCodes = new Dictionary<Guid, string>();
        foreach (var itemId in entries.Select(static e => e.ItemId).Distinct())
        {
            foreach (var uom in await items.UomsAsync(itemId, cancellationToken))
            {
                uomCodes[uom.UomId] = uom.UomCode;
            }
        }

        return entries
            .Select(e => new PriceListItemSummary(
                e.Id, e.PriceListId, e.ItemId, book.TryGetValue(e.ItemId, out var item) ? item.Code : string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
                e.VariantId, e.VariantId is { } v && book.TryGetValue(v, out var variant) ? variant.Code : null,
                e.UomId, uomCodes.GetValueOrDefault(e.UomId) ?? string.Empty, e.MinQuantity, e.Price, e.ValidFrom, e.ValidTo, e.UpdatedAt))
            .OrderBy(static e => e.ItemCode, StringComparer.Ordinal)
            .ThenBy(static e => e.VariantSku, StringComparer.Ordinal)
            .ThenBy(static e => e.UomCode, StringComparer.Ordinal)
            .ThenBy(static e => e.MinQuantity)
            .ThenBy(static e => e.ValidFrom ?? DateOnly.MinValue)
            .ToList();
    }
}

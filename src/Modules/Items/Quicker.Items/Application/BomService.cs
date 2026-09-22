using Microsoft.EntityFrameworkCore;
using Quicker.Items.Contracts;
using Quicker.Items.Domain;
using Quicker.Items.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Items.Application;

/// <summary>
/// Bills of material: a kit (components issued at shipment, never built) or an assembly (built into stock, M3.4),
/// versioned per item with one active version, lines in any unit of the component converted exactly to its base
/// unit, no cycles through nested bills, and a multi-level explosion that shows every requirement with its route.
/// </summary>
public sealed class BomService(ItemsDbContext db, IUomDirectory uoms, IClock clock)
{
    private IReadOnlyDictionary<Guid, UomInfo>? _uomsById;

    public async Task<Result<IReadOnlyList<BomSummary>>> ListAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, cancellationToken))
        {
            return Error.NotFound("item", itemId);
        }

        var boms = await db.Boms.Include(static b => b.Lines).Where(b => b.ItemId == itemId).OrderByDescending(static b => b.Version).ToListAsync(cancellationToken);
        var result = new List<BomSummary>();
        foreach (var bom in boms)
        {
            result.Add(await MapAsync(bom, cancellationToken));
        }

        return result;
    }

    public async Task<BomSummary?> GetAsync(Guid bomId, CancellationToken cancellationToken)
    {
        var bom = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.Id == bomId, cancellationToken);
        return bom is null ? null : await MapAsync(bom, cancellationToken);
    }

    /// <summary>A new version for the item (the previous active version retires when <see cref="SaveBomRequest.Activate"/> is set).</summary>
    public async Task<Result<BomSummary>> CreateAsync(Guid itemId, SaveBomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound("item", itemId);
        }

        var validated = await ValidateAsync(item, null, request, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var now = clock.UtcNow;
        var version = (await db.Boms.Where(b => b.ItemId == itemId).MaxAsync(static b => (int?)b.Version, cancellationToken) ?? 0) + 1;
        var bom = new Bom { Id = Guid.CreateVersion7(), ItemId = itemId, Kind = validated.Value.Kind, Version = version, OutputQty = request.OutputQty, IsActive = false, CreatedAt = now, UpdatedAt = now };
        bom.Lines.AddRange(validated.Value.Lines);
        db.Boms.Add(bom);
        if (request.Activate)
        {
            await ActivateAsync(bom, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(bom, cancellationToken);
    }

    public async Task<Result<BomSummary>> UpdateAsync(Guid bomId, SaveBomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bom = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.Id == bomId, cancellationToken);
        if (bom is null)
        {
            return Error.NotFound("bom", bomId);
        }

        var item = await db.Items.SingleAsync(i => i.Id == bom.ItemId, cancellationToken);
        var validated = await ValidateAsync(item, bom, request, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        bom.Kind = validated.Value.Kind;
        bom.OutputQty = request.OutputQty;
        bom.Lines.Clear();
        bom.Lines.AddRange(validated.Value.Lines);
        bom.UpdatedAt = clock.UtcNow;
        if (request.Activate && !bom.IsActive)
        {
            await ActivateAsync(bom, cancellationToken);
        }
        else if (!request.Activate)
        {
            bom.IsActive = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(bom, cancellationToken);
    }

    public async Task<Result<BomSummary>> ActivateAsync(Guid bomId, CancellationToken cancellationToken)
    {
        var bom = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.Id == bomId, cancellationToken);
        if (bom is null)
        {
            return Error.NotFound("bom", bomId);
        }

        await ActivateAsync(bom, cancellationToken);
        bom.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(bom, cancellationToken);
    }

    /// <summary>
    /// Every component needed for <paramref name="quantity"/> of the item, through nested bills: quantity in the
    /// component's base unit, exact, plus the quantity with scrap allowance; a component appears once per route.
    /// </summary>
    public async Task<Result<BomExplosion>> ExplodeAsync(Guid bomId, decimal quantity, CancellationToken cancellationToken)
    {
        var bom = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.Id == bomId, cancellationToken);
        if (bom is null)
        {
            return Error.NotFound("bom", bomId);
        }

        if (quantity <= 0m)
        {
            return Error.Validation("bom.quantity_invalid", "The quantity to explode must be positive.");
        }

        var item = await db.Items.SingleAsync(i => i.Id == bom.ItemId, cancellationToken);
        var requirements = new List<BomRequirement>();
        var walked = await WalkAsync(bom, quantity, 1m, 0, [item.Code], requirements, cancellationToken);
        return walked.IsFailure ? walked.Error! : new BomExplosion(bom.Id, item.Id, item.Code, quantity, requirements);
    }

    private async Task<Result> WalkAsync(Bom bom, decimal outputQuantity, decimal scrapMultiplier, int depth, List<string> route, List<BomRequirement> requirements, CancellationToken cancellationToken)
    {
        if (depth > 20)
        {
            return Error.Validation("bom.too_deep", "Bills of material nest more than 20 levels.").WithWhy(("route", route));
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var multiple = outputQuantity / bom.OutputQty;
        foreach (var line in bom.Lines.OrderBy(static l => l.Position))
        {
            var component = await db.Items.Include(static i => i.Uoms).SingleAsync(i => i.Id == line.ComponentItemId, cancellationToken);
            var uom = component.Uoms.First(u => u.UomId == line.UomId);
            var baseUom = byId[component.BaseUomId];
            var baseQuantity = line.Quantity * uom.Numerator / uom.Denominator * multiple;
            var withScrap = baseQuantity * (1m + line.ScrapPct / 100m) * scrapMultiplier;
            var variantSku = line.ComponentVariantId is { } vid ? await db.Variants.Where(v => v.Id == vid).Select(static v => v.Sku).SingleOrDefaultAsync(cancellationToken) : null;
            var childRoute = route.Append(component.Code).ToList();
            requirements.Add(new BomRequirement(component.Id, component.Code, component.Name.Values, line.ComponentVariantId, variantSku, baseUom.Id, baseUom.Code, baseUom.Precision,
                ItemUomMath.Normalize(baseQuantity), ItemUomMath.Normalize(withScrap), depth, childRoute));

            var child = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.ItemId == component.Id && b.IsActive, cancellationToken);
            if (child is not null)
            {
                if (route.Contains(component.Code, StringComparer.Ordinal))
                {
                    return Error.Validation("bom.cycle", "The bill of material contains itself.").WithWhy(("route", childRoute));
                }

                var walked = await WalkAsync(child, baseQuantity, scrapMultiplier * (1m + line.ScrapPct / 100m), depth + 1, childRoute, requirements, cancellationToken);
                if (walked.IsFailure)
                {
                    return walked;
                }
            }
        }

        return Result.Success();
    }

    private async Task ActivateAsync(Bom bom, CancellationToken cancellationToken)
    {
        foreach (var other in await db.Boms.Where(b => b.ItemId == bom.ItemId && b.Id != bom.Id && b.IsActive).ToListAsync(cancellationToken))
        {
            other.IsActive = false;
            other.UpdatedAt = clock.UtcNow;
        }

        // The unique partial index allows one active bill per item; the retired ones must be written first.
        await db.SaveChangesAsync(cancellationToken);
        bom.IsActive = true;
    }

    private async Task<Result<(string Kind, List<BomLine> Lines)>> ValidateAsync(Item item, Bom? existing, SaveBomRequest request, CancellationToken cancellationToken)
    {
        var kind = Validation.OneOf(request.Kind, "bom.kind", Validation.BomKinds);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        if (item.Type != kind.Value)
        {
            return Error.Validation("bom.kind_mismatch", "A kit bill belongs to a kit item and an assembly bill to an assembly item.").WithWhy(("itemType", item.Type), ("kind", kind.Value));
        }

        if (request.OutputQty <= 0m)
        {
            return Error.Validation("bom.output_qty_invalid", "The output quantity must be positive.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("bom.lines_required", "A bill of material has at least one component.");
        }

        var byId = await UomsByIdAsync(cancellationToken);
        var lines = new List<BomLine>();
        var position = 0;
        foreach (var line in request.Lines)
        {
            var code = line.ComponentItemCode?.Trim();
            var component = line.ComponentItemId is { } cid
                ? await db.Items.Include(static i => i.Uoms).SingleOrDefaultAsync(i => i.Id == cid, cancellationToken)
                : await db.Items.Include(static i => i.Uoms).SingleOrDefaultAsync(i => i.Code == code, cancellationToken);
            if (component is null)
            {
                return Error.Validation("bom.component_unknown", "The component item does not exist.").WithWhy(("component", line.ComponentItemCode ?? line.ComponentItemId?.ToString()));
            }

            if (component.Id == item.Id)
            {
                return Error.Validation("bom.cycle", "An item cannot be a component of itself.").WithWhy(("component", component.Code));
            }

            if (component.Type is not ("stock" or "kit" or "assembly"))
            {
                return Error.Validation("bom.component_not_stock", "Components are stock, kit or assembly items.").WithWhy(("component", component.Code), ("type", component.Type));
            }

            if (line.Quantity <= 0m)
            {
                return Error.Validation("bom.line_quantity_invalid", "Component quantities must be positive.").WithWhy(("component", component.Code));
            }

            if (line.ScrapPct is < 0m or >= 100m)
            {
                return Error.Validation("bom.scrap_invalid", "Scrap is a percentage from 0 up to (not including) 100.").WithWhy(("component", component.Code), ("scrapPct", line.ScrapPct));
            }

            ItemUom uom;
            if (line.UomId is null && string.IsNullOrWhiteSpace(line.Uom))
            {
                uom = component.Uoms.First(u => u.UomId == component.BaseUomId);
            }
            else
            {
                var resolved = ItemService.ResolveUom(byId, line.UomId, line.Uom, "bom.uom");
                if (resolved.IsFailure)
                {
                    return resolved.Error!;
                }

                var row = component.Uoms.FirstOrDefault(u => u.UomId == resolved.Value.Id);
                if (row is null)
                {
                    return Error.Validation("bom.uom_not_item_uom", "The line's unit must be one of the component's units.").WithWhy(("component", component.Code), ("uom", resolved.Value.Code));
                }

                uom = row;
            }

            var exact = ItemUomMath.ToBase(line.Quantity, uom.Numerator, uom.Denominator, byId[component.BaseUomId].Precision);
            if (exact.IsFailure)
            {
                return exact.Error!.WithWhy(("component", component.Code));
            }

            if (line.ComponentVariantId is { } variantId && !await db.Variants.AnyAsync(v => v.Id == variantId && v.ItemId == component.Id, cancellationToken))
            {
                return Error.Validation("bom.variant_unknown", "The variant must belong to the component item.").WithWhy(("component", component.Code), ("variantId", variantId));
            }

            // A component whose own active bill leads back to this item would loop forever at explosion.
            var cycle = await ReachesAsync(component.Id, item.Id, [item.Code, component.Code], cancellationToken);
            if (cycle.IsFailure)
            {
                return cycle.Error!;
            }

            lines.Add(new BomLine { Id = Guid.CreateVersion7(), Position = position++, ComponentItemId = component.Id, ComponentVariantId = line.ComponentVariantId, Quantity = line.Quantity, UomId = uom.UomId, ScrapPct = line.ScrapPct });
        }

        return (kind.Value, lines);
    }

    private async Task<Result> ReachesAsync(Guid fromItemId, Guid targetItemId, List<string> route, CancellationToken cancellationToken)
    {
        if (route.Count > 21)
        {
            return Error.Validation("bom.too_deep", "Bills of material nest more than 20 levels.").WithWhy(("route", route));
        }

        var bom = await db.Boms.Include(static b => b.Lines).SingleOrDefaultAsync(b => b.ItemId == fromItemId && b.IsActive, cancellationToken);
        if (bom is null)
        {
            return Result.Success();
        }

        foreach (var line in bom.Lines)
        {
            var code = await db.Items.Where(i => i.Id == line.ComponentItemId).Select(static i => i.Code).SingleAsync(cancellationToken);
            var childRoute = route.Append(code).ToList();
            if (line.ComponentItemId == targetItemId)
            {
                return Error.Validation("bom.cycle", "The bill of material would contain itself through a nested bill.").WithWhy(("route", childRoute));
            }

            var deeper = await ReachesAsync(line.ComponentItemId, targetItemId, childRoute, cancellationToken);
            if (deeper.IsFailure)
            {
                return deeper;
            }
        }

        return Result.Success();
    }

    private async Task<IReadOnlyDictionary<Guid, UomInfo>> UomsByIdAsync(CancellationToken cancellationToken) =>
        _uomsById ??= (await uoms.ListAsync(cancellationToken)).ToDictionary(static u => u.Id);

    private async Task<BomSummary> MapAsync(Bom bom, CancellationToken cancellationToken)
    {
        var byId = await UomsByIdAsync(cancellationToken);
        var item = await db.Items.SingleAsync(i => i.Id == bom.ItemId, cancellationToken);
        var componentIds = bom.Lines.Select(static l => l.ComponentItemId).Distinct().ToList();
        var components = await db.Items.Include(static i => i.Uoms).Where(i => componentIds.Contains(i.Id)).ToDictionaryAsync(static i => i.Id, cancellationToken);
        var variantIds = bom.Lines.Where(static l => l.ComponentVariantId != null).Select(static l => l.ComponentVariantId!.Value).ToList();
        var variants = variantIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Variants.Where(v => variantIds.Contains(v.Id)).ToDictionaryAsync(static v => v.Id, static v => v.Sku, cancellationToken);
        var lines = bom.Lines.OrderBy(static l => l.Position).Select(l =>
        {
            var component = components[l.ComponentItemId];
            var uom = component.Uoms.First(u => u.UomId == l.UomId);
            var baseUom = byId[component.BaseUomId];
            return new BomLineSummary(l.Id, l.Position, component.Id, component.Code, component.Name.Values, l.ComponentVariantId, l.ComponentVariantId is { } v ? variants.GetValueOrDefault(v) : null,
                ItemUomMath.Normalize(l.Quantity), l.UomId, byId[l.UomId].Code, ItemUomMath.Normalize(l.Quantity * uom.Numerator / uom.Denominator), baseUom.Code, ItemUomMath.Normalize(l.ScrapPct));
        }).ToList();
        return new BomSummary(bom.Id, bom.ItemId, item.Code, bom.Kind, bom.Version, ItemUomMath.Normalize(bom.OutputQty), bom.IsActive, lines, bom.UpdatedAt);
    }
}

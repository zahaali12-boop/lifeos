using Microsoft.EntityFrameworkCore;
using Quicker.Items.Domain;
using Quicker.Items.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Items.Application;

/// <summary>Brands, and the attributes (colour, size, …) whose values define variants.</summary>
public sealed class MasterDataService(ItemsDbContext db, ItemService items, IClock clock)
{
    // ------------------------------------------------------------------ brands

    public async Task<IReadOnlyList<BrandSummary>> ListBrandsAsync(CancellationToken cancellationToken) =>
        (await db.Brands.OrderBy(static b => b.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<BrandSummary>> SaveBrandAsync(Guid? brandId, SaveBrandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, "brand");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "brand");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        Brand? brand = null;
        if (brandId is { } id)
        {
            brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
            if (brand is null)
            {
                return Error.NotFound("brand", id);
            }
        }

        if (await db.Brands.AnyAsync(b => b.Code == code.Value && (brand == null || b.Id != brand.Id), cancellationToken))
        {
            return Error.Conflict("brand.code_taken", $"A brand with code '{code.Value}' already exists.");
        }

        var now = clock.UtcNow;
        if (brand is null)
        {
            brand = new Brand { Id = Guid.CreateVersion7(), CreatedAt = now };
            db.Brands.Add(brand);
        }

        brand.Code = code.Value;
        brand.Name = name.Value;
        brand.IsActive = request.IsActive;
        brand.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return Map(brand);
    }

    public async Task<Result> DeleteBrandAsync(Guid brandId, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.SingleOrDefaultAsync(b => b.Id == brandId, cancellationToken);
        if (brand is null)
        {
            return Error.NotFound("brand", brandId);
        }

        if (await db.Items.AnyAsync(i => i.BrandId == brandId, cancellationToken))
        {
            return Error.Conflict("brand.in_use", "Items carry this brand; reassign them first.");
        }

        db.Brands.Remove(brand);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ attributes

    public async Task<IReadOnlyList<AttributeSummary>> ListAttributesAsync(CancellationToken cancellationToken) =>
        (await db.Attributes.Include(static a => a.Values).OrderBy(static a => a.SortOrder).ThenBy(static a => a.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<AttributeSummary>> SaveAttributeAsync(Guid? attributeId, SaveAttributeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, "attribute");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "attribute");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var values = new List<(string Code, Kernel.Text.LocalizedText Name, int SortOrder)>();
        foreach (var value in request.Values ?? [])
        {
            var valueCode = Validation.UpperCode(value.Code, "attribute_value");
            if (valueCode.IsFailure)
            {
                return valueCode.Error!;
            }

            var valueName = Validation.Name(value.Name, "attribute_value");
            if (valueName.IsFailure)
            {
                return valueName.Error!;
            }

            if (values.Any(v => v.Code == valueCode.Value))
            {
                return Error.Validation("attribute_value.duplicate", "Each value code appears once.").WithWhy(("code", valueCode.Value));
            }

            values.Add((valueCode.Value, valueName.Value, value.SortOrder));
        }

        ItemAttribute? attribute = null;
        if (attributeId is { } id)
        {
            attribute = await db.Attributes.Include(static a => a.Values).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (attribute is null)
            {
                return Error.NotFound("attribute", id);
            }
        }

        if (await db.Attributes.AnyAsync(a => a.Code == code.Value && (attribute == null || a.Id != attribute.Id), cancellationToken))
        {
            return Error.Conflict("attribute.code_taken", $"An attribute with code '{code.Value}' already exists.");
        }

        var now = clock.UtcNow;
        if (attribute is null)
        {
            attribute = new ItemAttribute { Id = Guid.CreateVersion7(), CreatedAt = now };
            db.Attributes.Add(attribute);
        }

        attribute.Code = code.Value;
        attribute.Name = name.Value;
        attribute.SortOrder = request.SortOrder;
        attribute.UpdatedAt = now;

        if (request.Values is not null)
        {
            // Values are matched by code: kept (and renamed) when they still appear, added when new, removed when
            // absent and unused by any variant.
            var removed = attribute.Values.Where(v => values.All(n => n.Code != v.Code)).ToList();
            foreach (var value in removed)
            {
                if (await items.AttributeValueInUseAsync(value.Id, cancellationToken))
                {
                    return Error.Conflict("attribute_value.in_use", "Variants use this value; remove it from them first.").WithWhy(("code", value.Code));
                }

                attribute.Values.Remove(value);
            }

            foreach (var (valueCode, valueName, sortOrder) in values)
            {
                var existing = attribute.Values.FirstOrDefault(v => v.Code == valueCode);
                if (existing is null)
                {
                    attribute.Values.Add(new ItemAttributeValue { Id = Guid.CreateVersion7(), AttributeId = attribute.Id, Code = valueCode, Name = valueName, SortOrder = sortOrder });
                }
                else
                {
                    existing.Name = valueName;
                    existing.SortOrder = sortOrder;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(attribute);
    }

    public async Task<Result> DeleteAttributeAsync(Guid attributeId, CancellationToken cancellationToken)
    {
        var attribute = await db.Attributes.Include(static a => a.Values).SingleOrDefaultAsync(a => a.Id == attributeId, cancellationToken);
        if (attribute is null)
        {
            return Error.NotFound("attribute", attributeId);
        }

        if (await items.AttributeInUseAsync(attribute.Code, cancellationToken))
        {
            return Error.Conflict("attribute.in_use", "Variants use this attribute; remove it from them first.");
        }

        db.Attributes.Remove(attribute);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static BrandSummary Map(Brand b) => new(b.Id, b.Code, b.Name.Values, b.IsActive, b.UpdatedAt);

    private static AttributeSummary Map(ItemAttribute a) =>
        new(a.Id, a.Code, a.Name.Values, a.SortOrder, a.Values.OrderBy(static v => v.SortOrder).ThenBy(static v => v.Code, StringComparer.Ordinal).Select(static v => new AttributeValueSummary(v.Id, v.Code, v.Name.Values, v.SortOrder)).ToList(), a.UpdatedAt);
}

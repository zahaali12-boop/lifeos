using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Quantities;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;

namespace Quicker.Organization.Application;

/// <summary>Units of measure and global conversions with rational factors (ADR-0005); item-specific conversions come with Items (M2).</summary>
public sealed class UomService(OrganizationDbContext db, IClock clock) : IUomConversions, IUomDirectory
{
    private static readonly string[] Families = ["count", "weight", "volume", "length", "area", "time", "other"];

    // ------------------------------------------------------------------ units

    public async Task<IReadOnlyList<UomSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Uoms.OrderBy(static u => u.Family).ThenBy(static u => u.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<UomSummary>> CreateAsync(SaveUomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validated = Validate(request);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, name, family) = validated.Value;
        if (await db.Uoms.AnyAsync(u => u.Code == code, cancellationToken))
        {
            return Error.Conflict("uom.code_taken", $"A unit with code '{code}' already exists.");
        }

        var now = clock.UtcNow;
        var uom = new Uom { Id = Guid.CreateVersion7(), Code = code, Name = name, Family = family, Precision = request.Precision, IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now };
        db.Uoms.Add(uom);
        await db.SaveChangesAsync(cancellationToken);
        return Map(uom);
    }

    public async Task<Result<UomSummary>> UpdateAsync(Guid uomId, SaveUomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uom = await db.Uoms.SingleOrDefaultAsync(u => u.Id == uomId, cancellationToken);
        if (uom is null)
        {
            return Error.NotFound("uom", uomId);
        }

        var validated = Validate(request);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        var (code, name, family) = validated.Value;
        if (uom.IsSystem && (code != uom.Code || family != uom.Family))
        {
            return Error.Forbidden("uom.system_locked", "System units keep their code and family.");
        }

        if (await db.Uoms.AnyAsync(u => u.Code == code && u.Id != uomId, cancellationToken))
        {
            return Error.Conflict("uom.code_taken", $"A unit with code '{code}' already exists.");
        }

        uom.Code = code;
        uom.Name = name;
        uom.Family = family;
        uom.Precision = request.Precision;
        uom.IsActive = request.IsActive;
        uom.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(uom);
    }

    private static Result<(string Code, Kernel.Text.LocalizedText Name, string Family)> Validate(SaveUomRequest request)
    {
        var code = Validation.UpperCode(request.Code, "uom");
        var name = Validation.Name(request.Name, "uom");
        var family = Validation.OneOf(request.Family?.ToLowerInvariant(), "uom.family", Families);
        if (code.IsFailure || name.IsFailure || family.IsFailure)
        {
            return code.Error ?? name.Error ?? family.Error!;
        }

        return request.Precision is < 0 or > 9
            ? Error.Validation("uom.precision_invalid", "Precision is 0 to 9 decimals.")
            : (code.Value, name.Value, family.Value);
    }

    // ------------------------------------------------------------------ conversions

    public async Task<IReadOnlyList<UomConversionSummary>> ListConversionsAsync(CancellationToken cancellationToken)
    {
        var rows = await (from c in db.UomConversions
                          join f in db.Uoms on new { c.TenantId, Id = c.FromUomId } equals new { f.TenantId, f.Id }
                          join t in db.Uoms on new { c.TenantId, Id = c.ToUomId } equals new { t.TenantId, t.Id }
                          orderby f.Code, t.Code
                          select new { c, FromCode = f.Code, ToCode = t.Code }).ToListAsync(cancellationToken);
        return rows.Select(x => new UomConversionSummary(x.c.Id, x.c.FromUomId, x.FromCode, x.c.ToUomId, x.ToCode, x.c.Numerator, x.c.Denominator)).ToList();
    }

    public async Task<Result<UomConversionSummary>> SaveConversionAsync(SaveUomConversionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FromUomId == request.ToUomId)
        {
            return Error.Validation("uom_conversion.same_unit", "A conversion needs two different units.");
        }

        if (request.Numerator <= 0m || request.Denominator <= 0m)
        {
            return Error.Validation("uom_conversion.factor_invalid", "Numerator and denominator are positive numbers.");
        }

        var from = await db.Uoms.SingleOrDefaultAsync(u => u.Id == request.FromUomId, cancellationToken);
        var to = await db.Uoms.SingleOrDefaultAsync(u => u.Id == request.ToUomId, cancellationToken);
        if (from is null || to is null)
        {
            return Error.NotFound("uom", from is null ? request.FromUomId : request.ToUomId);
        }

        if (from.Family != to.Family)
        {
            return Error.Validation("uom_conversion.family_mismatch", $"{from.Code} ({from.Family}) and {to.Code} ({to.Family}) are different families; conversions between them are item-specific.")
                .WithWhy(("from", from.Code), ("to", to.Code));
        }

        var row = await db.UomConversions.SingleOrDefaultAsync(c => (c.FromUomId == from.Id && c.ToUomId == to.Id) || (c.FromUomId == to.Id && c.ToUomId == from.Id), cancellationToken);
        if (row is null)
        {
            row = new UomConversionRow { Id = Guid.CreateVersion7(), FromUomId = from.Id, ToUomId = to.Id };
            db.UomConversions.Add(row);
        }

        if (row.FromUomId == from.Id)
        {
            row.Numerator = request.Numerator;
            row.Denominator = request.Denominator;
        }
        else
        {
            // The pair is stored once; saving the reverse direction updates the same row with the inverse factor.
            row.Numerator = request.Denominator;
            row.Denominator = request.Numerator;
        }

        await db.SaveChangesAsync(cancellationToken);
        var stored = row.FromUomId == from.Id ? (from, to) : (to, from);
        return new UomConversionSummary(row.Id, row.FromUomId, stored.Item1.Code, row.ToUomId, stored.Item2.Code, row.Numerator, row.Denominator);
    }

    public async Task<Result> DeleteConversionAsync(Guid conversionId, CancellationToken cancellationToken)
    {
        var row = await db.UomConversions.SingleOrDefaultAsync(c => c.Id == conversionId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound("uom_conversion", conversionId);
        }

        db.UomConversions.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<QuantityConversion>> ConvertAsync(Guid fromUomId, Guid toUomId, decimal value, CancellationToken cancellationToken)
    {
        var resolved = await ResolveWithMethodAsync(fromUomId, toUomId, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var (conversion, method) = resolved.Value;
        var quantity = Quantity.Of(value, conversion.From).ConvertTo(conversion.To, conversion);
        return new QuantityConversion(value, conversion.From.Code, quantity.Value, conversion.To.Code, conversion.Numerator, conversion.Denominator, method);
    }

    public async Task<Result<UomConversion>> ResolveAsync(Guid fromUomId, Guid toUomId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithMethodAsync(fromUomId, toUomId, cancellationToken);
        return resolved.IsFailure ? resolved.Error! : resolved.Value.Conversion;
    }

    private async Task<Result<(UomConversion Conversion, string Method)>> ResolveWithMethodAsync(Guid fromUomId, Guid toUomId, CancellationToken cancellationToken)
    {
        var from = await db.Uoms.SingleOrDefaultAsync(u => u.Id == fromUomId, cancellationToken);
        var to = await db.Uoms.SingleOrDefaultAsync(u => u.Id == toUomId, cancellationToken);
        if (from is null || to is null)
        {
            return Error.NotFound("uom", from is null ? fromUomId : toUomId);
        }

        var source = UnitOfMeasure.Of(from.Code, from.Precision);
        var target = UnitOfMeasure.Of(to.Code, to.Precision);
        if (from.Id == to.Id)
        {
            return (new UomConversion(source, target, 1m, 1m), "identity");
        }

        if (from.Family != to.Family)
        {
            return Error.Validation("uom_conversion.family_mismatch", $"{from.Code} and {to.Code} are different families.").WithWhy(("from", from.Code), ("to", to.Code));
        }

        var family = from.Family;
        var units = await db.Uoms.Where(u => u.Family == family).ToDictionaryAsync(static u => u.Id, cancellationToken);
        var ids = units.Keys.ToList();
        var rows = await db.UomConversions.Where(c => ids.Contains(c.FromUomId) && ids.Contains(c.ToUomId)).ToListAsync(cancellationToken);

        // Edges in both directions; a direct edge wins, otherwise one intermediate unit.
        var edges = new Dictionary<Guid, List<(Guid To, decimal Numerator, decimal Denominator)>>();
        foreach (var row in rows)
        {
            Add(edges, row.FromUomId, row.ToUomId, row.Numerator, row.Denominator);
            Add(edges, row.ToUomId, row.FromUomId, row.Denominator, row.Numerator);
        }

        if (edges.TryGetValue(from.Id, out var direct))
        {
            var hit = direct.Find(e => e.To == to.Id);
            if (hit.To == to.Id)
            {
                var stored = rows.Exists(r => r.FromUomId == from.Id && r.ToUomId == to.Id);
                return (new UomConversion(source, target, hit.Numerator, hit.Denominator), stored ? "direct" : "inverse");
            }

            foreach (var leg1 in direct)
            {
                if (edges.TryGetValue(leg1.To, out var second))
                {
                    var leg2 = second.Find(e => e.To == to.Id);
                    if (leg2.To == to.Id)
                    {
                        var via = UnitOfMeasure.Of(units[leg1.To].Code, units[leg1.To].Precision);
                        var chained = new UomConversion(source, via, leg1.Numerator, leg1.Denominator).Then(new UomConversion(via, target, leg2.Numerator, leg2.Denominator));
                        return (chained, $"via {via.Code}");
                    }
                }
            }
        }

        return Error.Conflict("uom_conversion.not_found", $"No conversion from {from.Code} to {to.Code}, directly or through one unit of the {family} family.").WithWhy(("from", from.Code), ("to", to.Code));
    }

    private static void Add(Dictionary<Guid, List<(Guid To, decimal Numerator, decimal Denominator)>> edges, Guid from, Guid to, decimal numerator, decimal denominator)
    {
        if (!edges.TryGetValue(from, out var list))
        {
            list = [];
            edges[from] = list;
        }

        list.Add((to, numerator, denominator));
    }

    // ------------------------------------------------------------------ IUomDirectory

    async Task<IReadOnlyList<UomInfo>> IUomDirectory.ListAsync(CancellationToken cancellationToken) =>
        (await db.Uoms.OrderBy(static u => u.Family).ThenBy(static u => u.Code).ToListAsync(cancellationToken)).Select(Info).ToList();

    public async Task<UomInfo?> FindAsync(Guid uomId, CancellationToken cancellationToken = default)
    {
        var uom = await db.Uoms.SingleOrDefaultAsync(u => u.Id == uomId, cancellationToken);
        return uom is null ? null : Info(uom);
    }

    public async Task<UomInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var uom = await db.Uoms.SingleOrDefaultAsync(u => u.Code == normalized, cancellationToken);
        return uom is null ? null : Info(uom);
    }

    private static UomInfo Info(Uom u) => new(u.Id, u.Code, u.Name, u.Family, u.Precision, u.IsActive);

    private static UomSummary Map(Uom u) => new(u.Id, u.Code, u.Name.Values, u.Family, u.Precision, u.IsSystem, u.IsActive);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Persistence;

namespace Quicker.Organization.Application;

/// <summary>Dimensions, hierarchical values, and deduplicated dimension sets.</summary>
public sealed class DimensionService(OrganizationDbContext db, IUnitOfWorkAccessor unitOfWork, IClock clock) : IDimensionSets, IDimensionDirectory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ dimensions

    public async Task<IReadOnlyList<DimensionSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Dimensions.OrderBy(static d => d.SortOrder).ThenBy(static d => d.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<DimensionSummary>> CreateAsync(SaveDimensionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, "dimension");
        var name = Validation.Name(request.Name, "dimension");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (!char.IsAsciiLetterUpper(code.Value[0]) || code.Value.Any(static c => c is '.' or '-'))
        {
            return Error.Validation("dimension.code_invalid", "Dimension codes start with a letter and use letters, digits and '_' only.");
        }

        if (await db.Dimensions.AnyAsync(d => d.Code == code.Value, cancellationToken))
        {
            return Error.Conflict("dimension.code_taken", $"A dimension with code '{code.Value}' already exists.");
        }

        var now = clock.UtcNow;
        var dimension = new Dimension { Id = Guid.CreateVersion7(), Code = code.Value, Name = name.Value, IsHierarchical = request.IsHierarchical, SortOrder = request.SortOrder, IsActive = request.IsActive, CreatedAt = now, UpdatedAt = now };
        db.Dimensions.Add(dimension);
        await db.SaveChangesAsync(cancellationToken);
        _directoryCache = null;
        return Map(dimension);
    }

    public async Task<Result<DimensionSummary>> UpdateAsync(Guid dimensionId, SaveDimensionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dimension = await db.Dimensions.SingleOrDefaultAsync(d => d.Id == dimensionId, cancellationToken);
        if (dimension is null)
        {
            return Error.NotFound("dimension", dimensionId);
        }

        var name = Validation.Name(request.Name, "dimension");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        if (dimension.IsSystem && (!string.Equals(request.Code?.Trim(), dimension.Code, StringComparison.OrdinalIgnoreCase) || !request.IsActive))
        {
            return Error.Forbidden("dimension.system_locked", "System dimensions keep their code and stay active.");
        }

        var code = Validation.UpperCode(request.Code, "dimension");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        if (await db.Dimensions.AnyAsync(d => d.Code == code.Value && d.Id != dimensionId, cancellationToken))
        {
            return Error.Conflict("dimension.code_taken", $"A dimension with code '{code.Value}' already exists.");
        }

        dimension.Code = code.Value;
        dimension.Name = name.Value;
        dimension.IsHierarchical = request.IsHierarchical;
        dimension.SortOrder = request.SortOrder;
        dimension.IsActive = request.IsActive;
        dimension.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _directoryCache = null;
        return Map(dimension);
    }

    // ------------------------------------------------------------------ values

    public async Task<Result<IReadOnlyList<DimensionValueSummary>>> ListValuesAsync(Guid dimensionId, CancellationToken cancellationToken)
    {
        if (!await db.Dimensions.AnyAsync(d => d.Id == dimensionId, cancellationToken))
        {
            return Error.NotFound("dimension", dimensionId);
        }

        var values = await db.DimensionValues.Where(v => v.DimensionId == dimensionId).OrderBy(static v => v.Code).ToListAsync(cancellationToken);
        return values.Select(Map).ToList();
    }

    public async Task<Result<DimensionValueSummary>> CreateValueAsync(Guid dimensionId, SaveDimensionValueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dimension = await db.Dimensions.SingleOrDefaultAsync(d => d.Id == dimensionId, cancellationToken);
        if (dimension is null)
        {
            return Error.NotFound("dimension", dimensionId);
        }

        if (dimension.Code == OrganizationDefaults.BranchDimension)
        {
            return Error.Conflict("dimension.branch_values_are_branches", "BRANCH values are created with branches; create a branch on the company instead.");
        }

        var value = new DimensionValue { Id = Guid.CreateVersion7(), DimensionId = dimensionId, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(dimension, value, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.DimensionValues.Add(value);
        await db.SaveChangesAsync(cancellationToken);
        _directoryCache = null;
        return Map(value);
    }

    public async Task<Result<DimensionValueSummary>> UpdateValueAsync(Guid dimensionId, Guid valueId, SaveDimensionValueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dimension = await db.Dimensions.SingleOrDefaultAsync(d => d.Id == dimensionId, cancellationToken);
        var value = await db.DimensionValues.SingleOrDefaultAsync(v => v.DimensionId == dimensionId && v.Id == valueId, cancellationToken);
        if (dimension is null || value is null)
        {
            return Error.NotFound("dimension_value", valueId);
        }

        if (dimension.Code == OrganizationDefaults.BranchDimension)
        {
            return Error.Conflict("dimension.branch_values_are_branches", "BRANCH values change with their branch; edit the branch instead.");
        }

        var applied = await ApplyAsync(dimension, value, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        value.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _directoryCache = null;
        return Map(value);
    }

    private async Task<Result> ApplyAsync(Dimension dimension, DimensionValue value, SaveDimensionValueRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.UpperCode(request.Code, "dimension_value");
        var name = Validation.Name(request.Name, "dimension_value");
        if (code.IsFailure || name.IsFailure)
        {
            return code.Error ?? name.Error!;
        }

        if (await db.DimensionValues.AnyAsync(v => v.DimensionId == dimension.Id && v.Code == code.Value && v.Id != value.Id, cancellationToken))
        {
            return Error.Conflict("dimension_value.code_taken", $"Value '{code.Value}' already exists on {dimension.Code}.");
        }

        if (request.ParentId is { } parentId)
        {
            if (!dimension.IsHierarchical)
            {
                return Error.Validation("dimension_value.not_hierarchical", $"{dimension.Code} is flat; values have no parent.");
            }

            if (parentId == value.Id || !await db.DimensionValues.AnyAsync(v => v.DimensionId == dimension.Id && v.Id == parentId, cancellationToken))
            {
                return Error.NotFound("dimension_value", parentId);
            }
        }

        if (request.CompanyId is { } companyId && !await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            return Error.NotFound("company", companyId);
        }

        if (request.ValidFrom is { } from && request.ValidTo is { } to && to < from)
        {
            return Error.Validation("dimension_value.validity_invalid", "valid_to must be on or after valid_from.");
        }

        value.Code = code.Value;
        value.Name = name.Value;
        value.ParentId = request.ParentId;
        value.CompanyId = request.CompanyId;
        value.OwnerMembershipId = request.OwnerMembershipId;
        value.ValidFrom = request.ValidFrom;
        value.ValidTo = request.ValidTo;
        value.IsActive = request.IsActive;
        value.UpdatedAt = value.CreatedAt == default ? clock.UtcNow : value.UpdatedAt;
        return Result.Success();
    }

    // ------------------------------------------------------------------ sets

    public async Task<Result<DimensionSetSummary>> GetOrCreateSetAsync(DimensionSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = await GetOrCreateAsync(request.Values, cancellationToken);
        if (id.IsFailure)
        {
            return id.Error!;
        }

        var set = await db.DimensionSets.SingleAsync(s => s.Id == id.Value, cancellationToken);
        return Map(set);
    }

    public async Task<DimensionSetSummary?> GetSetAsync(Guid setId, CancellationToken cancellationToken)
    {
        var set = await db.DimensionSets.SingleOrDefaultAsync(s => s.Id == setId, cancellationToken);
        return set is null ? null : Map(set);
    }

    public async Task<Result<Guid>> GetOrCreateAsync(IReadOnlyDictionary<string, Guid> valuesByDimensionCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(valuesByDimensionCode);
        var normalized = new SortedDictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, valueId) in valuesByDimensionCode)
        {
            normalized[code.Trim().ToUpperInvariant()] = valueId;
        }

        if (normalized.Count == 0)
        {
            return Error.Validation("dimension_set.empty", "A dimension set needs at least one value.");
        }

        var codes = normalized.Keys.ToList();
        var dimensions = await db.Dimensions.Where(d => codes.Contains(d.Code)).ToDictionaryAsync(static d => d.Code, StringComparer.Ordinal, cancellationToken);
        var ids = normalized.Values.ToList();
        var values = await db.DimensionValues.Where(v => ids.Contains(v.Id)).ToDictionaryAsync(static v => v.Id, cancellationToken);
        foreach (var (code, valueId) in normalized)
        {
            if (!dimensions.TryGetValue(code, out var dimension))
            {
                return Error.NotFound("dimension", code);
            }

            if (!values.TryGetValue(valueId, out var value) || value.DimensionId != dimension.Id)
            {
                return Error.Validation("dimension_set.value_mismatch", $"Value {valueId} does not belong to dimension {code}.").WithWhy(("dimension", code), ("valueId", valueId));
            }

            if (!value.IsActive || !dimension.IsActive)
            {
                return Error.Conflict("dimension_set.value_inactive", $"Value {value.Code} of {code} is inactive.").WithWhy(("dimension", code), ("value", value.Code));
            }
        }

        var hash = Hash(normalized);
        var existing = await db.DimensionSets.Where(s => s.Hash == hash).Select(static s => (Guid?)s.Id).SingleOrDefaultAsync(cancellationToken);
        if (existing is { } found)
        {
            return found;
        }

        // Concurrent first users of the same combination race on the unique hash; ON CONFLICT returns whichever row won.
        var uow = unitOfWork.Current;
        var id = await uow.Connection.ExecuteScalarAsync<Guid>("""
            INSERT INTO app.org_dimension_sets (tenant_id, id, hash, values)
            VALUES (@tenant, @id, @hash, @values::jsonb)
            ON CONFLICT (tenant_id, hash) DO UPDATE SET hash = EXCLUDED.hash
            RETURNING id
            """, new { tenant = uow.Context.TenantId.Value, id = Guid.CreateVersion7(), hash, values = JsonSerializer.Serialize(normalized, Json) }, uow.Transaction);
        return id;
    }

    public async Task<IReadOnlyDictionary<string, Guid>?> GetAsync(Guid setId, CancellationToken cancellationToken = default) =>
        await db.DimensionSets.Where(s => s.Id == setId).Select(static s => s.Values).SingleOrDefaultAsync(cancellationToken);

    // The dimension list is read for every posted line; memoised for the life of this scoped service, dropped after every write it makes.
    private IReadOnlyList<DimensionInfo>? _directoryCache;

    async Task<IReadOnlyList<DimensionInfo>> IDimensionDirectory.ListAsync(CancellationToken cancellationToken) =>
        _directoryCache ??= await db.Dimensions.OrderBy(static d => d.SortOrder).ThenBy(static d => d.Code).Select(static d => new DimensionInfo(d.Id, d.Code, d.Name, d.IsActive)).ToListAsync(cancellationToken);

    public async Task<DimensionValueInfo?> FindValueAsync(Guid valueId, CancellationToken cancellationToken = default) =>
        await db.DimensionValues.Where(v => v.Id == valueId).Select(static v => new DimensionValueInfo(v.Id, v.DimensionId, v.Code, v.Name, v.IsActive)).SingleOrDefaultAsync(cancellationToken);

    /// <summary>SHA-256 of "CODE=valueId" lines sorted by code: the same combination always hashes the same.</summary>
    public static byte[] Hash(IReadOnlyDictionary<string, Guid> normalized) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key}={p.Value:N}"))));

    // ------------------------------------------------------------------ mapping

    private static DimensionSummary Map(Dimension d) => new(d.Id, d.Code, d.Name.Values, d.IsSystem, d.IsHierarchical, d.SortOrder, d.IsActive);

    private static DimensionValueSummary Map(DimensionValue v) => new(v.Id, v.DimensionId, v.ParentId, v.Code, v.Name.Values, v.CompanyId, v.OwnerMembershipId, v.ValidFrom, v.ValidTo, v.IsActive);

    private static DimensionSetSummary Map(DimensionSet s) => new(s.Id, s.Values, Convert.ToHexStringLower(s.Hash));
}

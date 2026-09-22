using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Collaboration.Application;

public sealed record SaveViewRequest(string EntityType, string Name, JsonElement? Definition = null, bool Shared = false, bool IsDefault = false);

public sealed record SavedViewView(Guid Id, string EntityType, string Name, Guid OwnerMembershipId, bool Shared, bool IsDefault, JsonElement Definition, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Grid views per entity type: private to their owner unless shared; one default per owner and entity type.</summary>
public sealed class SavedViewService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, IClock clock)
{
    public const int MaxDefinitionBytes = 16 * 1024;
    private static readonly string[] AllowedKeys = ["filters", "sort", "columns", "groupBy", "pageSize", "density"];

    private Guid? Me => unitOfWork.Current.Context.MembershipId?.Value;

    public async Task<Result<IReadOnlyList<SavedViewView>>> ListAsync(string? entityType, CancellationToken cancellationToken)
    {
        if (Me is not { } me)
        {
            return NoMembership();
        }

        var type = entityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type))
        {
            return Error.Validation("view.entity_invalid", "entityType is a lower-case name such as sales_invoice.");
        }

        var rows = await db.Views.Where(v => v.EntityType == type && (v.OwnerMembershipId == me || v.Shared)).OrderBy(static v => v.Name).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<SavedViewView>> CreateAsync(SaveViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Me is not { } me)
        {
            return NoMembership();
        }

        var view = new SavedView { Id = Guid.CreateVersion7(), OwnerMembershipId = me, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(view, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Views.Add(view);
        await db.SaveChangesAsync(cancellationToken);
        return Map(view);
    }

    public async Task<Result<SavedViewView>> UpdateAsync(Guid id, SaveViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = await VisibleAsync(id, cancellationToken);
        if (view is null)
        {
            return Error.NotFound("view", id);
        }

        var allowed = MayChange(view);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var applied = await ApplyAsync(view, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        view.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(view);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var view = await VisibleAsync(id, cancellationToken);
        if (view is null)
        {
            return Error.NotFound("view", id);
        }

        var allowed = MayChange(view);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        db.Views.Remove(view);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<SavedView?> VisibleAsync(Guid id, CancellationToken cancellationToken) =>
        Me is { } me ? await db.Views.SingleOrDefaultAsync(v => v.Id == id && (v.OwnerMembershipId == me || v.Shared), cancellationToken) : null;

    private Result MayChange(SavedView view) =>
        view.OwnerMembershipId == Me || principal.Required.Has(CollaborationPermissions.ViewManage)
            ? Result.Success()
            : Error.Forbidden("view.not_owner", "Only the owner, or a member with collaboration.view.manage, can change a shared view.");

    private async Task<Result> ApplyAsync(SavedView view, SaveViewRequest request, CancellationToken cancellationToken)
    {
        var type = request.EntityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type))
        {
            return Error.Validation("view.entity_invalid", "entityType is a lower-case name such as sales_invoice.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            return Error.Validation("view.name_invalid", "A name of up to 100 characters is required.");
        }

        var definition = request.Definition ?? JsonDocument.Parse("{}").RootElement;
        if (definition.ValueKind != JsonValueKind.Object || definition.EnumerateObject().Any(static p => !AllowedKeys.Contains(p.Name, StringComparer.Ordinal)) || definition.GetRawText().Length > MaxDefinitionBytes)
        {
            return Error.Validation("view.definition_invalid", "The definition is an object with filters, sort, columns, groupBy, pageSize or density, up to 16 KB.").WithWhy(("allowedKeys", AllowedKeys));
        }

        if (await db.Views.AnyAsync(v => v.EntityType == type && v.OwnerMembershipId == view.OwnerMembershipId && v.Name == name && v.Id != view.Id, cancellationToken))
        {
            return Error.Conflict("view.name_taken", $"You already have a view named '{name}' for {type}.");
        }

        if (request.IsDefault && !view.IsDefault)
        {
            foreach (var other in await db.Views.Where(v => v.EntityType == type && v.OwnerMembershipId == view.OwnerMembershipId && v.IsDefault && v.Id != view.Id).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
            }
        }

        view.EntityType = type;
        view.Name = name;
        view.Definition = definition.GetRawText();
        view.Shared = request.Shared;
        view.IsDefault = request.IsDefault;
        return Result.Success();
    }

    private static Error NoMembership() => Error.Forbidden("view.membership_required", "Only members have views.");

    private static SavedViewView Map(SavedView v) => new(v.Id, v.EntityType, v.Name, v.OwnerMembershipId, v.Shared, v.IsDefault, JsonDocument.Parse(v.Definition).RootElement.Clone(), v.CreatedAt, v.UpdatedAt);
}

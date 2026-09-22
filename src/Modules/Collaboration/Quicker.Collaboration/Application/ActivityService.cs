using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Collaboration.Application;

public sealed record ActivityView(Guid Id, string EntityType, Guid EntityId, string Kind, Guid? ActorMembershipId, IReadOnlyDictionary<string, string> Summary, JsonElement Data, DateTimeOffset CreatedAt);

/// <summary>The activity timeline of a record: written by every module through <see cref="IActivityLog"/>, read newest first.</summary>
public sealed class ActivityService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, IClock clock) : IActivityLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(ActivityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!EntityTypes.IsValid(entry.EntityType) || !ActivityKinds.IsValid(entry.Kind))
        {
            throw new ArgumentException($"'{entry.EntityType}'/'{entry.Kind}' is not a valid entity type and activity kind.", nameof(entry));
        }

        db.Activities.Add(new Activity
        {
            Id = Guid.CreateVersion7(),
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Kind = entry.Kind,
            ActorMembershipId = unitOfWork.Current.Context.MembershipId?.Value,
            Summary = entry.Summary,
            Data = entry.Data is null ? "{}" : JsonSerializer.Serialize(entry.Data, Json),
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Result<Page<ActivityView>>> ListAsync(string? entityType, Guid entityId, PageRequest page, CancellationToken cancellationToken)
    {
        var type = entityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type) || entityId == Guid.Empty)
        {
            return Error.Validation("activity.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        var paged = await KeysetPaging.ByIdDescendingAsync(db.Activities.Where(a => a.EntityType == type && a.EntityId == entityId), static a => a.Id, page, cancellationToken);
        return paged.IsSuccess
            ? paged.Value.Map(static a => new ActivityView(a.Id, a.EntityType, a.EntityId, a.Kind, a.ActorMembershipId, a.Summary.Values, JsonDocument.Parse(a.Data).RootElement.Clone(), a.CreatedAt))
            : paged.Error!;
    }
}

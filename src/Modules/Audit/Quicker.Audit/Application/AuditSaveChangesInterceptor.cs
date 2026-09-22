using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Audit.Application;

/// <summary>
/// Captures every insert, update and delete of an audited entity (see <see cref="AuditModelBuilderExtensions.HasAuditTrail{TEntity}"/>)
/// at SaveChanges time with before/after values and a field-level diff (ADR-0015). Bookkeeping properties are
/// ignored, redacted properties show up in the diff without their values.
/// </summary>
public sealed class AuditSaveChangesInterceptor(AuditWriter writer) : SaveChangesInterceptor
{
    private static readonly HashSet<string> ConventionIgnored = new(StringComparer.Ordinal) { "TenantId", "UpdatedAt", "RowVersion", "Xmin" };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Metadata.FindAnnotation(AuditAnnotations.EntityType)?.Value is not string entityType)
            {
                continue;
            }

            var action = entry.State switch
            {
                EntityState.Added => AuditActions.Created,
                EntityState.Modified => AuditActions.Updated,
                EntityState.Deleted => AuditActions.Deleted,
                _ => null,
            };
            if (action is null)
            {
                continue;
            }

            var properties = entry.Metadata.GetProperties()
                .Where(static p => !p.IsShadowProperty() && p.FindAnnotation(AuditAnnotations.Ignore)?.Value is not true && !ConventionIgnored.Contains(p.Name))
                .ToList();

            JsonObject? before = null;
            JsonObject? after = null;
            JsonObject? diff = null;
            switch (entry.State)
            {
                case EntityState.Added:
                    after = Snapshot(entry, properties, current: true);
                    break;
                case EntityState.Deleted:
                    before = Snapshot(entry, properties, current: false);
                    break;
                case EntityState.Modified:
                    (before, after, diff) = Changes(entry, properties);
                    if (diff.Count == 0)
                    {
                        continue; // only bookkeeping changed
                    }

                    break;
            }

            var id = EntityId(entry);
            var display = entry.Metadata.FindAnnotation(AuditAnnotations.Display)?.Value is Func<object, string> displayOf ? displayOf(entry.Entity) : id.ToString();
            writer.Capture(entityType, id, display, action, before, after, diff, CompanyId(entry));
        }
    }

    private static JsonObject Snapshot(EntityEntry entry, IEnumerable<IProperty> properties, bool current)
    {
        var snapshot = new JsonObject();
        foreach (var property in properties)
        {
            if (property.FindAnnotation(AuditAnnotations.Redact)?.Value is true)
            {
                continue;
            }

            var member = entry.Property(property.Name);
            snapshot[Name(property)] = AuditJson.ToNode(current ? member.CurrentValue : member.OriginalValue);
        }

        return snapshot;
    }

    private static (JsonObject Before, JsonObject After, JsonObject Diff) Changes(EntityEntry entry, IEnumerable<IProperty> properties)
    {
        var before = new JsonObject();
        var after = new JsonObject();
        var diff = new JsonObject();
        foreach (var property in properties)
        {
            var member = entry.Property(property.Name);
            if (property.GetValueComparer().Equals(member.OriginalValue, member.CurrentValue))
            {
                continue;
            }

            var name = Name(property);
            if (property.FindAnnotation(AuditAnnotations.Redact)?.Value is true)
            {
                diff[name] = new JsonObject { ["old"] = AuditAnnotations.RedactedValue, ["new"] = AuditAnnotations.RedactedValue };
                continue;
            }

            var oldValue = AuditJson.ToNode(member.OriginalValue);
            var newValue = AuditJson.ToNode(member.CurrentValue);
            before[name] = oldValue?.DeepClone();
            after[name] = newValue?.DeepClone();
            diff[name] = new JsonObject { ["old"] = oldValue, ["new"] = newValue };
        }

        return (before, after, diff);
    }

    private static string Name(IProperty property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static Guid EntityId(EntityEntry entry)
    {
        var idProperty = entry.Metadata.FindProperty("Id") ?? entry.Metadata.FindPrimaryKey()?.Properties[^1];
        var value = idProperty is null ? null : entry.Property(idProperty.Name).CurrentValue;
        return value switch
        {
            Guid guid => guid,
            IEntityId typed => typed.Value,
            _ => throw new InvalidOperationException($"Audited entity {entry.Metadata.ClrType.Name} needs a Guid 'Id' property."),
        };
    }

    private static Guid? CompanyId(EntityEntry entry)
    {
        var property = entry.Metadata.FindProperty("CompanyId");
        if (property is null)
        {
            return null;
        }

        return entry.Property(property.Name).CurrentValue switch
        {
            Guid guid => guid,
            IEntityId typed => typed.Value,
            _ => null,
        };
    }
}

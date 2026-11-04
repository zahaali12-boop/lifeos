using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Quicker.Persistence.EntityFramework;

/// <summary>
/// Model annotations the audit change-capture interceptor reads (ADR-0015). Modules declare which entities leave an
/// audit trail and which properties are ignored (bookkeeping) or redacted (secrets); the Audit module does the rest.
/// </summary>
public static class AuditAnnotations
{
    /// <summary>Entity-type annotation: the audit entity type string ("role", "customer").</summary>
    public const string EntityType = "Quicker:Audit:EntityType";

    /// <summary>Entity-type annotation: <c>Func&lt;object, string&gt;</c> producing the display text (number, code, email).</summary>
    public const string Display = "Quicker:Audit:Display";

    /// <summary>Property annotation (bool): never part of snapshots or diffs (counters, timestamps maintained by the system).</summary>
    public const string Ignore = "Quicker:Audit:Ignore";

    /// <summary>Property annotation (bool): a change is recorded, the value never is.</summary>
    public const string Redact = "Quicker:Audit:Redact";

    /// <summary>The placeholder stored instead of a redacted value.</summary>
    public const string RedactedValue = "[redacted]";
}

public static class AuditModelBuilderExtensions
{
    /// <summary>Marks an entity as audited: every insert, update and delete tracked by EF is captured with before/after values.</summary>
    public static EntityTypeBuilder<TEntity> HasAuditTrail<TEntity>(this EntityTypeBuilder<TEntity> builder, string entityType, Func<TEntity, string> display)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(display);
        builder.HasAnnotation(AuditAnnotations.EntityType, entityType);
        builder.HasAnnotation(AuditAnnotations.Display, (Func<object, string>)(entity => display((TEntity)entity)));
        return builder;
    }

    public static PropertyBuilder<TProperty> AuditIgnore<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(AuditAnnotations.Ignore, true);
    }

    public static PropertyBuilder<TProperty> AuditRedact<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(AuditAnnotations.Redact, true);
    }
}

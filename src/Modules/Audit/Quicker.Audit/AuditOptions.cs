namespace Quicker.Audit;

public sealed class AuditOptions
{
    public const string SectionName = "Quicker:Audit";

    public AnchoringOptions Anchoring { get; set; } = new();

    /// <summary>Largest page the explorer returns.</summary>
    public int PageMaxSize { get; set; } = 200;

    /// <summary>Largest synchronous export; bigger exports become jobs (M1.8).</summary>
    public int ExportMaxRows { get; set; } = 50_000;
}

public sealed class AnchoringOptions
{
    /// <summary>Anchor store: <c>file</c> (append-only local file, hash-linked) or <c>object_lock</c> (immutable objects in the configured object storage, S3 object lock in production).</summary>
    public string Store { get; set; } = "file";

    /// <summary>Directory of the file store.</summary>
    public string Path { get; set; } = ".data/audit-anchors";

    /// <summary>How long an object-lock anchor is retained (compliance mode: nobody can shorten it).</summary>
    public int RetentionDays { get; set; } = 3650;
}

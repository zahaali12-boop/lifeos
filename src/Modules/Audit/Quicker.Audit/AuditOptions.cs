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
    /// <summary>Anchor store: <c>file</c> (append-only local file, hash-linked). The object-lock store ships with the storage client in M1.8.</summary>
    public string Store { get; set; } = "file";

    /// <summary>Directory of the file store.</summary>
    public string Path { get; set; } = ".data/audit-anchors";
}

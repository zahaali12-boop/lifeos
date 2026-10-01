namespace Quicker.Persistence;

/// <summary>Connection strings per database role (ADR-0003, ADR-0004).</summary>
public sealed class DbOptions
{
    public const string SectionName = "Quicker:Db";

    /// <summary>Schema owner; used only by the migrator and maintenance jobs.</summary>
    public string OwnerConnection { get; set; } = "Host=localhost;Port=5432;Database=quicker;Username=quicker_owner;Password=quicker";

    /// <summary>Application role: no table ownership, no BYPASSRLS. Every request and job uses this.</summary>
    public string AppConnection { get; set; } = "Host=localhost;Port=5432;Database=quicker;Username=quicker_app;Password=quicker";

    /// <summary>Statement timeout for interactive work; jobs override per job type.</summary>
    public int StatementTimeoutSeconds { get; set; } = 15;

    public int LockTimeoutSeconds { get; set; } = 5;
}

namespace Quicker.Messaging;

/// <summary>Worker tuning (ADR-0010). Defaults suit a single node; scale by adding worker instances, not threads.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Quicker:Worker";

    /// <summary>The API host runs the dispatcher, job runner and scheduler in-process (single-node installs).</summary>
    public bool Embedded { get; set; }

    /// <summary>Fallback polling interval when no NOTIFY arrives.</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    public int OutboxBatchSize { get; set; } = 50;

    /// <summary>Attempts before an outbox message is dead-lettered.</summary>
    public int OutboxMaxAttempts { get; set; } = 20;

    /// <summary>Concurrent job executions per worker instance.</summary>
    public int JobConcurrency { get; set; } = 4;

    public int JobHeartbeatSeconds { get; set; } = 10;

    /// <summary>A running job whose heartbeat is older than this is reclaimed (the worker died).</summary>
    public int JobReclaimAfterSeconds { get; set; } = 120;

    /// <summary>Statement timeout applied to job units of work (jobs run longer than interactive requests).</summary>
    public int JobStatementTimeoutSeconds { get; set; } = 600;

    public int SchedulerTickSeconds { get; set; } = 30;

    /// <summary>Name reported in <c>locked_by</c>; defaults to machine name + process id.</summary>
    public string? InstanceName { get; set; }
}

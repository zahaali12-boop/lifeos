namespace Quicker.Api;

/// <summary>What the health endpoints answer; <c>migrations</c> is the count of applied schema versions when the database answers.</summary>
public sealed record HealthStatus(string Status, long? Migrations, string? Error = null);

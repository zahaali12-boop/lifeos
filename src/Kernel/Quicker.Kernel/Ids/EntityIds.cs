namespace Quicker.Kernel.Ids;

/// <summary>A strongly typed identifier backed by a UUIDv7 (ADR-0011).</summary>
public interface IEntityId
{
    Guid Value { get; }
}

/// <summary>Creates time-ordered UUIDv7 values; the single place identifiers are minted.</summary>
public static class Uuid7
{
    public static Guid New() => Guid.CreateVersion7();

    public static Guid NewAt(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}

public readonly record struct TenantId(Guid Value) : IEntityId
{
    public static TenantId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value) : IEntityId
{
    public static UserId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

public readonly record struct MembershipId(Guid Value) : IEntityId
{
    public static MembershipId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

public readonly record struct CompanyId(Guid Value) : IEntityId
{
    public static CompanyId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

public readonly record struct BranchId(Guid Value) : IEntityId
{
    public static BranchId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

public readonly record struct WarehouseId(Guid Value) : IEntityId
{
    public static WarehouseId New() => new(Uuid7.New());

    public override string ToString() => Value.ToString();
}

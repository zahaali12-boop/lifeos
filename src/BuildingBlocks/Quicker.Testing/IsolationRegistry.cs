using Npgsql;

namespace Quicker.Testing;

/// <summary>
/// Inserts one minimal, valid row for a tenant into a table, using an app-role connection whose transaction already
/// carries that tenant's session. Returns the new row's primary key value(s) as a WHERE fragment for follow-up checks.
/// </summary>
public delegate Task<RowRef> TenantRowFactory(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId);

/// <summary>Identifies an inserted row: table and a parameterless SQL predicate that selects exactly it.</summary>
public sealed record RowRef(string Table, string Predicate);

/// <summary>
/// Every tenant-scoped table must register a row factory here; the schema conventions test fails for any table in the
/// app or reporting schema without one, so the isolation suite (hard scenario 18) can never silently skip a table.
/// </summary>
public static class IsolationRegistry
{
    private static readonly Dictionary<string, TenantRowFactory> Factories = new(StringComparer.Ordinal);

    public static void Register(string qualifiedTable, TenantRowFactory factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Factories)
        {
            Factories[qualifiedTable] = factory;
        }
    }

    public static bool Has(string qualifiedTable)
    {
        lock (Factories)
        {
            return Factories.ContainsKey(qualifiedTable);
        }
    }

    public static IReadOnlyDictionary<string, TenantRowFactory> All()
    {
        lock (Factories)
        {
            return new Dictionary<string, TenantRowFactory>(Factories, StringComparer.Ordinal);
        }
    }
}

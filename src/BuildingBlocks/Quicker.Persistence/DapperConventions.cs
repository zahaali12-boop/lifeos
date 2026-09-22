using System.Runtime.CompilerServices;
using Dapper;

namespace Quicker.Persistence;

/// <summary>Process-wide Dapper settings: snake_case columns map to PascalCase members.</summary>
public static class DapperConventions
{
#pragma warning disable CA2255 // Library-wide convention: every host that loads this assembly gets the mapping.
    [ModuleInitializer]
    internal static void Initialize() => Apply();
#pragma warning restore CA2255

    public static void Apply()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }
}

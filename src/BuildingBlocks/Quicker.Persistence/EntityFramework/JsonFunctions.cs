using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace Quicker.Persistence.EntityFramework;

/// <summary>
/// Server-side JSON helpers usable in LINQ on jsonb columns mapped as strings. <see cref="Text"/> translates to
/// <c>jsonb_extract_path_text(column, key)</c>, the same expression the custom-field indexes are built on.
/// </summary>
public static class JsonFunctions
{
    public static string? Text(string json, string key) => throw new InvalidOperationException("JsonFunctions.Text is translated to SQL and cannot run in memory.");

    /// <summary>The SQL the custom-field indexer must use so a filter on <c>cf.key</c> hits the index.</summary>
    public static string IndexExpression(string column, string key) => $"jsonb_extract_path_text({column}, '{key}')";

    public static void Register(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDbFunction(() => Text(default!, default!))
            .HasTranslation(static args => new SqlFunctionExpression("jsonb_extract_path_text", args, nullable: true, argumentsPropagateNullability: [false, false], typeof(string), null));
    }
}

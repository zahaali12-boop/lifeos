using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;

namespace Quicker.Web;

/// <summary>One page of a list: the items and the opaque cursor of the next page (null on the last page).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public Page<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new Page<TOut>(Items.Select(map).ToList(), NextCursor);
    }
}

/// <summary>Query-string paging: <c>limit</c> (clamped to the endpoint's maximum) and the <c>cursor</c> returned by the previous page.</summary>
public sealed record PageRequest(int? Limit = null, string? Cursor = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public int Size(int max = MaxLimit) => Math.Clamp(Limit ?? DefaultLimit, 1, Math.Max(1, max));
}

/// <summary>Cursors are base64url JSON of the keyset position; opaque to clients, checked on the way back in.</summary>
public static class Cursor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Encode<TState>(TState state) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(state, Json));

    public static Result<TState> Decode<TState>(string cursor)
    {
        try
        {
            var bytes = FromBase64Url(cursor);
            var state = JsonSerializer.Deserialize<TState>(bytes, Json);
            return state is null ? Invalid() : state;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return Invalid();
        }

        static Error Invalid() => Error.Validation("page.cursor_invalid", "The cursor is not one this endpoint issued.");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Keyset paging on a version-7 id: ids are time-ordered, so "newest first, everything older than the cursor" is a
/// single indexed range scan with no offset. The cursor is the last id of the page.
/// </summary>
public static class KeysetPaging
{
    private sealed record IdCursor(Guid Id);

    public static async Task<Result<Page<T>>> ByIdDescendingAsync<T>(IQueryable<T> query, Expression<Func<T, Guid>> id, PageRequest page, CancellationToken cancellationToken, int maxLimit = PageRequest.MaxLimit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(page);
        var size = page.Size(maxLimit);
        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var decoded = Cursor.Decode<IdCursor>(page.Cursor);
            if (decoded.IsFailure)
            {
                return decoded.Error!;
            }

            // e => e.Id.CompareTo(cursor) < 0
            var compare = Expression.Call(id.Body, nameof(Guid.CompareTo), Type.EmptyTypes, Expression.Constant(decoded.Value.Id));
            var older = Expression.Lambda<Func<T, bool>>(Expression.LessThan(compare, Expression.Constant(0)), id.Parameters);
            query = query.Where(older);
        }

        var rows = await query.OrderByDescending(id).Take(size + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > size;
        var items = hasMore ? rows.Take(size).ToList() : rows;
        var selector = id.Compile();
        return new Page<T>(items, hasMore && items.Count > 0 ? Cursor.Encode(new IdCursor(selector(items[^1]))) : null);
    }

    /// <summary>The same contract for lists already in memory (small result sets from raw SQL).</summary>
    public static Result<Page<T>> ByIdDescending<T>(IEnumerable<T> ordered, Func<T, Guid> id, PageRequest page, int maxLimit = PageRequest.MaxLimit)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(page);
        var size = page.Size(maxLimit);
        var source = ordered.OrderByDescending(id);
        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var decoded = Cursor.Decode<IdCursor>(page.Cursor);
            if (decoded.IsFailure)
            {
                return decoded.Error!;
            }

            var after = decoded.Value.Id;
            source = source.Where(item => id(item).CompareTo(after) < 0).OrderByDescending(id);
        }

        var rows = source.Take(size + 1).ToList();
        var hasMore = rows.Count > size;
        var items = hasMore ? rows.Take(size).ToList() : rows;
        return new Page<T>(items, hasMore && items.Count > 0 ? Cursor.Encode(new IdCursor(id(items[^1]))) : null);
    }

}

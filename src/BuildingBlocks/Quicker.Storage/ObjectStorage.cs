namespace Quicker.Storage;

/// <summary>Settings of the object store (section <c>Quicker:Storage</c>).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Quicker:Storage";

    /// <summary><c>filesystem</c> (single node, tests) or <c>s3</c> (MinIO locally, any S3-compatible service in production).</summary>
    public string Provider { get; set; } = "filesystem";

    /// <summary>Root directory of the filesystem provider.</summary>
    public string Path { get; set; } = ".data/storage";

    /// <summary>S3 endpoint; empty for AWS itself.</summary>
    public string? Endpoint { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Bucket for ordinary objects (attachments, exports).</summary>
    public string Bucket { get; set; } = "quicker";

    /// <summary>Bucket with object lock for keys under <see cref="ObjectKeys.ImmutablePrefix"/> (audit anchors).</summary>
    public string ImmutableBucket { get; set; } = "quicker-immutable";

    /// <summary>Path-style addressing (MinIO); virtual-hosted style otherwise.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Largest upload the attachment endpoints accept.</summary>
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
}

/// <summary>An immutability request: the object cannot be replaced or deleted before the date (S3 object lock in compliance mode).</summary>
public sealed record ObjectRetention(DateTimeOffset RetainUntil);

/// <summary>What the store knows about a stored object. <see cref="VersionId"/> pins an exact version where the store versions (S3).</summary>
public sealed record StoredObjectInfo(string Key, long Length, string ContentType, DateTimeOffset? RetainUntil, string? VersionId, DateTimeOffset? StoredAt = null);

/// <summary>A stored object's metadata and content; dispose to release the content stream.</summary>
public sealed class StoredObject(StoredObjectInfo info, Stream content) : IAsyncDisposable
{
    public StoredObjectInfo Info { get; } = info;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Objects by key. Keys are slash-separated paths; keys under <see cref="ObjectKeys.ImmutablePrefix"/> must carry a
/// retention and are stored where the provider can enforce it (object-lock bucket, refusal to overwrite on disk).
/// </summary>
public interface IObjectStorage
{
    string Provider { get; }

    /// <summary>Stores the content (read to its end) and returns what was stored; replaces an existing object unless it is retained.</summary>
    Task<StoredObjectInfo> PutAsync(string key, Stream content, string contentType, ObjectRetention? retention = null, CancellationToken cancellationToken = default);

    /// <summary>The object, a specific version of it when the store versions and one is given, or null when absent.</summary>
    Task<StoredObject?> GetAsync(string key, string? versionId = null, CancellationToken cancellationToken = default);

    Task<StoredObjectInfo?> HeadAsync(string key, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the object; false when it did not exist. Throws <see cref="ObjectRetainedException"/> while a retention holds.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The current objects whose keys start with the prefix, in key order, each with when it was stored (for sweeps of objects nothing refers to).</summary>
    IAsyncEnumerable<StoredObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken = default);
}

/// <summary>The object is under retention and cannot be replaced or deleted yet.</summary>
public sealed class ObjectRetainedException(string key, DateTimeOffset retainUntil) : Exception($"Object '{key}' is retained until {retainUntil:O}.")
{
    public string Key { get; } = key;

    public DateTimeOffset RetainUntil { get; } = retainUntil;
}

public static class ObjectKeys
{
    /// <summary>Keys that must be written with a retention and live in the immutable store.</summary>
    public const string ImmutablePrefix = "immutable/";

    public const int MaxLength = 512;

    public static bool IsImmutable(string key) => key.StartsWith(ImmutablePrefix, StringComparison.Ordinal);

    /// <summary>Slash-separated segments of letters, digits, dot, dash and underscore; no empty, "." or ".." segments.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
        {
            return false;
        }

        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static void Validate(string key, ObjectRetention? retention)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid object key.", nameof(key));
        }

        if (IsImmutable(key) && retention is null)
        {
            throw new ArgumentException($"Objects under '{ImmutablePrefix}' must be stored with a retention.", nameof(retention));
        }
    }
}

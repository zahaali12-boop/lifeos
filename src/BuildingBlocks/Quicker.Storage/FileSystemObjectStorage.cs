using System.Runtime.CompilerServices;
using System.Text.Json;
using Quicker.Kernel.Time;

namespace Quicker.Storage;

/// <summary>
/// Objects as files under a root directory with a JSON sidecar per object (content type, length, retention).
/// Writes go to a temporary file and are moved into place, so a reader never sees a partial object. A retained
/// object is refused for replacement and deletion until its date: the single-node stand-in for object lock.
/// </summary>
public sealed class FileSystemObjectStorage(string root, IClock clock) : IObjectStorage
{
    private const string SidecarSuffix = ".meta.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Sidecar(string ContentType, long Length, DateTimeOffset? RetainUntil, DateTimeOffset? StoredAt = null);

    public string Provider => "filesystem";

    public string Root { get; } = Path.GetFullPath(root);

    public async Task<StoredObjectInfo> PutAsync(string key, Stream content, string contentType, ObjectRetention? retention = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ObjectKeys.Validate(key, retention);
        var path = PathFor(key);
        await ThrowIfRetainedAsync(key, path, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        long length;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
                length = file.Length;
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        var storedAt = clock.UtcNow;
        var sidecar = new Sidecar(contentType, length, retention?.RetainUntil, storedAt);
        await File.WriteAllTextAsync(path + SidecarSuffix, JsonSerializer.Serialize(sidecar, Json), cancellationToken);
        return new StoredObjectInfo(key, length, contentType, retention?.RetainUntil, null, storedAt);
    }

    public async Task<StoredObject?> GetAsync(string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        var info = await HeadAsync(key, versionId, cancellationToken);
        if (info is null)
        {
            return null;
        }

        var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new StoredObject(info, stream);
    }

    public async Task<StoredObjectInfo?> HeadAsync(string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        if (!ObjectKeys.IsValid(key) || versionId is not null)
        {
            return null; // this store has no versions: a version reference cannot be honoured
        }

        var path = PathFor(key);
        var sidecar = await ReadSidecarAsync(path, cancellationToken);
        return sidecar is null || !File.Exists(path) ? null : new StoredObjectInfo(key, sidecar.Length, sidecar.ContentType, sidecar.RetainUntil, null, sidecar.StoredAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!ObjectKeys.IsValid(key))
        {
            return false;
        }

        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return false;
        }

        await ThrowIfRetainedAsync(key, path, cancellationToken);
        File.Delete(path);
        if (File.Exists(path + SidecarSuffix))
        {
            File.Delete(path + SidecarSuffix);
        }

        return true;
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // Walk the deepest directory the prefix names fully, then keep the keys that start with the whole prefix.
        var directoryPart = prefix.Contains('/', StringComparison.Ordinal) ? prefix[..(prefix.LastIndexOf('/') + 1)] : string.Empty;
        var directory = directoryPart.Length == 0 ? Root : PathFor(directoryPart.TrimEnd('/'));
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var keys = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(static f => !f.EndsWith(SidecarSuffix, StringComparison.Ordinal) && !f.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && ObjectKeys.IsValid(k))
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var key in keys)
        {
            if (await HeadAsync(key, null, cancellationToken) is { } info)
            {
                yield return info;
            }
        }
    }

    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : throw new ArgumentException($"'{key}' escapes the storage root.", nameof(key));
    }

    private async Task ThrowIfRetainedAsync(string key, string path, CancellationToken cancellationToken)
    {
        var sidecar = await ReadSidecarAsync(path, cancellationToken);
        if (sidecar?.RetainUntil is { } until && until > clock.UtcNow)
        {
            throw new ObjectRetainedException(key, until);
        }
    }

    private static async Task<Sidecar?> ReadSidecarAsync(string path, CancellationToken cancellationToken)
    {
        var sidecarPath = path + SidecarSuffix;
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Sidecar>(await File.ReadAllTextAsync(sidecarPath, cancellationToken), Json);
    }
}

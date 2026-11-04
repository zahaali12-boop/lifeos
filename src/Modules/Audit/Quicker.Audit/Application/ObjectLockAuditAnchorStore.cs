using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quicker.Kernel.Time;
using Quicker.Storage;

namespace Quicker.Audit.Application;

/// <summary>
/// Anchors as immutable objects: one JSON document per head under the storage's immutable prefix, written with a
/// retention so the provider refuses to change or delete it (S3 object lock in compliance mode; the filesystem
/// provider refuses through its sidecar). The reference pins the exact version, so a later write under the same key
/// cannot shadow the anchored document; the receipt is the SHA-256 of the stored bytes.
/// </summary>
public sealed class ObjectLockAuditAnchorStore(IObjectStorage storage, IClock clock, int retentionDays) : IAuditAnchorStore
{
    private const string Prefix = ObjectKeys.ImmutablePrefix + "audit-anchors/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "object_lock";

    public async Task<AnchorReceipt> WriteAsync(AnchorRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var key = Prefix + (record.TenantId is { } tenant ? "tenant/" + tenant.ToString("N") : "platform") + "/" + record.Chain + "/" + record.Seq.ToString("D12", CultureInfo.InvariantCulture) + ".json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        using var content = new MemoryStream(bytes, writable: false);
        var stored = await storage.PutAsync(key, content, "application/json", new ObjectRetention(clock.UtcNow.AddDays(retentionDays)), cancellationToken);
        var reference = stored.VersionId is null ? key : key + "@" + stored.VersionId;
        return new AnchorReceipt(Name, reference, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public async Task<AnchorRecord?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        var key = at < 0 ? reference : reference[..at];
        var versionId = at < 0 ? null : reference[(at + 1)..];
        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        await using var stored = await storage.GetAsync(key, versionId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stored.Content.CopyToAsync(buffer, cancellationToken);
        return JsonSerializer.Deserialize<AnchorRecord>(Encoding.UTF8.GetString(buffer.ToArray()), Json);
    }
}

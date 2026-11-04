using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Storage;

namespace Quicker.Collaboration.Application;

public sealed record AttachmentSummary(Guid Id, string EntityType, Guid EntityId, string FileName, string ContentType, long SizeBytes, string Sha256, Guid? UploadedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Attachments on any record: metadata in the module's table, bytes in the object store under
/// <c>tenants/{tenant}/attachments/{id}</c>. Uploads are spooled to a temporary file while the size limit is
/// enforced and the SHA-256 computed, then stored; deletion removes the object just before the row's commit.
/// </summary>
public sealed class AttachmentService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, IObjectStorage storage, StorageOptions options, IActivityLog activities, IClock clock, RecordAccess access)
{
    public const int MaxFileNameLength = 255;

    public static string KeyFor(Guid tenantId, Guid attachmentId) => $"tenants/{tenantId:N}/attachments/{attachmentId:N}";

    public async Task<Result<AttachmentSummary>> UploadAsync(string? entityType, Guid entityId, string? fileName, string? contentType, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var type = entityType?.Trim() ?? string.Empty;
        if (!IsEntityType(type) || entityId == Guid.Empty)
        {
            return Error.Validation("attachment.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        if (await access.CheckAsync(type, entityId, cancellationToken) is { } withheld)
        {
            return withheld;
        }

        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (name.Length is 0 or > MaxFileNameLength)
        {
            return Error.Validation("attachment.file_name_invalid", $"A file name of up to {MaxFileNameLength} characters is required.");
        }

        var id = Guid.CreateVersion7();
        var tenantId = unitOfWork.Current.Context.TenantId.Value;
        await using var spool = new FileStream(Path.Combine(Path.GetTempPath(), "quicker-upload-" + id.ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            size += read;
            if (size > options.MaxUploadBytes)
            {
                return Error.Validation("attachment.too_large", $"Attachments are limited to {options.MaxUploadBytes} bytes.").WithWhy(("maxBytes", options.MaxUploadBytes));
            }

            hash.AppendData(buffer, 0, read);
            await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await spool.FlushAsync(cancellationToken);
        spool.Position = 0;
        var attachment = new Attachment
        {
            Id = id,
            EntityType = type,
            EntityId = entityId,
            FileName = name,
            ContentType = NormalizeContentType(contentType),
            SizeBytes = size,
            Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
            StorageKey = KeyFor(tenantId, id),
            UploadedBy = unitOfWork.Current.Context.MembershipId?.Value,
            CreatedAt = clock.UtcNow,
        };
        await storage.PutAsync(attachment.StorageKey, spool, attachment.ContentType, null, cancellationToken);
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        await activities.RecordAsync(new ActivityEntry(type, entityId, ActivityKinds.AttachmentAdded, LocalizedText.Bilingual($"Attached {name}", $"أُرفق {name}"), new { attachmentId = id, fileName = name, sizeBytes = size }), cancellationToken);
        return Map(attachment);
    }

    /// <summary>How long an upload may wait for its record: a file stored this recently may belong to a save still in flight.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes this workspace's attachment files that no attachment refers to. A file is stored before its record commits,
    /// so a save that fails afterwards leaves the file behind; files older than <see cref="OrphanGrace"/> without a record
    /// are deleted (retained ones are left). Returns how many were removed.
    /// </summary>
    public async Task<int> SweepOrphansAsync(CancellationToken cancellationToken)
    {
        var prefix = $"tenants/{unitOfWork.Current.Context.TenantId!.Value:N}/attachments/";
        var cutoff = clock.UtcNow - OrphanGrace;
        var candidates = new Dictionary<Guid, string>();
        await foreach (var stored in storage.ListAsync(prefix, cancellationToken))
        {
            if (stored.StoredAt is { } at && at < cutoff && Guid.TryParseExact(stored.Key[prefix.Length..], "N", out var id))
            {
                candidates[id] = stored.Key;
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Keys.ToList();
        var referenced = (await db.Attachments.Where(a => ids.Contains(a.Id)).Select(static a => a.Id).ToListAsync(cancellationToken)).ToHashSet();
        var removed = 0;
        foreach (var (id, key) in candidates.Where(c => !referenced.Contains(c.Key)))
        {
            try
            {
                removed += await storage.DeleteAsync(key, cancellationToken) ? 1 : 0;
            }
            catch (ObjectRetainedException)
            {
                // A retained object outlives its record by design; it goes when its retention ends.
            }
        }

        return removed;
    }

    public async Task<Result<IReadOnlyList<AttachmentSummary>>> ListAsync(string? entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var type = entityType?.Trim() ?? string.Empty;
        if (!IsEntityType(type) || entityId == Guid.Empty)
        {
            return Error.Validation("attachment.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        if (await access.CheckAsync(type, entityId, cancellationToken) is { } withheld)
        {
            return withheld;
        }

        var rows = await db.Attachments.Where(a => a.EntityType == type && a.EntityId == entityId).OrderBy(static a => a.CreatedAt).ThenBy(static a => a.Id).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<AttachmentSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return attachment is null || !await access.MayReadAsync(attachment.EntityType, attachment.EntityId, cancellationToken) ? null : Map(attachment);
    }

    /// <summary>The attachment and its content stream; the caller disposes the stream.</summary>
    public async Task<Result<(AttachmentSummary Attachment, StoredObject Content)>> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null || !await access.MayReadAsync(attachment.EntityType, attachment.EntityId, cancellationToken))
        {
            return Error.NotFound("attachment", id);
        }

        var stored = await storage.GetAsync(attachment.StorageKey, null, cancellationToken);
        if (stored is null)
        {
            return Error.Conflict("attachment.content_missing", "The attachment's content is missing from storage.").WithWhy(("storageKey", attachment.StorageKey));
        }

        return (Map(attachment), stored);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null || !await access.MayReadAsync(attachment.EntityType, attachment.EntityId, cancellationToken))
        {
            return Error.NotFound("attachment", id);
        }

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        await activities.RecordAsync(new ActivityEntry(attachment.EntityType, attachment.EntityId, ActivityKinds.AttachmentRemoved, LocalizedText.Bilingual($"Removed {attachment.FileName}", $"أُزيل {attachment.FileName}"), new { attachmentId = id, fileName = attachment.FileName }), cancellationToken);
        var key = attachment.StorageKey;
        unitOfWork.Current.BeforeCommit(ct => storage.DeleteAsync(key, ct));
        return Result.Success();
    }

    private static bool IsEntityType(string type) =>
        type.Length is > 0 and <= 64 && type.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.');

    private static string NormalizeContentType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.MediaType is { } media && media.Contains('/', StringComparison.Ordinal) ? media.ToLowerInvariant() : "application/octet-stream";

    private static AttachmentSummary Map(Attachment a) => new(a.Id, a.EntityType, a.EntityId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.UploadedBy, a.CreatedAt);
}

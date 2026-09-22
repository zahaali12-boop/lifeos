using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Quicker.Kernel.Time;

namespace Quicker.Storage;

/// <summary>
/// S3-compatible storage (MinIO locally). Ordinary keys go to the plain bucket; keys under the immutable prefix go
/// to the object-lock bucket in compliance mode, where a version can be neither changed nor deleted before its
/// retention date, and are read back by version id so a later write under the same key cannot shadow them.
/// Buckets are created on first use.
/// </summary>
public sealed class S3ObjectStorage(IAmazonS3 client, StorageOptions options, IClock clock) : IObjectStorage, IDisposable
{
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);

    public string Provider => "s3";

    public void Dispose()
    {
        _bucketGate.Dispose();
        client.Dispose();
    }

    public async Task<StoredObjectInfo> PutAsync(string key, Stream content, string contentType, ObjectRetention? retention = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ObjectKeys.Validate(key, retention);
        var bucket = await BucketForAsync(key, cancellationToken);
        if (!ObjectKeys.IsImmutable(key))
        {
            var existing = await HeadAsync(key, null, cancellationToken);
            if (existing?.RetainUntil is { } until && until > clock.UtcNow)
            {
                throw new ObjectRetainedException(key, until);
            }
        }

        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };
        if (retention is not null)
        {
            request.ObjectLockMode = ObjectLockMode.Compliance;
            request.ObjectLockRetainUntilDate = retention.RetainUntil.UtcDateTime;
        }

        var response = await client.PutObjectAsync(request, cancellationToken);
        var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = response.VersionId }, cancellationToken);
        return new StoredObjectInfo(key, head.ContentLength, contentType, retention?.RetainUntil, response.VersionId);
    }

    public async Task<StoredObject?> GetAsync(string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        if (!ObjectKeys.IsValid(key))
        {
            return null;
        }

        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = await BucketForAsync(key, cancellationToken), Key = key, VersionId = versionId }, cancellationToken);
            var info = new StoredObjectInfo(key, response.ContentLength, response.Headers.ContentType ?? "application/octet-stream", RetainUntil(response.ObjectLockRetainUntilDate), response.VersionId);
            return new StoredObject(info, response.ResponseStream);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredObjectInfo?> HeadAsync(string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        if (!ObjectKeys.IsValid(key))
        {
            return null;
        }

        try
        {
            var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = await BucketForAsync(key, cancellationToken), Key = key, VersionId = versionId }, cancellationToken);
            return new StoredObjectInfo(key, response.ContentLength, response.Headers.ContentType ?? "application/octet-stream", RetainUntil(response.ObjectLockRetainUntilDate), response.VersionId);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var head = await HeadAsync(key, null, cancellationToken);
        if (head is null)
        {
            return false;
        }

        if (head.RetainUntil is { } until && until > clock.UtcNow)
        {
            throw new ObjectRetainedException(key, until);
        }

        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = await BucketForAsync(key, cancellationToken), Key = key, VersionId = head.VersionId }, cancellationToken);
        return true;
    }

    private static DateTimeOffset? RetainUntil(DateTime? date) => date is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null;

    private async Task<string> BucketForAsync(string key, CancellationToken cancellationToken)
    {
        var immutable = ObjectKeys.IsImmutable(key);
        var bucket = immutable ? options.ImmutableBucket : options.Bucket;
        if (_ensured.Contains(bucket))
        {
            return bucket;
        }

        await _bucketGate.WaitAsync(cancellationToken);
        try
        {
            if (!_ensured.Contains(bucket))
            {
                if (!await AmazonS3Util.DoesS3BucketExistV2Async(client, bucket))
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = immutable, UseClientRegion = true }, cancellationToken);
                }

                _ensured.Add(bucket);
            }
        }
        finally
        {
            _bucketGate.Release();
        }

        return bucket;
    }
}

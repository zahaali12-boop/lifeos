using System.Net;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Identity.Application;

/// <summary>
/// API keys act as a member (default: the creator) with optional narrower scopes. Format:
/// <c>qk_&lt;tenant id, base64url&gt;.&lt;secret&gt;</c>; only the SHA-256 of the secret is stored.
/// </summary>
public sealed class ApiKeyService(IdentityDbContext db, IAuditSink audit, IClock clock)
{
    public const string Prefix = "qk_";

    public async Task<Result<ApiKeyCreatedResponse>> CreateAsync(Guid tenantId, Guid membershipId, Guid userId, CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error.Validation("apikey.name_required", "Give the key a name.");
        }

        var scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var invalid = scopes.Where(static s => !PermissionCatalog.IsValidGrant(s)).ToList();
        if (invalid.Count > 0)
        {
            return Error.Validation("apikey.scope_unknown", "Unknown permission keys in scopes.").WithWhy(("keys", invalid));
        }

        var allowlist = new List<IPAddress>();
        foreach (var ip in request.IpAllowlist ?? [])
        {
            if (!IPAddress.TryParse(ip, out var parsed))
            {
                return Error.Validation("apikey.ip_invalid", $"'{ip}' is not a valid IP address.");
            }

            allowlist.Add(parsed);
        }

        if (request.ExpiresAt is { } expires && expires <= clock.UtcNow)
        {
            return Error.Validation("apikey.expiry_past", "Expiry must be in the future.");
        }

        var secret = Tokens.NewUrlSafe(32);
        var key = $"{Prefix}{Base64Url(tenantId)}.{secret}";
        var entity = new ApiKey
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            Prefix = secret[..6],
            KeyHash = Tokens.Hash(secret),
            Scopes = scopes,
            IpAllowlist = allowlist.ToArray(),
            MembershipId = membershipId,
            ExpiresAt = request.ExpiresAt,
            CreatedBy = userId,
            CreatedAt = clock.UtcNow,
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken); // captured as "created"; the key hash is redacted
        return new ApiKeyCreatedResponse(entity.Id, entity.Name, key, entity.Prefix, entity.ExpiresAt);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var keys = await db.ApiKeys.OrderByDescending(static k => k.CreatedAt).ToListAsync(cancellationToken);
        return keys.Select(static k => new ApiKeySummary(k.Id, k.Name, k.Prefix, k.Scopes, k.ExpiresAt, k.LastUsedAt, k.CreatedAt, k.RevokedAt)).ToList();
    }

    public async Task<Result> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
        {
            return Error.NotFound("api_key", id);
        }

        key.RevokedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("api_key", key.Id, key.Name, AuditActions.Revoked), cancellationToken); // inherits the captured diff
        return Result.Success();
    }

    /// <summary>Splits a presented key into tenant id and secret; null when malformed.</summary>
    public static (Guid TenantId, string Secret)? Parse(string presented)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var dot = presented.IndexOf('.', StringComparison.Ordinal);
        if (dot <= Prefix.Length)
        {
            return null;
        }

        try
        {
            var tenantBytes = Convert.FromBase64String(presented[Prefix.Length..dot].Replace('-', '+').Replace('_', '/') + "==");
            return tenantBytes.Length == 16 ? (new Guid(tenantBytes), presented[(dot + 1)..]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

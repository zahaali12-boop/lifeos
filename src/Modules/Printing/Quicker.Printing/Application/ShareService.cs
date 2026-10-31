using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Printing.Domain;
using Quicker.Printing.Persistence;

namespace Quicker.Printing.Application;

/// <summary>
/// Share links (roadmap 5.9, ADR-0022 amendment): a ready render opened by anyone holding the link, for WhatsApp
/// and the like. The token carries the tenant (an anonymous request has none) and 32 random bytes; only its hash is
/// stored, so a leaked database gives no working links. Links expire, can be revoked, and count their openings.
/// </summary>
public sealed class ShareService(
    PrintingDbContext db,
    PrintService prints,
    PrintOptions options,
    IActivityLog activities,
    IAuditSink audit,
    ICurrentPrincipal principal,
    IUnitOfWorkAccessor unitOfWork,
    IClock clock)
{
    public const string ChannelLink = "link";
    public const string ChannelWhatsApp = "whatsapp";
    private const int TokenRandomBytes = 32;
    private const int TenantBytes = 16;

    public async Task<Result<PrintShareSummary>> ShareAsync(PrintShareRequest request, string requestOrigin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = string.IsNullOrWhiteSpace(request.Channel) ? ChannelLink : request.Channel.Trim().ToLowerInvariant();
        if (channel is not (ChannelLink or ChannelWhatsApp))
        {
            return Error.Validation("print.share_channel_unknown", "A link is shared as a plain link or by WhatsApp.").WithWhy(("channel", request.Channel));
        }

        if (request.ExpiresInDays < 1 || request.ExpiresInDays > options.ShareMaxDays)
        {
            return Error.Validation("print.share_expiry_invalid", $"A link lives between 1 and {options.ShareMaxDays} days.").WithWhy(("expiresInDays", request.ExpiresInDays), ("max", options.ShareMaxDays));
        }

        var recipient = NormalisePhone(request.Recipient);
        if (!string.IsNullOrWhiteSpace(request.Recipient) && recipient is null)
        {
            return Error.Validation("print.share_phone_invalid", "The phone number needs its country code and at least eight digits.").WithWhy(("recipient", request.Recipient));
        }

        var render = await db.Renders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RenderId, cancellationToken);
        if (render is null)
        {
            return Error.NotFound("print_render", request.RenderId);
        }

        if (render.Status != "ready" || render.StorageKey is null)
        {
            return Error.Conflict("print.render_not_ready", render.Status == "failed" ? "The render failed." : "The render is not ready yet.").WithWhy(("status", render.Status), ("error", render.Error));
        }

        var tenant = unitOfWork.Current.Context.TenantId.Value;
        var token = NewToken(tenant);
        var share = new PrintShareLink
        {
            Id = Guid.CreateVersion7(),
            CompanyId = render.CompanyId,
            RenderId = render.Id,
            DocumentType = render.DocumentType,
            DocumentId = render.DocumentId,
            TokenHash = Hash(token),
            Channel = channel,
            Recipient = recipient,
            ExpiresAt = clock.UtcNow.AddDays(request.ExpiresInDays),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
        };
        db.Shares.Add(share);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("print_share", share.Id, render.Number, AuditActions.Created, After: new { share.DocumentType, share.DocumentId, share.RenderId, share.Channel, share.Recipient, share.ExpiresAt }, CompanyId: share.CompanyId), cancellationToken);
        if (render.DocumentId is { } document)
        {
            var by = channel == ChannelWhatsApp ? "by WhatsApp" : "by link";
            var byAr = channel == ChannelWhatsApp ? "عبر واتساب" : "برابط";
            await activities.RecordAsync(new ActivityEntry(render.DocumentType, document, PrintActivityKinds.Shared,
                LocalizedText.Bilingual($"{render.Title} {render.Number} shared {by}", $"شورك {render.Title} {render.Number} {byAr}"),
                new { shareId = share.Id, share.Channel, share.Recipient, share.ExpiresAt }), cancellationToken);
        }

        return Map(share, render.Number, Url(requestOrigin, token), clock.UtcNow);
    }

    public async Task<IReadOnlyList<PrintShareSummary>> ListAsync(string documentType, Guid? documentId, CancellationToken cancellationToken)
    {
        var query = db.Shares.AsNoTracking().Where(s => s.DocumentType == documentType);
        query = documentId is { } id ? query.Where(s => s.DocumentId == id) : query;
        var shares = await query.OrderByDescending(static s => s.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var renderIds = shares.Select(static s => s.RenderId).Distinct().ToList();
        var numbers = await db.Renders.AsNoTracking().Where(r => renderIds.Contains(r.Id)).Select(static r => new { r.Id, r.Number }).ToDictionaryAsync(static r => r.Id, static r => r.Number, cancellationToken);
        var now = clock.UtcNow;
        return shares.Select(s => Map(s, numbers.GetValueOrDefault(s.RenderId, string.Empty), null, now)).ToList();
    }

    public async Task<Result<PrintShareSummary>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var share = await db.Shares.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (share is null)
        {
            return Error.NotFound("print_share", id);
        }

        if (share.RevokedAt is null)
        {
            share.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry("print_share", share.Id, share.Channel, AuditActions.Updated, After: new { share.RevokedAt, share.OpenCount }, CompanyId: share.CompanyId), cancellationToken);
        }

        var number = await db.Renders.AsNoTracking().Where(r => r.Id == share.RenderId).Select(static r => r.Number).SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        return Map(share, number, null, clock.UtcNow);
    }

    /// <summary>
    /// Opens a link for an anonymous caller: the tenant comes from the token (the request has none), the link must be
    /// live, and the opening is counted before the PDF streams. Unknown, tampered and revoked links read the same (not found).
    /// </summary>
    public async Task<Result<PrintFile>> OpenAsync(string token, CancellationToken cancellationToken)
    {
        var tenant = TenantOf(token);
        if (tenant is null)
        {
            return NotFound();
        }

        await unitOfWork.Current.SwitchTenantAsync(new TenantId(tenant.Value), cancellationToken: cancellationToken);
        var hash = Hash(token);
        var share = await db.Shares.SingleOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
        if (share is null || share.RevokedAt is not null)
        {
            return NotFound();
        }

        if (share.ExpiresAt <= clock.UtcNow)
        {
            return Error.Conflict("print.share_expired", "This link has expired; ask for a new one.").WithWhy(("expiredAt", share.ExpiresAt));
        }

        share.OpenCount++;
        share.LastOpenedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await prints.OpenAsync(share.RenderId, cancellationToken);
    }

    /// <summary>Digits only, international: a leading 00 or + dropped; null when nothing usable was given.</summary>
    public static string? NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length is >= 8 and <= 15 ? digits : null;
    }

    private static Error NotFound() => new("print.share_not_found", "No such link.", Kind: ErrorKind.NotFound);

    private static string NewToken(Guid tenant)
    {
        var bytes = new byte[TenantBytes + TokenRandomBytes];
        tenant.TryWriteBytes(bytes.AsSpan(0, TenantBytes));
        RandomNumberGenerator.Fill(bytes.AsSpan(TenantBytes));
        return Base64UrlEncode(bytes);
    }

    private static Guid? TenantOf(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length != TenantBytes + TokenRandomBytes)
        {
            return null;
        }

        var tenant = new Guid(bytes.AsSpan(0, TenantBytes));
        return tenant == Guid.Empty ? null : tenant;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private string Url(string requestOrigin, string token)
    {
        var origin = string.IsNullOrWhiteSpace(options.PublicOrigin) ? requestOrigin : options.PublicOrigin;
        return $"{origin.TrimEnd('/')}/api/v1/public/prints/{token}";
    }

    private static PrintShareSummary Map(PrintShareLink s, string number, string? url, DateTimeOffset now) =>
        new(s.Id, s.RenderId, s.DocumentType, s.DocumentId, number, s.Channel, s.Recipient, url, s.ExpiresAt, s.RevokedAt, s.OpenCount, s.LastOpenedAt, s.CreatedAt, s.RevokedAt is null && s.ExpiresAt > now);
}

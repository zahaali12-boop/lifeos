using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quicker.Identity.Contracts;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;
using Quicker.Web;

namespace Quicker.Identity.Application;

/// <summary>
/// Builds the <see cref="Principal"/> for a request from its claims: verifies the session is still live, loads
/// membership and user, and takes the effective grants from a cache keyed by (membership, permissions epoch).
/// A permission change bumps the epoch, so the next request recomputes; revocation of a session is immediate.
/// </summary>
public sealed class PrincipalResolver(IdentityDbContext db, RoleService roles, ITenantDirectory tenants, IMemoryCache cache, IClock clock) : IPrincipalResolver, IStepUpPolicy
{
    private sealed record CachedGrants(IReadOnlyList<Grant> Grants, IReadOnlyList<FieldRule> FieldRules, IReadOnlyList<DocumentTypeRule> DocumentTypeRules, IReadOnlyList<Guid> RoleIds);

    public async Task<Principal?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var claims = httpContext.User;
        if (!Guid.TryParse(claims.FindFirst(QuickerClaims.Tenant)?.Value, out var tenantId)
            || !Guid.TryParse(claims.FindFirst(QuickerClaims.Membership)?.Value, out var membershipId)
            || !Guid.TryParse(claims.FindFirst(QuickerClaims.User)?.Value, out var userId))
        {
            return null;
        }

        var kind = claims.FindFirst(QuickerClaims.TokenKind)?.Value;
        var now = clock.UtcNow;
        DateTimeOffset authTime;
        var amr = claims.FindFirst(QuickerClaims.Amr)?.Value ?? "pwd";

        if (kind == "access")
        {
            if (!Guid.TryParse(claims.FindFirst(QuickerClaims.Session)?.Value, out var sessionId))
            {
                return null;
            }

            var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null || !session.IsUsable(now))
            {
                return null;
            }

            authTime = session.AuthTime;
            amr = session.Amr;
            if (now - session.LastUsedAt > TimeSpan.FromMinutes(1))
            {
                await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(static x => x.LastUsedAt, now), cancellationToken);
            }
        }
        else if (kind == "api_key")
        {
            authTime = now;
            amr = "api_key";
        }
        else
        {
            return null;
        }

        var membership = await db.Memberships.AsNoTracking().Include(static m => m.User).SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == tenantId, cancellationToken);
        if (membership is null || !membership.IsActive || membership.UserId != userId || !membership.User.IsActive)
        {
            return null;
        }

        var tenant = await tenants.FindByIdAsync(new TenantId(tenantId), cancellationToken);
        if (tenant is null || tenant.Status != "active")
        {
            return null;
        }

        // A member's session is accepted only from the workspace's allowed networks; API keys carry their own list.
        if (kind != "api_key" && !IpAllowlists.Allows(tenant.Policy.IpAllowlist, httpContext.Connection.RemoteIpAddress))
        {
            return null;
        }

        var cacheKey = $"grants:{membershipId}:{tenant.PermissionsEpoch}";
        var cached = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var (grants, fieldRules, documentRules, roleIds) = await roles.EffectiveAsync(membershipId, cancellationToken);
            return new CachedGrants(grants, fieldRules, documentRules, roleIds);
        }) ?? new CachedGrants([], [], [], []);

        var grants = cached.Grants;
        if (kind == "api_key")
        {
            var scopes = claims.FindAll("scope").Select(static c => c.Value).ToList();
            if (scopes.Count > 0)
            {
                // A key may only narrow the member's grants, never widen them.
                grants = grants.Where(g => scopes.Any(s => PermissionCatalog.Covers(s, g.Permission) || PermissionCatalog.Covers(g.Permission, s)))
                    .Select(g => scopes.Any(s => PermissionCatalog.Covers(g.Permission, s) && !PermissionCatalog.Covers(s, g.Permission)) ? g with { Permission = scopes.First(s => PermissionCatalog.Covers(g.Permission, s)) } : g)
                    .ToList();
            }
        }

        var isOwner = membership.IsOwner && kind == "access";
        return new Principal(
            new TenantId(tenantId), new UserId(userId), new MembershipId(membershipId),
            membership.User.Email, membership.User.DisplayName, membership.User.Locale,
            isOwner, membership.User.IsPlatformOperator,
            kind == "api_key" ? TenantContext.ActorApiKey : TenantContext.ActorUser,
            grants, cached.FieldRules, cached.DocumentTypeRules, authTime, amr, cached.RoleIds);
    }

    public async Task<bool> IsRecentAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.ActorType == TenantContext.ActorApiKey)
        {
            return false; // step-up actions are for people
        }

        var tenant = await tenants.FindByIdAsync(principal.TenantId, cancellationToken);
        var window = TimeSpan.FromMinutes(tenant?.Policy.StepUpWindowMinutes ?? 5);
        return clock.UtcNow - principal.AuthTime <= window;
    }
}

/// <summary>Authenticates <c>X-Api-Key</c> / <c>Authorization: ApiKey ...</c> requests into the same claims shape as JWTs.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IUnitOfWorkFactory unitOfWorkFactory,
    IClock clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers["X-Api-Key"].FirstOrDefault();
        if (presented is null && Request.Headers.Authorization.FirstOrDefault() is { } header && header.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
        {
            presented = header["ApiKey ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var parsed = ApiKeyService.Parse(presented);
        if (parsed is null)
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var (tenantId, secret) = parsed.Value;
        var hash = Tokens.Hash(secret);

        // Keys live under RLS, so look them up in a short tenant-scoped transaction of their own.
        await using var uow = await unitOfWorkFactory.BeginAsync(TenantContext.System(new TenantId(tenantId), Context.TraceIdentifier), cancellationToken: Context.RequestAborted);
        var row = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<(Guid Id, Guid? MembershipId, string[] Scopes, System.Net.IPAddress[] IpAllowlist, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)>(
            uow.Connection,
            "SELECT id, membership_id, scopes, ip_allowlist, expires_at, revoked_at FROM app.idn_api_keys WHERE key_hash = @hash",
            new { hash }, uow.Transaction);

        var now = clock.UtcNow;
        if (row.Id == Guid.Empty || row.RevokedAt is not null || (row.ExpiresAt is { } exp && exp <= now) || row.MembershipId is null)
        {
            return AuthenticateResult.Fail("Unknown, revoked or expired API key.");
        }

        var remote = Context.Connection.RemoteIpAddress;
        if (row.IpAllowlist.Length > 0 && (remote is null || !row.IpAllowlist.Any(ip => ip.Equals(remote) || ip.Equals(remote.MapToIPv4()))))
        {
            return AuthenticateResult.Fail("API key not allowed from this address.");
        }

        var membership = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<(Guid UserId, string Status)>(
            uow.Connection, "SELECT user_id, status FROM control.tenant_memberships WHERE id = @id", new { id = row.MembershipId }, uow.Transaction);
        if (membership.Status != "active")
        {
            return AuthenticateResult.Fail("The API key's member is not active.");
        }

        await Dapper.SqlMapper.ExecuteAsync(uow.Connection, "UPDATE app.idn_api_keys SET last_used_at = @now WHERE id = @id", new { now, id = row.Id }, uow.Transaction);
        await uow.CommitAsync(Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(QuickerClaims.User, membership.UserId.ToString()),
            new(QuickerClaims.Tenant, tenantId.ToString()),
            new(QuickerClaims.Membership, row.MembershipId.Value.ToString()),
            new(QuickerClaims.TokenKind, "api_key"),
            new(QuickerClaims.AuthTime, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(QuickerClaims.Amr, "api_key"),
        };
        claims.AddRange(row.Scopes.Select(static s => new Claim("scope", s)));
        var identity = new ClaimsIdentity(claims, SchemeName, QuickerClaims.User, "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

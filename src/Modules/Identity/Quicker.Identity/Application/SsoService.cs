using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Identity.Application;

/// <summary>
/// OIDC authorization-code flow with PKCE against a tenant's identity provider: discovery, redirect, code exchange,
/// id_token validation, just-in-time membership provisioning by email domain and group-to-role mapping. Ends with a
/// one-time login code the SPA exchanges for tokens, so tokens never travel in a URL.
/// </summary>
public sealed class SsoService(
    IdentityDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ITenantDirectory tenants,
    RoleService roles,
    AuthService auth,
    SecretProtector protector,
    AuthOptions options,
    IHttpClientFactory httpClientFactory,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------------ admin

    public async Task<IReadOnlyList<SsoConnectionSummary>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connections = await db.SsoConnections.Where(c => c.TenantId == tenantId).OrderBy(static c => c.Code).ToListAsync(cancellationToken);
        return connections.Select(Map).ToList();
    }

    public async Task<Result<SsoConnectionSummary>> SaveAsync(Guid tenantId, Guid? id, SaveSsoConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || !request.Code.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return Error.Validation("sso.code_invalid", "Code must be lower-case letters, digits and hyphens.");
        }

        if (!Uri.TryCreate(request.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps && !authority.IsLoopback)
        {
            return Error.Validation("sso.authority_invalid", "Authority must be an https URL.");
        }

        var connection = id is { } cid ? await db.SsoConnections.SingleOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId, cancellationToken) : null;
        if (id is not null && connection is null)
        {
            return Error.NotFound("sso_connection", id);
        }

        if (connection is null)
        {
            if (await db.SsoConnections.AnyAsync(c => c.TenantId == tenantId && c.Code == request.Code, cancellationToken))
            {
                return Error.Conflict("sso.code_taken", "A connection with this code exists.");
            }

            connection = new SsoConnection { Id = Guid.CreateVersion7(), TenantId = tenantId, Code = request.Code, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
            db.SsoConnections.Add(connection);
        }

        connection.DisplayName = request.DisplayName.Trim();
        connection.Authority = request.Authority.TrimEnd('/');
        connection.ClientId = request.ClientId.Trim();
        if (!string.IsNullOrEmpty(request.ClientSecret))
        {
            connection.ClientSecretEnc = protector.Protect(Encoding.UTF8.GetBytes(request.ClientSecret));
        }

        connection.Scopes = string.IsNullOrWhiteSpace(request.Scopes) ? "openid profile email" : request.Scopes.Trim();
        connection.EmailDomains = request.EmailDomains.Select(static d => d.Trim().ToLowerInvariant()).Where(static d => d.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        connection.JitProvisioning = request.JitProvisioning;
        connection.GroupClaim = string.IsNullOrWhiteSpace(request.GroupClaim) ? null : request.GroupClaim.Trim();
        connection.GroupRoleMap = JsonSerializer.Serialize(request.GroupRoleMap ?? new Dictionary<string, Guid>(StringComparer.Ordinal), Json);
        connection.IsActive = request.IsActive;
        connection.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken); // captured as created/updated; the client secret is redacted
        return Map(connection);
    }

    public async Task<Result> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var connection = await db.SsoConnections.SingleOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, cancellationToken);
        if (connection is null)
        {
            return Error.NotFound("sso_connection", id);
        }

        db.SsoConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken); // captured as "deleted"
        return Result.Success();
    }

    // ------------------------------------------------------------------ flow

    public async Task<Result<SsoStartResponse>> StartAsync(string tenantSlug, string connectionCode, string? returnTo, CancellationToken cancellationToken)
    {
        var tenant = await tenants.FindBySlugAsync(tenantSlug, cancellationToken);
        var connection = tenant is null ? null : await db.SsoConnections.SingleOrDefaultAsync(c => c.TenantId == tenant.Id.Value && c.Code == connectionCode && c.IsActive, cancellationToken);
        if (tenant is null || connection is null)
        {
            return Error.NotFound("sso_connection", connectionCode);
        }

        var configuration = await DiscoverAsync(connection, cancellationToken);
        var state = Tokens.NewUrlSafe(24);
        var nonce = Tokens.NewUrlSafe(24);
        var verifier = Tokens.NewUrlSafe(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        db.OneTimeTokens.Add(new OneTimeToken
        {
            Id = Guid.CreateVersion7(),
            Kind = "oidc_transaction",
            TenantId = tenant.Id.Value,
            TokenHash = Tokens.Hash(state),
            Payload = JsonSerializer.Serialize(new { connectionId = connection.Id, nonce, verifier, returnTo = SafeReturnTo(returnTo) }, Json),
            ExpiresAt = clock.UtcNow.Add(TransactionLifetime),
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        var url = $"{configuration.AuthorizationEndpoint}?response_type=code&client_id={Uri.EscapeDataString(connection.ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(connection.Scopes)}" +
                  $"&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method=S256";
        return new SsoStartResponse(url);
    }

    /// <summary>Handles the provider's redirect; returns the SPA URL carrying a one-time login code.</summary>
    public async Task<Result<string>> CallbackAsync(string? code, string? state, string? error, ClientInfo client, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return Error.Forbidden("sso.provider_error", $"The identity provider returned '{error}'.");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Error.Validation("sso.callback_invalid", "Missing code or state.");
        }

        var transaction = await db.OneTimeTokens.SingleOrDefaultAsync(t => t.TokenHash == Tokens.Hash(state) && t.Kind == "oidc_transaction", cancellationToken);
        if (transaction is null || !transaction.IsUsable(clock.UtcNow) || transaction.TenantId is null)
        {
            return Error.Validation("sso.state_invalid", "The sign-in attempt has expired; start again.");
        }

        transaction.ConsumedAt = clock.UtcNow;
        using var payload = JsonDocument.Parse(transaction.Payload);
        var connectionId = payload.RootElement.GetProperty("connectionId").GetGuid();
        var nonce = payload.RootElement.GetProperty("nonce").GetString()!;
        var verifier = payload.RootElement.GetProperty("verifier").GetString()!;
        var returnTo = payload.RootElement.GetProperty("returnTo").GetString() ?? "/";

        var connection = await db.SsoConnections.SingleAsync(c => c.Id == connectionId, cancellationToken);
        var tenant = (await tenants.FindByIdAsync(new TenantId(transaction.TenantId.Value), cancellationToken))!;
        var configuration = await DiscoverAsync(connection, cancellationToken);

        // Exchange the code.
        var http = httpClientFactory.CreateClient("oidc");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = connection.ClientId,
            ["code_verifier"] = verifier,
        };
        if (connection.ClientSecretEnc is not null)
        {
            form["client_secret"] = Encoding.UTF8.GetString(protector.Unprotect(connection.ClientSecretEnc));
        }

        using var tokenResponse = await http.PostAsync(new Uri(configuration.TokenEndpoint), new FormUrlEncodedContent(form), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return Error.Forbidden("sso.token_exchange_failed", "The identity provider rejected the sign-in.");
        }

        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        var idToken = tokenJson.RootElement.TryGetProperty("id_token", out var idTokenElement) ? idTokenElement.GetString() : null;
        if (string.IsNullOrEmpty(idToken))
        {
            return Error.Forbidden("sso.id_token_missing", "The identity provider did not return an id_token.");
        }

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        System.Security.Claims.ClaimsPrincipal identity;
        try
        {
            identity = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidIssuer = configuration.Issuer,
                ValidAudience = connection.ClientId,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            }, out _);
        }
        catch (SecurityTokenException ex)
        {
            return Error.Forbidden("sso.id_token_invalid", ex.Message);
        }

        if (identity.FindFirst("nonce")?.Value != nonce)
        {
            return Error.Forbidden("sso.nonce_mismatch", "The sign-in response does not match the request.");
        }

        var email = Emails.Normalize(identity.FindFirst("email")?.Value ?? string.Empty);
        var emailVerified = identity.FindFirst("email_verified")?.Value is "true" or "True";
        var name = identity.FindFirst("name")?.Value ?? identity.FindFirst("preferred_username")?.Value ?? email;
        if (string.IsNullOrEmpty(email))
        {
            return Error.Forbidden("sso.email_missing", "The identity provider did not return an email address.");
        }

        var domain = email.Split('@').Last();
        if (connection.EmailDomains.Length > 0 && !connection.EmailDomains.Contains(domain, StringComparer.Ordinal))
        {
            return Error.Forbidden("sso.domain_not_allowed", "This email domain is not allowed for this connection.");
        }

        // Find or provision.
        var now = clock.UtcNow;
        var user = await db.Users.Include(static u => u.MfaMethods).Include(static u => u.Memberships).SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        var membership = user?.Memberships.SingleOrDefault(m => m.TenantId == tenant.Id.Value);
        if (user is null || membership is null)
        {
            if (!connection.JitProvisioning)
            {
                return Error.Forbidden("sso.not_a_member", "You are not a member of this workspace; ask an administrator for an invitation.");
            }

            if (user is null)
            {
                user = new User { Id = Guid.CreateVersion7(), Email = email, DisplayName = name ?? email, Locale = tenant.DefaultLanguage, EmailVerifiedAt = emailVerified ? now : null, CreatedAt = now, UpdatedAt = now };
                db.Users.Add(user);
            }

            membership = new TenantMembership { Id = Guid.CreateVersion7(), TenantId = tenant.Id.Value, UserId = user.Id, Status = "active", AcceptedAt = now, CreatedAt = now, UpdatedAt = now, User = user };
            user.Memberships.Add(membership);
            db.Memberships.Add(membership);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!membership.IsActive)
        {
            return Error.Forbidden("sso.membership_disabled", "Your membership is disabled.");
        }

        if (!user.IsActive)
        {
            return Error.Forbidden("auth.account_disabled", "This account is disabled.");
        }

        // Group → role mapping applies inside the tenant.
        await unitOfWork.Current.SwitchTenantAsync(tenant.Id, new UserId(user.Id), new MembershipId(membership.Id), user.Email, cancellationToken);
        if (connection.GroupClaim is { } groupClaim)
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Guid>>(connection.GroupRoleMap, Json) ?? new Dictionary<string, Guid>(StringComparer.Ordinal);
            var groups = identity.FindAll(groupClaim).Select(static c => c.Value).ToHashSet(StringComparer.Ordinal);
            var current = (await roles.MemberAsync(membership.Id, cancellationToken))?.Assignments ?? [];
            foreach (var (group, roleId) in map)
            {
                if (groups.Contains(group) && current.All(a => a.RoleId != roleId))
                {
                    await roles.AssignAsync(membership.Id, new AssignRoleRequest(roleId, AcknowledgeWarnings: true), user.Id, cancellationToken);
                }
            }
        }

        var tokens = await auth.IssueSessionAsync(user, membership, tenant, client, "sso", now, cancellationToken);

        // Hand the SPA a one-time code instead of tokens in the URL.
        var loginCode = Tokens.NewUrlSafe(32);
        db.OneTimeTokens.Add(new OneTimeToken
        {
            Id = Guid.CreateVersion7(),
            Kind = "step_up",
            UserId = user.Id,
            TenantId = tenant.Id.Value,
            TokenHash = Tokens.Hash(loginCode),
            Payload = JsonSerializer.Serialize(new { purpose = "sso_exchange", tokens }, Json),
            ExpiresAt = now.AddMinutes(2),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"{options.PublicOrigin}{returnTo}{(returnTo.Contains('?', StringComparison.Ordinal) ? "&" : "?")}sso_code={loginCode}";
    }

    public async Task<Result<TokenResponse>> ExchangeAsync(string loginCode, CancellationToken cancellationToken)
    {
        var token = await db.OneTimeTokens.SingleOrDefaultAsync(t => t.TokenHash == Tokens.Hash(loginCode) && t.Kind == "step_up", cancellationToken);
        if (token is null || !token.IsUsable(clock.UtcNow))
        {
            return Error.Validation("sso.code_invalid", "The sign-in code is not valid or has expired.");
        }

        token.ConsumedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        using var payload = JsonDocument.Parse(token.Payload);
        return JsonSerializer.Deserialize<TokenResponse>(payload.RootElement.GetProperty("tokens").GetRawText(), Json)!;
    }

    private string RedirectUri => $"{options.ApiOrigin}/api/v1/auth/sso/callback";

    private async Task<OpenIdConnectConfiguration> DiscoverAsync(SsoConnection connection, CancellationToken cancellationToken)
    {
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{connection.Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClientFactory.CreateClient("oidc")) { RequireHttps = !new Uri(connection.Authority).IsLoopback });
        return await manager.GetConfigurationAsync(cancellationToken);
    }

    private static string SafeReturnTo(string? returnTo) =>
        !string.IsNullOrEmpty(returnTo) && returnTo.StartsWith('/') && !returnTo.StartsWith("//", StringComparison.Ordinal) ? returnTo : "/";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SsoConnectionSummary Map(SsoConnection c) => new(
        c.Id, c.Code, c.DisplayName, c.Authority, c.ClientId, c.ClientSecretEnc is not null, c.Scopes, c.EmailDomains, c.JitProvisioning, c.GroupClaim,
        JsonSerializer.Deserialize<Dictionary<string, Guid>>(c.GroupRoleMap, Json) ?? new Dictionary<string, Guid>(StringComparer.Ordinal), c.IsActive);
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Quicker.Kernel.Time;

namespace Quicker.Identity.Security;

public sealed class AuthOptions
{
    public const string SectionName = "Quicker:Auth";

    /// <summary>Base64 256-bit HMAC key for access tokens. Rotate by adding a new key first, then removing the old one.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string? PreviousSigningKey { get; set; }

    /// <summary>Base64 256-bit AES key for secrets at rest (TOTP seeds, client secrets).</summary>
    public string SecretProtectionKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "quicker";

    public string Audience { get; set; } = "quicker-api";

    /// <summary>Public origin of the web app, used for WebAuthn relying-party id/origin and OIDC redirects.</summary>
    public string PublicOrigin { get; set; } = "http://localhost:5173";

    public string ApiOrigin { get; set; } = "http://localhost:8080";

    public bool BreachedPasswordCheck { get; set; }
}

/// <summary>Claim names used in Quicker access tokens.</summary>
public static class QuickerClaims
{
    public const string Tenant = "tid";
    public const string Membership = "mid";
    public const string User = "sub";
    public const string Epoch = "pep";
    public const string AuthTime = "auth_time";
    public const string Amr = "amr";
    public const string Session = "sid";
    public const string TokenKind = "tkn";
    public const string Language = "lang";
    public const string Operator = "op";
}

/// <summary>Issues and validates HS256 access tokens, MFA challenge tokens and step-up tokens.</summary>
public sealed class JwtIssuer(AuthOptions options, IClock clock)
{
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public SymmetricSecurityKey Key { get; } = new(Convert.FromBase64String(options.SigningKey));

    public SymmetricSecurityKey? PreviousKey { get; } = string.IsNullOrEmpty(options.PreviousSigningKey) ? null : new SymmetricSecurityKey(Convert.FromBase64String(options.PreviousSigningKey));

    public string IssueAccessToken(Guid userId, Guid tenantId, Guid membershipId, Guid sessionId, long epoch, DateTimeOffset authTime, string amr, string language, bool isOperator, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new(QuickerClaims.User, userId.ToString()),
            new(QuickerClaims.Tenant, tenantId.ToString()),
            new(QuickerClaims.Membership, membershipId.ToString()),
            new(QuickerClaims.Session, sessionId.ToString()),
            new(QuickerClaims.Epoch, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(QuickerClaims.AuthTime, authTime.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(QuickerClaims.Amr, amr),
            new(QuickerClaims.Language, language),
            new(QuickerClaims.TokenKind, "access"),
        };
        if (isOperator)
        {
            claims.Add(new Claim(QuickerClaims.Operator, "true", ClaimValueTypes.Boolean));
        }

        return Write(claims, lifetime);
    }

    /// <summary>Short-lived token proving the password step passed; exchanged with an MFA proof for real tokens.</summary>
    public string IssueChallengeToken(Guid userId, Guid? tenantId, string purpose, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new(QuickerClaims.User, userId.ToString()),
            new(QuickerClaims.TokenKind, purpose),
        };
        if (tenantId is { } t)
        {
            claims.Add(new Claim(QuickerClaims.Tenant, t.ToString()));
        }

        return Write(claims, lifetime);
    }

    public ClaimsPrincipal? Validate(string token, string expectedKind)
    {
        var parameters = ValidationParameters();
        try
        {
            var principal = _handler.ValidateToken(token, parameters, out _);
            return principal.FindFirst(QuickerClaims.TokenKind)?.Value == expectedKind ? principal : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKeys = PreviousKey is null ? [Key] : [Key, PreviousKey],
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        // Lifetimes are judged by the injected clock (ADR-0011), never the ambient one, so tests can travel in time.
        LifetimeValidator = (notBefore, expires, _, parameters) =>
        {
            var now = clock.UtcNow.UtcDateTime;
            return (notBefore is null || notBefore <= now.Add(parameters.ClockSkew))
                && (expires is null || expires > now.Subtract(parameters.ClockSkew));
        },
        NameClaimType = QuickerClaims.User,
        RoleClaimType = "role",
    };

    private string Write(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var now = clock.UtcNow.UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now.Add(lifetime),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return _handler.WriteToken(token);
    }
}

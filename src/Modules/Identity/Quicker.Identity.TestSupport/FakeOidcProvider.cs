using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Quicker.Identity.TestSupport;

/// <summary>
/// A minimal OpenID Connect provider on a loopback port: discovery, JWKS, an authorize endpoint that immediately
/// redirects back with a code, and a token endpoint that issues an RS256 id_token carrying the configured user.
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RsaSecurityKey _key;
    private readonly Dictionary<string, (string Nonce, string RedirectUri)> _codes = new(StringComparer.Ordinal);

    private FakeOidcProvider(WebApplication app, RsaSecurityKey key, string issuer)
    {
        _app = app;
        _key = key;
        Issuer = issuer;
    }

    public string Issuer { get; }

    public string ClientId { get; } = "quicker-test-client";

    public string ClientSecret { get; } = "test-secret";

    public string UserEmail { get; set; } = "sso.user@example.test";

    public string UserName { get; set; } = "SSO User";

    public IReadOnlyList<string> Groups { get; set; } = [];

    public int TokenRequests { get; private set; }

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var port = FreePort();
        var issuer = $"http://127.0.0.1:{port}";
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(issuer);
        var app = builder.Build();
        var provider = new FakeOidcProvider(app, key, issuer);

        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer,
            authorization_endpoint = issuer + "/authorize",
            token_endpoint = issuer + "/token",
            jwks_uri = issuer + "/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            code_challenge_methods_supported = new[] { "S256" },
        }));

        app.MapGet("/jwks", () =>
        {
            var parameters = rsa.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", kid = key.KeyId, alg = "RS256", n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) },
                },
            });
        });

        app.MapGet("/authorize", (HttpRequest request) =>
        {
            var redirect = request.Query["redirect_uri"].ToString();
            var state = request.Query["state"].ToString();
            var nonce = request.Query["nonce"].ToString();
            var code = Guid.NewGuid().ToString("N");
            provider._codes[code] = (nonce, redirect);
            return Results.Redirect($"{redirect}?code={code}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            provider.TokenRequests++;
            var code = form["code"].ToString();
            if (!provider._codes.Remove(code, out var entry) || form["client_id"] != provider.ClientId || form["client_secret"] != provider.ClientSecret)
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            var claims = new List<Claim>
            {
                new("sub", "sso-subject-1"),
                new("email", provider.UserEmail),
                new("email_verified", "true"),
                new("name", provider.UserName),
                new("nonce", entry.Nonce),
            };
            claims.AddRange(provider.Groups.Select(static g => new Claim("groups", g)));
            var token = new JwtSecurityToken(issuer, provider.ClientId, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            var idToken = new JwtSecurityTokenHandler().WriteToken(token);
            return Results.Json(new { access_token = "at-" + code, token_type = "Bearer", expires_in = 600, id_token = idToken });
        });

        await app.StartAsync();
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

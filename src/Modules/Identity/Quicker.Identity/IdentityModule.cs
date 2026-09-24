using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quicker.Identity.Application;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Time;
using Quicker.Persistence.EntityFramework;
using Quicker.Web;

namespace Quicker.Identity;

public static class IdentityModule
{
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IdentityRegistration.RegisterPermissions();

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.AddSingleton(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.SigningKey) || string.IsNullOrWhiteSpace(options.SecretProtectionKey))
            {
                throw new InvalidOperationException("Quicker:Auth:SigningKey and Quicker:Auth:SecretProtectionKey must be configured (base64, 32 bytes each).");
            }

            return options;
        });
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton(static sp => new JwtIssuer(sp.GetRequiredService<AuthOptions>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton(static sp => new TotpProvider(sp.GetRequiredService<IClock>()));
        services.AddSingleton(static sp => new SecretProtector(sp.GetRequiredService<AuthOptions>().SecretProtectionKey));
        services.AddSingleton<Quicker.Identity.Contracts.ISecretProtector>(static sp => sp.GetRequiredService<SecretProtector>());
        services.AddScoped<Quicker.Identity.Contracts.IMemberDirectory, MemberDirectory>();
        services.AddSingleton<IFido2>(static sp =>
        {
            var options = sp.GetRequiredService<AuthOptions>();
            var origin = new Uri(options.PublicOrigin);
            return new Fido2(new Fido2Configuration
            {
                RPID = origin.Host,
                RPName = "Quicker",
                Origins = new HashSet<string>(StringComparer.Ordinal) { origin.GetLeftPart(UriPartial.Authority) },
                TimestampDriftTolerance = 300_000,
            }, null!);
        });
        services.AddMemoryCache();
        // Providers are addresses a workspace administrator types in: discovery, keys and the token call stay on the public
        // internet unless the operator allows an internal provider (Quicker:Outbound:AllowedPrivateNetworks).
        services.AddHttpClient("oidc").RestrictToPublicNetworks();
        services.AddHttpClient<HibpBreachedPasswordChecker>();
        services.AddSingleton<IBreachedPasswordChecker>(static sp =>
            sp.GetRequiredService<AuthOptions>().BreachedPasswordCheck ? sp.GetRequiredService<HibpBreachedPasswordChecker>() : new NoBreachCheck());
        services.AddScoped<PasswordPolicy>();

        services.AddModuleDbContext<IdentityDbContext>();

        services.AddScoped<AuthService>();
        services.AddScoped<AccountService>();
        services.AddScoped<WebAuthnService>();
        services.AddScoped<RoleService>();
        services.AddScoped<Quicker.Identity.Contracts.IRoleDirectory>(static sp => sp.GetRequiredService<RoleService>());
        services.AddScoped<ApiKeyService>();
        services.AddScoped<SsoService>();
        services.AddScoped<PrincipalResolver>();
        services.AddScoped<IPrincipalResolver>(static sp => sp.GetRequiredService<PrincipalResolver>());
        services.AddScoped<IStepUpPolicy>(static sp => sp.GetRequiredService<PrincipalResolver>());

        services.AddAuthentication(BearerScheme)
            .AddJwtBearer(BearerScheme, static _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, static _ => { });
        services.AddOptions<JwtBearerOptions>(BearerScheme).Configure<JwtIssuer>(static (options, issuer) =>
        {
            options.TokenValidationParameters = issuer.ValidationParameters();
            options.MapInboundClaims = false;
        });
        services.AddAuthorization(static options =>
        {
            options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(BearerScheme, ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }
}

using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quicker.Kernel.Results;

namespace Quicker.Web;

/// <summary>Request budgets per minute (section <c>Quicker:Api:RateLimit</c>); sliding windows, in-process per node.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Quicker:Api:RateLimit";

    public bool Enabled { get; set; } = true;

    /// <summary>Authenticated traffic, per principal (user or API key) within its tenant.</summary>
    public int PerPrincipalPerMinute { get; set; } = 600;

    /// <summary>Anonymous traffic per client address.</summary>
    public int AnonymousPerMinute { get; set; } = 120;

    /// <summary>Anonymous calls to /api/v1/auth/* per client address: sign-in, sign-up, reset (credential stuffing guard).</summary>
    public int AuthPerMinute { get; set; } = 20;
}

public static class RateLimiting
{
    public const string PolicyName = "api";
    private const int Segments = 6;

    public static IServiceCollection AddQuickerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.AddRateLimiter(static limiter =>
        {
            limiter.OnRejected = static async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)((wait.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                var problem = ApiProblems.From(Error.RateLimited("rate.limited", "Too many requests; slow down and retry after the indicated seconds.").WithWhy(("retryAfterSeconds", retryAfter)));
                await problem.ExecuteAsync(context.HttpContext);
            };
            limiter.AddPolicy(PolicyName, static context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("off");
                }

                var user = context.User;
                var tenant = user.FindFirst("tid")?.Value;
                if (user.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(tenant))
                {
                    var principal = user.FindFirst("mid")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "?";
                    return Sliding("principal:" + tenant + ":" + principal, options.PerPrincipalPerMinute);
                }

                var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var isAuth = context.Request.Path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase);
                return Sliding((isAuth ? "auth:" : "anon:") + address, isAuth ? options.AuthPerMinute : options.AnonymousPerMinute);
            });
        });
        return services;
    }

    private static RateLimitPartition<string> Sliding(string key, int permitsPerMinute) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = Segments,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    /// <summary>Applies the API policy to every endpoint of the group.</summary>
    public static TBuilder RequireQuickerRateLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireRateLimiting(PolicyName);
    }
}

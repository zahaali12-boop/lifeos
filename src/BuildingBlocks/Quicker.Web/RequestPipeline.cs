using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Observability;
using Quicker.Persistence;

namespace Quicker.Web;

/// <summary>Scoped holder for the request's principal; set by the unit-of-work middleware.</summary>
public sealed class CurrentPrincipal : ICurrentPrincipal
{
    public Principal? Principal { get; set; }

    public Principal Required => Principal ?? throw new UnauthorizedAccessException("This endpoint requires an authenticated principal.");
}

/// <summary>Resolves the principal for the request from the authenticated claims (implemented by the Identity module).</summary>
public interface IPrincipalResolver
{
    Task<Principal?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken);
}

/// <summary>
/// Opens one unit of work per API request before endpoint parameters are bound (so module DbContexts can attach to it),
/// resolves the principal from the authenticated claims, and disposes the unit of work at the end (rolling back
/// anything the <see cref="UnitOfWorkFilter"/> did not commit). Order in the pipeline: authentication → authorization
/// → this middleware → endpoint (with the commit filter).
/// </summary>
public sealed class UnitOfWorkMiddleware(IUnitOfWorkFactory factory, ITenantContextAccessor tenantContext) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var accessor = context.RequestServices.GetRequiredService<IUnitOfWorkAccessor>();
        var current = context.RequestServices.GetRequiredService<CurrentPrincipal>();
        var requestId = Activity.Current?.Id ?? context.TraceIdentifier;
        var language = LanguageFrom(context);

        var claims = context.User;
        TenantContext tenant;
        if (claims.Identity?.IsAuthenticated == true && Guid.TryParse(claims.FindFirst("tid")?.Value, out var tenantId) && tenantId != Guid.Empty)
        {
            var userId = Guid.TryParse(claims.FindFirst("sub")?.Value, out var parsedUser) ? parsedUser : Guid.Empty;
            var membershipId = Guid.TryParse(claims.FindFirst("mid")?.Value, out var parsedMembership) ? parsedMembership : Guid.Empty;
            var actorType = string.Equals(claims.FindFirst("tkn")?.Value, "api_key", StringComparison.Ordinal) ? TenantContext.ActorApiKey : TenantContext.ActorUser;
            tenant = new TenantContext(new TenantId(tenantId), new MembershipId(membershipId), new UserId(userId), actorType, claims.FindFirst("sub")?.Value ?? string.Empty, requestId, context.Request.Headers["X-Correlation-Id"].FirstOrDefault(), language);
        }
        else
        {
            tenant = TenantContext.Anonymous(requestId, language);
        }

        var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
        tenant = tenant with
        {
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
        };

        Telemetry.TagCurrent(tenant.TenantId.Value, tenant.UserId?.Value, tenant.MembershipId?.Value, tenant.ActorType, requestId);
        Telemetry.TenantRequests.Add(1, new KeyValuePair<string, object?>(Telemetry.TenantTag, tenant.IsAnonymous ? "anonymous" : tenant.TenantId.Value.ToString()), new KeyValuePair<string, object?>(Telemetry.ActorTag, tenant.ActorType));

        await using var unitOfWork = await factory.BeginAsync(tenant, cancellationToken: context.RequestAborted);
        accessor.Set(unitOfWork);
        using var scope = tenantContext.Use(tenant);

        if (!tenant.IsAnonymous)
        {
            var resolver = context.RequestServices.GetRequiredService<IPrincipalResolver>();
            current.Principal = await resolver.ResolveAsync(context, context.RequestAborted);
            if (current.Principal is null)
            {
                await unitOfWork.RollbackAsync(context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { type = "https://docs.quicker.app/errors/auth.invalid_session", title = "Authentication required", status = 401, code = "auth.invalid_session" }, context.RequestAborted);
                return;
            }

            // Name the actor for the audit log (the token only carries ids).
            await unitOfWork.SwitchTenantAsync(tenant.TenantId, tenant.UserId, tenant.MembershipId, current.Principal.Email, context.RequestAborted);
        }

        await next(context);
    }

    private static string LanguageFrom(HttpContext http)
    {
        var header = http.Request.Headers.AcceptLanguage.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return "en";
        }

        var first = header.Split(',')[0].Split(';')[0].Trim();
        return first.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
    }
}

/// <summary>
/// Commits the request's unit of work when the endpoint returns a success result (before the response is written),
/// rolls back otherwise. Exceptions roll back and propagate to the problem-details handler.
/// </summary>
public sealed class UnitOfWorkFilter(IUnitOfWorkAccessor accessor) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            if (accessor.HasCurrent)
            {
                await accessor.Current.RollbackAsync(CancellationToken.None);
            }

            throw;
        }

        if (!accessor.HasCurrent)
        {
            return result;
        }

        if (IsSuccess(result) || accessor.Current.CommitOnFailure)
        {
            await accessor.Current.CommitAsync(CancellationToken.None);
        }
        else
        {
            await accessor.Current.RollbackAsync(CancellationToken.None);
        }

        return result;
    }

    // A typed union (Results<Ok<T>, ProblemHttpResult>) carries no status itself: look at the result it wraps,
    // otherwise a refused request would commit what the service wrote before it refused.
    private static bool IsSuccess(object? result) => result switch
    {
        INestedHttpResult nested => IsSuccess(nested.Result),
        IStatusCodeHttpResult { StatusCode: { } status } => status < 400,
        _ => true,
    };
}

/// <summary>Requires an authenticated principal holding a permission; 401 when anonymous, 403 with the missing key otherwise.</summary>
public sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var current = context.HttpContext.RequestServices.GetRequiredService<CurrentPrincipal>();
        if (current.Principal is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "auth.required" });
        }

        if (!current.Principal.Has(permission))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: $"Missing permission '{permission}'.", extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "auth.forbidden",
                ["why"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["permission"] = permission },
            });
        }

        return await next(context);
    }
}

/// <summary>Requires a recent authentication (step-up) for sensitive actions, per tenant policy.</summary>
public sealed class RequireRecentAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var current = context.HttpContext.RequestServices.GetRequiredService<CurrentPrincipal>();
        var policy = context.HttpContext.RequestServices.GetRequiredService<IStepUpPolicy>();
        if (current.Principal is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "auth.required" });
        }

        if (!await policy.IsRecentAsync(current.Principal, context.HttpContext.RequestAborted))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Recent authentication required", detail: "Confirm your identity again to perform this action.", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "auth.step_up_required" });
        }

        return await next(context);
    }
}

/// <summary>Requires a platform operator (support staff); 403 for ordinary members, including owners.</summary>
public sealed class RequireOperatorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var current = context.HttpContext.RequestServices.GetRequiredService<CurrentPrincipal>();
        if (current.Principal is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "auth.required" });
        }

        if (!current.Principal.IsPlatformOperator)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "This action is reserved for platform operators.", extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "auth.operator_required" });
        }

        return await next(context);
    }
}

/// <summary>Decides whether the principal's authentication is recent enough for sensitive actions (tenant policy).</summary>
public interface IStepUpPolicy
{
    Task<bool> IsRecentAsync(Principal principal, CancellationToken cancellationToken);
}

public static class EndpointConventions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new RequirePermissionFilter(permission));
        return builder;
    }

    public static TBuilder RequireRecentAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new RequireRecentAuthFilter());
        return builder;
    }

    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new RequireOperatorFilter());
        return builder;
    }

    public static IServiceCollection AddQuickerWebCore(this IServiceCollection services)
    {
        services.AddScoped<IUnitOfWorkAccessor, UnitOfWorkAccessor>();
        services.AddScoped<IUnitOfWork>(static sp => sp.GetRequiredService<IUnitOfWorkAccessor>().Current);
        services.AddScoped<CurrentPrincipal>();
        services.AddScoped<ICurrentPrincipal>(static sp => sp.GetRequiredService<CurrentPrincipal>());
        services.AddScoped<UnitOfWorkMiddleware>();
        services.AddScoped<UnitOfWorkFilter>();
        return services;
    }

    /// <summary>Applies the unit-of-work middleware to API paths only (health and static assets stay outside).</summary>
    public static IApplicationBuilder UseQuickerUnitOfWork(this IApplicationBuilder app, PathString apiPrefix)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWhen(context => context.Request.Path.StartsWithSegments(apiPrefix, StringComparison.OrdinalIgnoreCase), static branch => branch.UseMiddleware<UnitOfWorkMiddleware>());
    }
}

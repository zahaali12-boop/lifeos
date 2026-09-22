using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Quicker.Web;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Quicker:Api:Idempotency";

    public const string Header = "Idempotency-Key";

    /// <summary>Reject POST/PUT/PATCH/DELETE on /api without an Idempotency-Key (ADR-0012). Off until every client sends one.</summary>
    public bool Required { get; set; }

    public int RetentionHours { get; set; } = 24;

    /// <summary>Responses larger than this are not stored; a replay then re-executes (still safe for reads, documented for writes).</summary>
    public int MaxStoredBodyBytes { get; set; } = 1_048_576;
}

/// <summary>
/// Idempotent mutations (ADR-0009): the key, principal, tenant and a hash of the request are stored with the final
/// response for 24 hours. A replay with the same key and hash returns the stored response; the same key with a
/// different request is refused; concurrent replays serialise on an advisory lock so exactly one executes.
/// </summary>
public sealed class IdempotencyMiddleware(NpgsqlDataSource dataSource, IOptions<IdempotencyOptions> options) : IMiddleware
{
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var current = context.RequestServices.GetRequiredService<CurrentPrincipal>();
        var key = context.Request.Headers[IdempotencyOptions.Header].FirstOrDefault()?.Trim();
        if (!Mutating.Contains(context.Request.Method) || current.Principal is null)
        {
            await next(context);
            return;
        }

        if (string.IsNullOrEmpty(key))
        {
            if (options.Value.Required)
            {
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency.key_required", $"Mutating requests need an {IdempotencyOptions.Header} header.");
                return;
            }

            await next(context);
            return;
        }

        if (key.Length > 200)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency.key_invalid", "Idempotency keys are at most 200 characters.");
            return;
        }

        var principal = $"{current.Principal.ActorType}:{current.Principal.MembershipId.Value:N}";
        var tenant = current.Principal.TenantId.Value;
        var requestHash = await HashRequestAsync(context.Request, context.RequestAborted);

        // The lock lives on this connection for the whole request: a concurrent replay waits, then sees the stored response.
        await using var connection = await dataSource.OpenConnectionAsync(context.RequestAborted);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(hashtext(@scope))", new { scope = $"idem:{tenant:N}:{principal}:{key}" }, cancellationToken: context.RequestAborted));
        try
        {
            var stored = await connection.QuerySingleOrDefaultAsync<StoredResponse>(new CommandDefinition(
                "SELECT request_hash AS RequestHash, response_status AS ResponseStatus, response_body::text AS ResponseBody, response_content_type AS ResponseContentType FROM ops.idempotency_keys WHERE tenant_id = @tenant AND principal = @principal AND key = @key AND expires_at > now()",
                new { tenant, principal, key }, cancellationToken: context.RequestAborted));
            if (stored is not null)
            {
                if (!stored.RequestHash.AsSpan().SequenceEqual(requestHash))
                {
                    await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency.key_reused", "This Idempotency-Key was already used for a different request.");
                    return;
                }

                context.Response.StatusCode = stored.ResponseStatus ?? StatusCodes.Status200OK;
                context.Response.Headers["Idempotent-Replayed"] = "true";
                if (stored.ResponseBody is not null)
                {
                    context.Response.ContentType = stored.ResponseContentType ?? "application/json";
                    await context.Response.WriteAsync(stored.ResponseBody, context.RequestAborted);
                }

                return;
            }

            var original = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = original;
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);

            // Server errors are not stored: the client should retry and get a real execution.
            if (context.Response.StatusCode < 500 && buffer.Length <= options.Value.MaxStoredBodyBytes)
            {
                var body = buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                var isJson = context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ops.idempotency_keys (tenant_id, principal, key, request_hash, response_status, response_body, response_content_type, expires_at)
                    VALUES (@tenant, @principal, @key, @hash, @status, CASE WHEN @isJson THEN @body::jsonb ELSE to_jsonb(@body::text) END, @contentType, now() + make_interval(hours => @hours))
                    ON CONFLICT (tenant_id, principal, key) DO UPDATE SET request_hash = EXCLUDED.request_hash, response_status = EXCLUDED.response_status, response_body = EXCLUDED.response_body, response_content_type = EXCLUDED.response_content_type, expires_at = EXCLUDED.expires_at, created_at = now()
                    """, new { tenant, principal, key, hash = requestHash, status = context.Response.StatusCode, body, isJson = isJson && body is not null, contentType = context.Response.ContentType, hours = options.Value.RetentionHours }, cancellationToken: CancellationToken.None));
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock_all()");
        }
    }

    private static async Task<byte[]> HashRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return sha.GetHashAndReset();
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { type = $"https://docs.quicker.app/errors/{code}", title = status == 400 ? "Bad request" : "Request cannot be processed", status, detail, code }, context.RequestAborted);
    }

    private sealed class StoredResponse
    {
        public byte[] RequestHash { get; set; } = [];

        public int? ResponseStatus { get; set; }

        public string? ResponseBody { get; set; }

        public string? ResponseContentType { get; set; }
    }
}

public static class IdempotencyRegistration
{
    public static IServiceCollection AddQuickerIdempotency(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));
        services.AddScoped<IdempotencyMiddleware>();
        return services;
    }

    /// <summary>Applies idempotency to API paths; must follow <see cref="EndpointConventions.UseQuickerUnitOfWork"/> so the principal is known.</summary>
    public static IApplicationBuilder UseQuickerIdempotency(this IApplicationBuilder app, PathString apiPrefix)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWhen(context => context.Request.Path.StartsWithSegments(apiPrefix, StringComparison.OrdinalIgnoreCase), static branch => branch.UseMiddleware<IdempotencyMiddleware>());
    }
}

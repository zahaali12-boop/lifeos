using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Quicker.Kernel.Results;

namespace Quicker.Web;

/// <summary>
/// GET responses on the API get two things from this middleware (slice 1.9): field selection
/// (<c>?fields=id,code,name</c> keeps only those properties on the object, on each item of an array, or on the
/// items of a page envelope; <c>id</c> is always kept) and a validator (<c>ETag</c> from the body's SHA-256 unless
/// the endpoint set one, and <c>304 Not Modified</c> when <c>If-None-Match</c> carries it).
/// </summary>
public sealed class ResponseShapingMiddleware : IMiddleware
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        var fields = context.Request.Query["fields"].ToString();
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            context.Response.Body = original;
            var bytes = buffer.ToArray();
            if (context.Response.StatusCode != StatusCodes.Status200OK || !IsJson(context.Response.ContentType))
            {
                await Copy(context, original, bytes);
                return;
            }

            if (!string.IsNullOrWhiteSpace(fields))
            {
                bytes = Select(bytes, fields);
            }

            var etag = context.Response.Headers.ETag.ToString();
            if (string.IsNullOrEmpty(etag))
            {
                etag = ETags.ForBytes(bytes);
                context.Response.Headers.ETag = etag;
            }

            if (ETags.Matches(context.Request.Headers.IfNoneMatch, etag))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = 0;
                context.Response.Headers.Remove(HeaderNames.ContentType);
                return;
            }

            await Copy(context, original, bytes);
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    private static async Task Copy(HttpContext context, Stream original, byte[] bytes)
    {
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted);
    }

    private static bool IsJson(string? contentType) => contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static byte[] Select(byte[] body, string fields)
    {
        var keep = new HashSet<string>(fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase) { "id" };
        var node = JsonNode.Parse(body);
        switch (node)
        {
            case JsonObject page when page["items"] is JsonArray items && page.ContainsKey("nextCursor"):
                foreach (var item in items)
                {
                    Trim(item, keep);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Trim(item, keep);
                }

                break;
            case JsonObject single:
                Trim(single, keep);
                break;
            default:
                return body;
        }

        return JsonSerializer.SerializeToUtf8Bytes(node, Json);
    }

    private static void Trim(JsonNode? node, HashSet<string> keep)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var name in obj.Select(static p => p.Key).Where(name => !keep.Contains(name)).ToList())
        {
            obj.Remove(name);
        }
    }
}

public static class ETags
{
    /// <summary>A weak validator from the body bytes.</summary>
    public static string ForBytes(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return "W/\"" + Convert.ToHexStringLower(SHA256.HashData(body))[..32] + "\"";
    }

    /// <summary>A weak validator from a resource's last change: what endpoints with <c>If-Match</c> support set on GET and check on write.</summary>
    public static string ForVersion(DateTimeOffset updatedAt) => "W/\"v" + updatedAt.UtcTicks.ToString("x", CultureInfo.InvariantCulture) + "\"";

    public static bool Matches(Microsoft.Extensions.Primitives.StringValues header, string etag)
    {
        foreach (var raw in header)
        {
            if (raw is null)
            {
                continue;
            }

            foreach (var candidate in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (candidate == "*" || string.Equals(Weak(candidate), Weak(etag), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;

        static string Weak(string value) => value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;
    }

    /// <summary>412 problem when the request's <c>If-Match</c> does not name the resource's current version; success when absent or matching.</summary>
    public static Result RequireMatch(HttpRequest request, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.IfMatch;
        if (header.Count == 0 || Matches(header, ForVersion(updatedAt)))
        {
            return Result.Success();
        }

        return new Error("precondition.failed", "The resource changed since it was read; reload and apply the change again.", Kind: ErrorKind.PreconditionFailed)
            .WithWhy(("currentETag", ForVersion(updatedAt)));
    }
}

public static class ResponseShapingRegistration
{
    public static IServiceCollection AddQuickerResponseShaping(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ResponseShapingMiddleware>();
        return services;
    }

    public static IApplicationBuilder UseQuickerResponseShaping(this IApplicationBuilder app, PathString apiPrefix)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWhen(context => context.Request.Path.StartsWithSegments(apiPrefix, StringComparison.OrdinalIgnoreCase), static branch => branch.UseMiddleware<ResponseShapingMiddleware>());
    }
}

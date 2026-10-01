using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Quicker.Kernel.Results;

namespace Quicker.Web;

/// <summary>
/// RFC 9457 problem details with a stable <c>code</c> and a structured <c>why</c> for business blocks (ADR-0012).
/// The HTTP status comes from the error's kind, never from parsing the code.
/// </summary>
public static class ApiProblems
{
    public static ProblemHttpResult From(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = StatusFor(error.Kind);
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = error.Code };
        if (error.Why is not null)
        {
            extensions["why"] = error.Why;
        }

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            title: TitleFor(status),
            type: $"https://docs.quicker.app/errors/{error.Code}",
            extensions: extensions);
    }

    // Typed variants: the union return types carry the success schema into the OpenAPI contract (slice 1.9).

    public static Results<Ok<T>, ProblemHttpResult> Ok<T>(Result<T> result) => result.IsSuccess ? TypedResults.Ok(result.Value) : From(result.Error!);

    public static Results<Created<T>, ProblemHttpResult> Created<T>(Result<T> result, Func<T, string> location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return result.IsSuccess ? TypedResults.Created(location(result.Value), result.Value) : From(result.Error!);
    }

    public static Results<Accepted<T>, ProblemHttpResult> Accepted<T>(Result<T> result, Func<T, string?> location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return result.IsSuccess ? TypedResults.Accepted(location(result.Value), result.Value) : From(result.Error!);
    }

    public static Results<NoContent, ProblemHttpResult> NoContent(Result result) => result.IsSuccess ? TypedResults.NoContent() : From(result.Error!);

    /// <summary>200 with the value, or the standard 404 problem when it is null.</summary>
    public static Results<Ok<T>, ProblemHttpResult> Found<T>(T? value, string entity, object id) where T : class => value is null ? From(Error.NotFound(entity, id)) : TypedResults.Ok(value);

    public static IResult From<T>(Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : From(result.Error!);
    }

    public static IResult From(Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess() : From(result.Error!);
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Locked => StatusCodes.Status423Locked,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static string TitleFor(int status) => status switch
    {
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        412 => "Precondition failed",
        422 => "Request cannot be processed",
        423 => "Locked",
        429 => "Too many requests",
        _ => "Error",
    };
}

/// <summary>Turns a <see cref="ProblemDetails"/> into the Error shape used inside the system (for tests and clients).</summary>
public sealed record ApiError(string Code, string? Detail, IReadOnlyDictionary<string, object?>? Why);

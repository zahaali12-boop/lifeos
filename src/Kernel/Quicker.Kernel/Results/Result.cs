namespace Quicker.Kernel.Results;

/// <summary>
/// A domain error with a stable machine-readable code (surfaced as the API problem `code`), a human message and a
/// structured "why" payload for business blocks (rule, threshold, actual values).
/// </summary>
public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, object?>? Why = null)
{
    public static Error Validation(string code, string message) => new(code, message);

    public static Error NotFound(string entity, object id) => new($"{entity}.not_found", $"{entity} '{id}' was not found.");

    public static Error Conflict(string code, string message) => new(code, message);

    public static Error Forbidden(string code, string message) => new(code, message);

    public Error WithWhy(params (string Key, object? Value)[] facts)
    {
        var why = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Why is not null)
        {
            foreach (var (key, value) in Why)
            {
                why[key] = value;
            }
        }

        foreach (var (key, value) in facts)
        {
            why[key] = value;
        }

        return this with { Why = why };
    }
}

public readonly struct Result
{
    private Result(Error? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public Error? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(Error error) => Failure(error);

    public static Result FromError(Error error) => Failure(error);
}

public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public Error? Error { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {Error!.Code}");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public static Result<T> FromValue(T value) => Success(value);

    public static Result<T> FromError(Error error) => Failure(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result<TOut>.Success(map(Value)) : Result<TOut>.Failure(Error!);
    }

    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(Value) : Result<TOut>.Failure(Error!);
    }
}
